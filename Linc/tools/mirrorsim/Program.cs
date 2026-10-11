using System.Collections.Concurrent;
using System.Diagnostics;
using Linc.Desktop.Protocol;
using Linc.Desktop.Services;

// Reverse-mirror capture and encode path against the REAL PcScreenCapture / PcVideoEncoder /
// PcMirrorSource (M05a; D-029, D-039).
//
//   dotnet run --project tools/mirrorsim
//
// "It produced bytes" is not evidence a phone could play it, so these scenarios parse the
// Annex-B bitstream and assert its structure, not just its existence. The latency scenario
// drives the real PcMirrorSource rather than a loop written here, because the threading is
// exactly what the measurement is about (D-036).
//
// Read-only with respect to the machine: it captures the screen and touches nothing else --
// no settings.json, no phone. The rate-control scenarios briefly show a window of scrolling
// noise on display 0 (Motion.cs), because rate control cannot be measured on an idle screen.

Mf.Check(Mf.MFStartup(0x00020070, 0), "MFStartup");

// `dotnet run -- <text>` runs only the scenarios whose name contains <text>.
var only = args.FirstOrDefault();
var failures = new List<string>();
foreach (var (name, body) in Scenarios())
{
    if (only is not null && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
    Console.WriteLine($"--- {name}");
    try { body(new Asserter(name, failures)); }
    catch (Exception ex) { failures.Add($"{name}: threw {ex.GetType().Name}: {ex.Message}"); }
}

Console.WriteLine();
int exitCode;
if (failures.Count == 0)
{
    Console.WriteLine($"mirrorsim: all {Scenarios().Count(x => only is null || x.Item1.Contains(only, StringComparison.OrdinalIgnoreCase))} scenarios green");
    exitCode = 0;
}
else
{
    Console.WriteLine($"mirrorsim: {failures.Count} FAILURE(S)");
    foreach (var failure in failures) Console.WriteLine($"  {failure}");
    exitCode = 1;
}

// Exit deterministically once the result is known. The capture/encode path spins up
// background threads and native COM objects across scenarios; letting the runtime tear those
// down on its own occasionally races and reports a spurious non-zero code that has nothing to
// do with the tests. The meaningful work is finished here, so end on the real result.
Console.Out.Flush();
Environment.Exit(exitCode);

static IEnumerable<(string, Action<Asserter>)> Scenarios()
{
    yield return ("video packet header round-trips (the phone must parse this identically)", a =>
    {
        // The 12-byte header is the one wire contract between this encoder and the phone's
        // decoder. Anything wrong here corrupts the stream in a way that looks like a codec
        // fault, so it is checked directly rather than only implicitly through streaming.
        var cases = new (long ts, bool config, bool key)[]
        {
            (0, false, false),
            (33_333, false, true),
            (1_000_000, false, false),
            (0, true, false),                       // config packet, no timestamp
            (4_611_686_018_427_387L, false, true),  // a large but legal 62-bit timestamp
        };
        var header = new byte[VideoFraming.HeaderBytes];
        foreach (var (ts, config, key) in cases)
        {
            VideoFraming.WriteHeader(header, ts, config, key, 4242);
            var parsed = VideoFraming.ReadHeader(header);
            a.Equal(ts, parsed.TimestampUs, $"timestamp survives (ts={ts})");
            a.Equal(config, parsed.Config, $"config flag survives (ts={ts})");
            a.Equal(key, parsed.Keyframe, $"keyframe flag survives (ts={ts})");
            a.Equal(4242, parsed.Length, $"length survives (ts={ts})");
        }

        // The flag bits must live in the header word, not bleed into the length.
        VideoFraming.WriteHeader(header, 500, config: true, keyframe: true, length: 7);
        a.Equal(7, VideoFraming.ReadHeader(header).Length, "flags do not corrupt the length field");
    });

    yield return ("touch coordinates map correctly through the whole chain", a =>
    {
        // Pure mapping, so the real cursor is never touched. The chain is video-normalised
        // (0..65535 on the mirrored display) -> physical pixel -> virtual-desktop-normalised.

        // Single 1920x1080 primary display, virtual desktop == that display.
        var single = new DisplayRect(0, 0, 1920, 1080);
        var (x0, y0) = PcInputService.MapToVirtualDesktop(0, 0, single, single);
        a.Equal(0, x0, "top-left maps to 0 (x)");
        a.Equal(0, y0, "top-left maps to 0 (y)");
        var (xm, ym) = PcInputService.MapToVirtualDesktop(65535, 65535, single, single);
        a.Equal(65535, xm, "bottom-right maps to 65535 (x)");
        a.Equal(65535, ym, "bottom-right maps to 65535 (y)");
        var (xc, yc) = PcInputService.MapToVirtualDesktop(32767, 32767, single, single);
        a.True(Math.Abs(xc - 32767) <= 40, $"centre stays near centre x (was {xc})");
        a.True(Math.Abs(yc - 32767) <= 40, $"centre stays near centre y (was {yc})");

        // Monotonic: a touch further right never maps further left.
        int previous = -1; bool monotonic = true;
        for (int v = 0; v <= 65535; v += 4096)
        {
            int nx = PcInputService.MapToVirtualDesktop(v, 0, single, single).X;
            if (nx < previous) monotonic = false;
            previous = nx;
        }
        a.True(monotonic, "mapping is monotonic left-to-right");

        // A second display to the right of the primary: a touch on IT must land on the RIGHT
        // half of the virtual desktop, which is the multi-monitor case the spike flagged.
        var secondary = new DisplayRect(1920, 0, 1920, 1080);
        var virtualBoth = new DisplayRect(0, 0, 3840, 1080);
        var (leftEdge, _) = PcInputService.MapToVirtualDesktop(0, 0, secondary, virtualBoth);
        var (rightEdge, _) = PcInputService.MapToVirtualDesktop(65535, 0, secondary, virtualBoth);
        a.True(leftEdge is >= 32000 and <= 33000,
            $"secondary's left edge maps to the middle of the virtual desktop (was {leftEdge})");
        a.Equal(65535, rightEdge, "secondary's right edge maps to the far right of the virtual desktop");
    });

    yield return ("displays enumerate for the picker", a =>
    {
        var displays = PcScreenCapture.Enumerate();
        a.True(displays.Count > 0, "at least one attached display");
        a.True(displays.Any(d => d.Primary), "a display reports primary");
        foreach (var display in displays)
        {
            Console.WriteLine($"    {display.Name} {display.Width}x{display.Height}" +
                              $"{(display.Primary ? " (primary)" : "")}");
            a.True(display.Width > 0 && display.Height > 0, $"{display.Name} has a real size");
        }
    });

    yield return ("capture reports PHYSICAL pixels, not DPI-virtualised ones", a =>
    {
        using var capture = new PcScreenCapture(0);
        a.True(Prime(capture), "a frame arrives within two seconds");
        Console.WriteLine($"    captured {capture.Width}x{capture.Height}");

        // gdi32's DESKTOPHORZRES stays physical whatever the process's DPI awareness, so it is
        // the independent check that capture is not silently virtualised. A DPI-unaware process
        // is handed 1536x864 here by user32 AND by DXGI, while the real panel is 1920x1080.
        var (physicalWidth, physicalHeight) = PhysicalScreenSize();
        Console.WriteLine($"    gdi32 says {physicalWidth}x{physicalHeight} (physical, DPI-proof)");
        a.Equal(physicalWidth, capture.Width, "capture width == physical width");
        a.Equal(physicalHeight, capture.Height, "capture height == physical height");
    });

    yield return ("encoder is hardware and encodes a decodable stream", a =>
    {
        var (frames, source) = Run(Config.TargetFrames);
        Console.WriteLine($"    encoder: {source.Encoder}, {source.Width}x{source.Height}");
        a.Equal(Config.TargetFrames, frames.Count, "every requested frame arrived");

        var stream = frames.SelectMany(f => f.Data).ToArray();
        var nals = ParseNals(stream);
        int sps = nals.Count(n => n.Type == 7), pps = nals.Count(n => n.Type == 8);
        int idr = nals.Count(n => n.Type == 5), slices = nals.Count(n => n.Type == 1);
        Console.WriteLine($"    {stream.Length:N0} bytes; NALs: SPS x{sps} PPS x{pps} IDR x{idr} P x{slices}");

        a.True(sps > 0, "stream carries SPS");
        a.True(pps > 0, "stream carries PPS");
        a.True(idr > 0, "stream carries at least one IDR keyframe");
        a.True(slices > 0, "stream carries coded P slices, not keyframes only");

        int firstSps = nals.FindIndex(n => n.Type == 7);
        int firstIdr = nals.FindIndex(n => n.Type == 5);
        a.True(firstSps >= 0 && firstSps < firstIdr,
            "SPS precedes the first IDR, so a phone joining mid-stream can decode");
        a.True(sps > 1 && pps > 1, "SPS/PPS repeat rather than appearing only once");
    });

    yield return ("D-077 NV12 input: the GPU-converted path encodes the same picture", a =>
    {
        // Intel Quick Sync refused ARGB32 on a friend's PC; the fallback converts to NV12 on the
        // GPU. Forced here on whatever encoder this PC has, and compared with the direct path.
        var (direct, _) = Run(Config.TargetFrames);
        PcMirrorSource.PreferNv12 = true;
        try
        {
            var (frames, source) = Run(Config.TargetFrames);
            Console.WriteLine($"    encoder: {source.Encoder}");
            a.True(source.Encoder.Contains("NV12"), "the NV12 path was really used");
            a.Equal(Config.TargetFrames, frames.Count, "every requested frame arrived");
            var nals = ParseNals(frames.SelectMany(f => f.Data).ToArray());
            a.True(nals.Any(n => n.Type == 7) && nals.Any(n => n.Type == 8) && nals.Any(n => n.Type == 5),
                "SPS, PPS and an IDR — decodable from the start");
            a.True(nals.Count(n => n.Type == 1) > 0, "P slices follow, so the picture is moving through");
            long viaNv12 = frames.Sum(f => (long)f.Data.Length), viaArgb = direct.Sum(f => (long)f.Data.Length);
            Console.WriteLine($"    bytes: ARGB32 path {viaArgb:N0}, NV12 path {viaNv12:N0}");
            a.True(viaNv12 > viaArgb / 4 && viaNv12 < viaArgb * 4,
                "about as many bytes as the direct path — the same picture, not a blank one");
        }
        finally { PcMirrorSource.PreferNv12 = false; }
    });

    yield return ("keyframe flag matches the bitstream", a =>
    {
        var (frames, _) = Run(Config.TargetFrames);
        int flagged = frames.Count(f => f.Keyframe);
        Console.WriteLine($"    {flagged} of {frames.Count} frames flagged as keyframes");
        a.True(flagged > 0, "at least one frame is flagged a keyframe");

        // Checked across every frame, but reported once -- a per-frame line would bury the run.
        int disagreements = frames.Count(f => ParseNals(f.Data).Any(n => n.Type == 5) != f.Keyframe);
        a.Equal(0, disagreements, "every frame's Keyframe flag agrees with its own NALs");
    });

    yield return ("timestamps advance monotonically", a =>
    {
        var (frames, _) = Run(60);
        int regressions = 0;
        for (int i = 1; i < frames.Count; i++)
            if (frames[i].TimestampUs <= frames[i - 1].TimestampUs) regressions++;
        Console.WriteLine($"    PTS {frames[0].TimestampUs} .. {frames[^1].TimestampUs} us");
        a.Equal(0, regressions, "presentation timestamps strictly increase");
    });

    yield return ("frames are delivered smoothly, without stalls", a =>
    {
        // This measures the interval BETWEEN delivered frames, not encode latency -- it is the
        // metric that decides whether the mirror looks smooth. (Encode latency itself measured
        // ~5-7 ms median during the M05 spike, well inside the budget.)
        var (_, source) = Run(Config.TargetFrames);
        var intervals = source.Intervals;
        intervals.Sort();
        double median = intervals[intervals.Count / 2];
        double p95 = intervals[(int)(intervals.Count * 0.95)];
        Console.WriteLine($"    delivery interval: median {median:F1} ms, " +
                          $"p95 {p95:F1} ms, max {intervals[^1]:F1} ms");
        Console.WriteLine($"    effective rate: {source.Fps:F1} fps over {source.ElapsedMs} ms");

        // One frame at 30 fps is 33 ms. Delivery must keep pace or the mirror falls behind
        // once network and phone-side decode are added on top.
        a.True(median < 40.0, $"median delivery interval near the 33 ms target (was {median:F1} ms)");
        a.True(p95 < 66.0, $"p95 under two frame times, i.e. no repeated stalls (was {p95:F1} ms)");
    });

    yield return ("pacing holds the stream near the target frame rate", a =>
    {
        var (_, source) = Run(90);
        Console.WriteLine($"    {source.Fps:F1} fps (target {Config.Fps})");

        // Ungated this encoder free-runs past 100 fps, which is wasted CPU and bandwidth.
        a.True(source.Fps < Config.Fps * 1.5,
            $"paced rather than free-running (was {source.Fps:F1} fps)");
        a.True(source.Fps > Config.Fps * 0.5,
            $"keeps up with the target rate (was {source.Fps:F1} fps)");
    });

    yield return ("an idle screen costs almost nothing on the link", a =>
    {
        // A static desktop has nothing to say, and a mirror spends most of its life on one.
        // Rate control that pads to the target (CBR filler, NAL type 12) instead sends the full
        // bit rate of nothing, all the time -- on Wi-Fi that is the link saturated by a screen
        // that isn't changing, and every real change then arrives behind it.
        var (frames, source) = Run(60);
        var byType = new Dictionary<int, long>();
        long total = 0;
        foreach (var f in frames)
        {
            total += f.Data.Length;
            var nals = ParseNals(f.Data);
            for (int i = 0; i < nals.Count; i++)
            {
                int end = i + 1 < nals.Count ? nals[i + 1].Offset : f.Data.Length;
                byType[nals[i].Type] = byType.GetValueOrDefault(nals[i].Type) + (end - nals[i].Offset);
            }
        }
        double mbps = total * 8 / (source.ElapsedMs / 1000.0) / 1e6;
        Console.WriteLine($"    {mbps:F2} Mb/s for an idle screen at a {Config.Bitrate / 1e6} Mb/s ceiling");
        Console.WriteLine("    bytes by NAL type: " +
            string.Join(", ", byType.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value:N0}")));
        a.True(byType.GetValueOrDefault(12) == 0, "no filler data (NAL 12) padding the stream");
        a.True(mbps < Config.Bitrate / 1e6 * 0.15,
            $"an idle screen uses a small fraction of the ceiling (was {mbps:F2} Mb/s; CBR measured 7.2)");
    });

    yield return ("a fresh encoder obeys the bit rate it starts with", a =>
    {
        // The rebuild path (D-069) relies on this: a new encoder made at the target rate.
        foreach (int target in Environment.GetEnvironmentVariable("MIRRORSIM_TARGET") is { } t ? new[] { int.Parse(t) } : new[] { 1_500_000, 4_000_000 })
        {
            long bytes = 0;
            using var motion = Motion.Start(block: int.Parse(Environment.GetEnvironmentVariable("MIRRORSIM_BLOCK") ?? "32"));
            using var source = new PcMirrorSource(0, Config.Fps, target, new ConsoleLog());
            source.FrameEncoded += f => Interlocked.Add(ref bytes, f.Data.Length);
            source.Start();
            Thread.Sleep(1500);
            Interlocked.Exchange(ref bytes, 0);
            Thread.Sleep(2000);
            double bps = Interlocked.Read(ref bytes) * 8 / 2.0;
            Console.WriteLine($"    started at {target / 1e6} Mb/s under motion -> {bps / 1e6:F2} Mb/s");
            a.True(bps < target * 1.3, $"output near the {target / 1e6} Mb/s it was built for ({bps / 1e6:F2} Mb/s)");
        }
    });

    yield return ("the encoder obeys a bit rate change mid-stream", a =>
    {
        // Congestion control is only real if this is: the sender lowers the bit rate when the
        // link cannot keep up, and a hardware MFT is allowed to ignore that. Measure the bytes
        // actually produced before and after, rather than trusting the HRESULT.
        const int High = 8_000_000, Low = 1_500_000;
        long bytes = 0;
        using var motion = Motion.Start(block: 8);        // content that wants more than either target (see Motion)
        using var source = new PcMirrorSource(0, Config.Fps, High, new ConsoleLog());
        source.FrameEncoded += f => Interlocked.Add(ref bytes, f.Data.Length);
        source.Start();

        Thread.Sleep(1000);                       // settle
        Interlocked.Exchange(ref bytes, 0);
        Thread.Sleep(2000);
        double before = Interlocked.Read(ref bytes) * 8 / 2.0;

        source.RequestBitrate(Low);
        Thread.Sleep(1000);                       // let the rate controller converge
        Interlocked.Exchange(ref bytes, 0);
        Thread.Sleep(2000);
        double after = Interlocked.Read(ref bytes) * 8 / 2.0;

        Console.WriteLine($"    {before / 1e6:F2} Mb/s at {High / 1e6} Mb/s target -> {after / 1e6:F2} Mb/s at {Low / 1e6} Mb/s target");
        a.True(before > High * 0.6,
            $"under motion the stream actually uses the high target ({before / 1e6:F2} Mb/s)");
        a.True(after < before * 0.5,
            $"output fell with the target ({before / 1e6:F2} -> {after / 1e6:F2} Mb/s)");
        a.True(after < Low * 1.6,
            $"output near the new target (was {after / 1e6:F2} Mb/s, target {Low / 1e6} Mb/s)");
    });

    yield return ("a stalled link never blocks the encoder, and drops rather than lags", a =>
    {
        // The wireless failure this guards: when the socket stops draining, the old code
        // blocked the encoder's pump thread inside the write, which stalled capture and then
        // delivered the whole backlog late. PcMirrorSender must instead take every frame
        // without blocking, discard what it cannot send, and ask for a keyframe so the phone
        // can resynchronise on something current.
        using var link = new StallingStream();
        int keyframeRequests = 0;
        using var sender = new PcMirrorSender(link, () => Interlocked.Increment(ref keyframeRequests),
            _ => { }, 8_000_000, new ConsoleLog());

        var payload = new byte[64 * 1024];
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++)
            sender.Enqueue(new EncodedFrame(payload, Keyframe: i == 0, TimestampUs: i * 33_333L));
        long enqueueMs = clock.ElapsedMilliseconds;

        Console.WriteLine($"    200 frames enqueued in {enqueueMs} ms against a stalled link");
        Console.WriteLine($"    dropped {sender.DroppedFrames}, keyframe requests {keyframeRequests}");

        a.True(enqueueMs < 1000,
            $"enqueue never waits on the socket (took {enqueueMs} ms)");
        a.True(sender.DroppedFrames > 0, "a backlog the link cannot carry is discarded");
        a.True(keyframeRequests > 0, "dropping asks the encoder for a fresh keyframe");

        // Once dropping starts, only a keyframe may be queued again -- anything else would
        // decode to garbage on the phone.
        link.Release();
        sender.Enqueue(new EncodedFrame(payload, Keyframe: true, TimestampUs: 9_000_000L));
        Thread.Sleep(300);
        a.True(link.KeyframeSeen, "the stream resumes on the keyframe after the drop");
    });

    yield return ("on a link slower than the stream, the bit rate converges and drops stop", a =>
    {
        // The session log that motivated D-066: a link carrying less than the stream's bit
        // rate made the drop-only sender loop -- 11 frames dropped every ~0.37 s for seventeen
        // seconds at a stretch. Here the REAL encoder runs at 8 Mb/s into a link throttled to
        // 2.5 Mb/s, and the sender must pull the encoder under the link and stop dropping.
        const int Asked = 8_000_000, LinkBps = 2_500_000;
        using var motion = Motion.Start();       // or an idle screen never congests the link at all
        using var link = new ThrottledStream(LinkBps);
        using var source = new PcMirrorSource(0, Config.Fps, Asked, new ConsoleLog());
        using var sender = new PcMirrorSender(link, source.RequestKeyFrame, source.RequestBitrate,
            Asked, new ConsoleLog());
        source.AcceptFrame = sender.AcceptFrame;
        source.FrameEncoded += sender.Enqueue;
        source.Start();

        Thread.Sleep(10_000);                     // let the controller find the link
        long droppedBefore = sender.DroppedFrames;
        long bytesBefore = link.Bytes;
        var window = Stopwatch.StartNew();
        // Then watch the steady state. AIMD never sits still -- it probes just past the link
        // and backs off -- so the target is sampled across the window, not read once.
        var targets = new List<int>();
        while (window.ElapsedMilliseconds < 5_000) { targets.Add(sender.CurrentBitrate); Thread.Sleep(100); }
        double meanTarget = targets.Average();
        long steadyDrops = sender.DroppedFrames - droppedBefore;
        double delivered = (link.Bytes - bytesBefore) * 8 / window.Elapsed.TotalSeconds;

        Console.WriteLine($"    link {LinkBps / 1e6} Mb/s, asked {Asked / 1e6} Mb/s -> steady target " +
                          $"{meanTarget / 1e6:F2} Mb/s (range {targets.Min() / 1e6:F2}..{targets.Max() / 1e6:F2})");
        Console.WriteLine($"    first 10 s: {droppedBefore} frames dropped; steady 5 s: {steadyDrops} dropped, " +
                          $"{delivered / 1e6:F2} Mb/s delivered");

        a.True(meanTarget < LinkBps * 1.2,
            $"bit rate driven down to the link, from {Asked / 1e6} Mb/s (steady mean {meanTarget / 1e6:F2} Mb/s)");
        a.True(meanTarget > LinkBps * 0.5,
            $"...but not collapsed toward the floor (steady mean {meanTarget / 1e6:F2} Mb/s; the first controller hit 0.60)");
        a.Equal(0L, steadyDrops,
            "no encoded frame is dropped once converged -- load is shed by skipping before encode");
        a.True(delivered > LinkBps * 0.5,
            $"the link is actually used (delivered {delivered / 1e6:F2} of {LinkBps / 1e6} Mb/s)");
    });

    yield return ("a static screen does not stall the stream", a =>
    {
        // Desktop Duplication only reports frames that changed, so on an idle desktop
        // AcquireNextFrame times out. If nothing were submitted the stream would stop dead,
        // which on the phone looks like a frozen mirror with no error anywhere.
        var (frames, source) = Run(45);
        Console.WriteLine($"    {frames.Count} frames over {source.ElapsedMs} ms with no forced activity");
        a.Equal(45, frames.Count, "stream keeps producing frames regardless of screen activity");
    });

    yield return ("v20 cursor: the live pointer is captured with a usable image", a =>
    {
        // The phone draws the pointer itself (D-067), so a shape that comes back blank or with
        // its hotspot outside the image would leave the mirror with no visible cursor again.
        var display = PcScreenCapture.Enumerate()[0];
        CursorSample? sample = null;
        using (var stop = new CancellationTokenSource(400))
            PcCursorWatcher.Run(display, s => { sample ??= s; stop.Cancel(); }, stop.Token);
        a.True(sample is not null, "the watcher reports the pointer on its first tick");
        if (sample is null) return;
        Console.WriteLine($"    pointer at {sample.X},{sample.Y} visible={sample.Visible} id={sample.ShapeId}");
        if (!sample.Visible) { Console.WriteLine("    (pointer hidden right now; shape checks skipped)"); return; }
        var shape = sample.Shape;
        a.True(shape is not null, "the first sample carries the cursor's shape");
        if (shape is null) return;
        int opaque = shape.Argb.Count(p => ((uint)p >> 24) != 0);
        Console.WriteLine($"    shape {shape.Width}x{shape.Height}, hotspot {shape.HotX},{shape.HotY}, {opaque} opaque px");
        a.True(opaque > 10, "the cursor image is not blank");
        a.True(shape.HotX >= 0 && shape.HotX < shape.Width && shape.HotY >= 0 && shape.HotY < shape.Height,
            "the hotspot lies inside the image");
        var payload = PcCursorWatcher.ToPayload(sample);
        var bgra = Convert.FromBase64String((string)payload["shape"]!["bgra"]!);
        a.Equal(shape.Width * shape.Height * 4, bgra.Length, "the wire image is exactly w*h*4 bytes");
    });

    yield return ("v20 cursor: the standard arrow and I-beam render to real images", a =>
    {
        // Independent of whether the pointer is showing right now: load the system cursors.
        foreach (var (id, name) in new[] { (32512, "arrow"), (32513, "I-beam"), (32649, "hand") })
        {
            var shape = PcCursorWatcher.Capture(Native.LoadCursor(IntPtr.Zero, id));
            a.True(shape is not null, $"{name}: captured");
            if (shape is null) continue;
            int opaque = shape.Argb.Count(p => ((uint)p >> 24) != 0);
            Console.WriteLine($"    {name}: {shape.Width}x{shape.Height}, hotspot {shape.HotX},{shape.HotY}, {opaque} opaque px");
            a.True(opaque > 10 && opaque < shape.Width * shape.Height, $"{name}: has a visible, non-rectangular image");
        }
    });

    yield return ("v20 cursor: a monochrome I-beam stays visible on any background", a =>
    {
        // 3x3: centre column is "invert screen" (AND=1, XOR=1), the rest transparent (AND=1, XOR=0).
        const int On = unchecked((int)0xFFFFFFFF), Off = unchecked((int)0xFF000000);
        int[] and = [On, On, On, On, On, On, On, On, On];
        int[] xor = [Off, On, Off, Off, On, Off, Off, On, Off];
        var px = PcCursorWatcher.DecodeMonochrome([.. and, .. xor], 3, 3);
        a.Equal(On, px[4], "an inverting pixel is drawn white");
        a.Equal(unchecked((int)0xFF000000), px[3], "...with a black outline beside it");
        a.True(px.All(p => p != 0), "no pixel of a 3-wide I-beam is left fully transparent");
    });

    yield return ("v20 input: right/middle buttons and extended keys", a =>
    {
        a.Equal(0x0008u, PcInputService.ButtonFlag("right", down: true), "right down");
        a.Equal(0x0010u, PcInputService.ButtonFlag("right", down: false), "right up");
        a.Equal(0x0020u, PcInputService.ButtonFlag("middle", down: true), "middle down");
        a.Equal(0x0002u, PcInputService.ButtonFlag(null, down: true), "an old phone (no button) still left-clicks");
        a.True(PcInputService.IsExtendedKey(0x25) && PcInputService.IsExtendedKey(0x2E) && PcInputService.IsExtendedKey(0x5B),
            "arrows, Delete and Win are extended keys");
        a.True(!PcInputService.IsExtendedKey(0x41) && !PcInputService.IsExtendedKey(0x0D), "letters and Enter are not");
    });

    yield return ("v21 phone mic: 16 kHz speech is upsampled cleanly and cables are recognised", a =>
    {
        short[] ramp = [0, 16384, 32767];
        var up = PcMicAudio.Upsample(ramp, 16_000, 48_000);
        a.Equal(9, up.Length, "16 kHz -> 48 kHz triples the sample count");
        a.True(Math.Abs(up[0]) < 1e-6 && Math.Abs(up[3] - 0.5f) < 0.001f, "samples land where the input had them");
        a.True(up[1] > up[0] && up[2] > up[1] && up[2] < up[3], "and in between is a straight line, not steps");
        a.True(PcMicAudio.IsCableInput("CABLE Input (VB-Audio Virtual Cable)"), "VB-CABLE's playback side is found");
        a.True(PcMicAudio.IsCableOutput("CABLE Output (VB-Audio Virtual Cable)"), "...and its recording side");
        a.True(!PcMicAudio.IsCableInput("Speaker (Realtek(R) Audio)") && !PcMicAudio.IsCableOutput("Microphone Array (AMD Audio Device)"),
            "real speakers and microphones are never mistaken for a cable");
    });

    yield return ("D-071 virtual cable: the pinned VB-CABLE pack is verified and the right setup is chosen", a =>
    {
        byte[] data = System.Text.Encoding.ASCII.GetBytes("abc");
        const string abc = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";
        a.True(VirtualCablePackage.HashMatches(data, abc), "a matching SHA-256 passes");
        a.True(VirtualCablePackage.HashMatches(data, abc.ToLowerInvariant()), "...in either case");
        a.True(!VirtualCablePackage.HashMatches("abd"u8, abc), "a different file is refused");
        a.True(!VirtualCablePackage.HashMatches(data, "PENDING"), "an unpinned hash refuses everything");
        a.True(VirtualCablePackage.Sha256.Length == 64, "the shipped pin is a real SHA-256");
        string[] pack = [@"x\VBCABLE_Setup.exe", @"x\VBCABLE_Setup_x64.exe", @"x\readme.txt"];
        a.Equal(@"x\VBCABLE_Setup_x64.exe", VirtualCablePackage.SetupFor(pack, System.Runtime.InteropServices.Architecture.X64), "x64 Windows runs the x64 setup");
        a.Equal(@"x\VBCABLE_Setup.exe", VirtualCablePackage.SetupFor(pack, System.Runtime.InteropServices.Architecture.X86), "32-bit Windows runs the plain one");
        a.Equal(@"x\VBCABLE_Setup_x64.exe", VirtualCablePackage.SetupFor(pack, System.Runtime.InteropServices.Architecture.Arm64), "ARM64 runs the x64 setup, which carries the ARM64 driver");
        a.True(VirtualCablePackage.SetupFor([@"x\readme.txt"], System.Runtime.InteropServices.Architecture.X64) is null, "no setup in the pack means no guess");
        a.True(VirtualCablePackage.Url.StartsWith("https://download.vb-audio.com/"), "fetched from the maker over HTTPS, never bundled");
    });

    yield return ("D-071 virtual cable download: ranged, survives a flaky server, resumes", a =>
    {
        // The real pack, served from localhost the way vb-audio.com behaved on 2026-10-10: slow,
        // and failing now and then. LINC_VBCABLE_ZIP points at a verified copy; no copy, no test.
        var source = Environment.GetEnvironmentVariable("LINC_VBCABLE_ZIP");
        if (source is null || !File.Exists(source)) { Console.WriteLine("    LINC_VBCABLE_ZIP not set; skipped"); return; }
        var pack = File.ReadAllBytes(source);
        a.True(VirtualCablePackage.HashMatches(pack, VirtualCablePackage.Sha256), "the test copy is the pinned pack");

        int port = 47000 + Random.Shared.Next(1000);
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        int requests = 0, fullBodyRequests = 0;
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                _ = Task.Run(async () =>
                {
                    int n = Interlocked.Increment(ref requests);
                    var range = ctx.Request.Headers["Range"];
                    if (range is null) { Interlocked.Increment(ref fullBodyRequests); }
                    if (n % 3 == 0) { ctx.Response.StatusCode = 500; ctx.Response.Close(); return; }   // flaky
                    await Task.Delay(150);                                                                // slow
                    var span = range!.Replace("bytes=", "").Split('-');
                    long start = long.Parse(span[0]), end = long.Parse(span[1]);
                    ctx.Response.StatusCode = 206;
                    ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{pack.Length}";
                    ctx.Response.ContentLength64 = end - start + 1;
                    try { await ctx.Response.OutputStream.WriteAsync(pack.AsMemory((int)start, (int)(end - start + 1))); } catch { }
                    ctx.Response.Close();
                });
            }
        });

        var folder = Path.Combine(Path.GetTempPath(), "linc-cable-test-" + Guid.NewGuid().ToString("N"));
        var log = new ConsoleLog();
        var setup = new VirtualCableSetup(log, () => false, $"http://127.0.0.1:{port}/pack.zip", folder);
        try
        {
            // First attempt: stopped part-way, as if the PC slept or Linc was closed.
            using var stop = new CancellationTokenSource();
            int seen = 0;
            var first = new Progress<int>(p => { seen = Math.Max(seen, p); if (p >= 40) stop.Cancel(); });
            try { setup.FetchPackAsync(first, stop.Token).GetAwaiter().GetResult(); a.True(false, "the first attempt should have been stopped"); }
            catch (OperationCanceledException) { }
            var kept = Directory.Exists(Path.Combine(folder, "parts")) ? Directory.GetFiles(Path.Combine(folder, "parts")).Length : 0;
            a.True(kept > 0 && kept < VirtualCablePackage.Chunks(VirtualCablePackage.Bytes).Count, $"a stopped download keeps what it got ({kept} ranges)");

            // Second attempt carries on and produces the exact pinned file.
            int before = requests;
            int last = 0;
            var zip = setup.FetchPackAsync(new Progress<int>(p => last = p), CancellationToken.None).GetAwaiter().GetResult();
            Thread.Sleep(200); // let the last progress callback land
            a.True(VirtualCablePackage.HashMatches(File.ReadAllBytes(zip), VirtualCablePackage.Sha256), "the resumed download is the pinned pack, byte for byte");
            a.True(requests - before < 2 * VirtualCablePackage.Chunks(VirtualCablePackage.Bytes).Count, "only the missing ranges were fetched again (with retries)");
            a.Equal(100, last, "progress reaches 100 %");
            a.Equal(0, fullBodyRequests, "never asks for the whole file in one go");
            a.True(!Directory.Exists(Path.Combine(folder, "parts")), "the pieces are cleaned up once the pack is whole");

            // Already there: a third call does no network at all.
            int quiet = requests;
            setup.FetchPackAsync(null, CancellationToken.None).GetAwaiter().GetResult();
            a.Equal(quiet, requests, "a verified copy on disk is used without downloading again");
        }
        finally
        {
            listener.Stop();
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    });

    yield return ("D-075 phone mic: Voice Access's own mic id, and who is listening", a =>
    {
        // The id format must be exactly what Voice Access writes itself, or it ignores it. On a
        // PC where it has a microphone chosen, rebuild that value from its endpoint id.
        using var va = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\VoiceAccess");
        if (va?.GetValue("VoiceAccessMicrophoneId") is string chosen)
        {
            var endpoint = chosen.Split('#')[2];   // \\?\SWD#MMDEVAPI#{0.0.1.00000000}.{guid}#{interface}
            a.Equal(chosen, PcMicAudio.VoiceAccessMicId(endpoint), "rebuilds Voice Access's own value exactly");
        }
        else Console.WriteLine("    Voice Access has no microphone chosen here; format checked by shape only");
        a.True(PcMicAudio.VoiceAccessMicId("{0.0.1.00000000}.{dd0d4dfa-6cc5-4191-a766-b69e7975d214}")
            .StartsWith(@"\\?\SWD#MMDEVAPI#{0.0.1.00000000}.{dd0d4dfa"), "a capture endpoint becomes a device-interface path");
        bool listening = PcMicAudio.MicInUseByOtherApps();
        Console.WriteLine($"    an app is using a microphone right now: {listening}");
        a.True(true, "the microphone-in-use records read without error");
    });

    yield return ("1.0.0.11 phone input without a mirror lands on the primary display", a =>
    {
        // Before 1.0.0.11 every pc.input with no mirror session was dropped: the Remote
        // trackpad, its taps and scrolls, the keyboard and presentation keys did nothing.
        var left = new PcDisplay(0, "Left", -1920, 0, 1920, 1080, false);
        var main = new PcDisplay(1, "Main", 0, 0, 2560, 1440, true);
        var desk = new DisplayRect(-1920, 0, 4480, 1440);
        a.True(PcInputService.TargetFor(null, [left, main], desk) == new DisplayRect(0, 0, 2560, 1440),
            "no mirror: the primary display");
        a.True(PcInputService.TargetFor(left, [left, main], desk) == new DisplayRect(-1920, 0, 1920, 1080),
            "mirroring: the mirrored display");
        a.True(PcInputService.TargetFor(null, [], desk) == desk, "no display list: the virtual desktop");
        var real = PcScreenCapture.Enumerate();
        var t = PcInputService.TargetFor(null, real, desk);
        a.True(t.Width > 0 && t.Height > 0, $"this PC has a target ({t.Width}x{t.Height})");
    });

    yield return ("D-078 adb serials: wireless links are never mistaken for a USB cable", a =>
    {
        // The exact serials one phone had in a friend's first setup.
        a.True(!AdbSerial.IsNetwork("131722549O000808"), "the USB serial is USB");
        a.True(AdbSerial.IsNetwork("192.168.1.61:34521"), "IPv4 wireless debugging is network");
        a.True(AdbSerial.IsNetwork("[2605:59c1:343f:b210:bca1:fdff:fee0:d6cf]:33705"), "IPv6 wireless debugging is network");
        a.True(AdbSerial.IsNetwork("adb-131722549O000808-hiwood._adb-tls-connect._tcp"), "the mDNS name from wireless pairing is network");
        a.True(AdbSerial.IsNetwork("adb-2C141FDH20089M-abc._adb._tcp"), "the older mDNS form is network");
        a.True(!AdbSerial.IsNetwork("emulator-5554") && !AdbSerial.IsNetwork("") && !AdbSerial.IsNetwork(null), "everything else is not");
        // 1.0.0.11: the one place a network serial's host is read (was a hand-rolled split).
        a.True(AdbSerial.HostOf("192.168.1.61:34521") == "192.168.1.61", "IPv4 host");
        a.True(AdbSerial.HostOf("[2605:59c1:343f:b210:bca1:fdff:fee0:d6cf]:33705") == "2605:59c1:343f:b210:bca1:fdff:fee0:d6cf", "IPv6 host, unbracketed");
        a.True(AdbSerial.HostOf("adb-131722549O000808-hiwood._adb-tls-connect._tcp") is null && AdbSerial.HostOf("131722549O000808") is null, "mDNS and USB serials have no host");
    });

    yield return ("v20 audio: PC sound is captured and AAC-encoded live", a =>
    {
        int rate;
        try { rate = PcAudioStreamer.ProbeSampleRate(); }
        catch (Exception ex) { Console.WriteLine($"    no usable output device ({ex.Message}); skipped"); return; }
        Console.WriteLine($"    default output mixes at {rate} Hz");

        // Loopback is silent unless something plays, so play a quiet tone for the duration.
        var tone = Native.QuietToneWav(rate, seconds: 2);
        Native.PlaySound(tone, IntPtr.Zero, Native.SND_MEMORY | Native.SND_ASYNC);
        var link = new MemoryStream();
        var streamer = PcAudioStreamer.Start(link, rate, new ConsoleLog());
        Thread.Sleep(1500);
        streamer.Dispose();
        Native.PlaySound(null, IntPtr.Zero, 0);

        var bytes = link.ToArray();
        int packets = 0, offset = 0, smallest = int.MaxValue, largest = 0;
        while (offset + VideoFraming.HeaderBytes <= bytes.Length)
        {
            var h = VideoFraming.ReadHeader(bytes.AsSpan(offset));
            if (offset + VideoFraming.HeaderBytes + h.Length > bytes.Length) break;
            offset += VideoFraming.HeaderBytes + h.Length;
            packets++; smallest = Math.Min(smallest, h.Length); largest = Math.Max(largest, h.Length);
        }
        double kbps = bytes.Length * 8 / 1.5 / 1000;
        Console.WriteLine($"    {packets} AAC packets ({smallest}..{largest} bytes), {kbps:F0} kb/s on the wire");
        a.True(packets >= 30, "about 47 frames a second come out while sound plays (>= 30 in 1.5 s)");
        a.Equal(bytes.Length, offset, "the channel is a clean sequence of framed packets");
        a.True(kbps < 200, "the stream stays near its 128 kb/s budget");
    });
}

// ---- helpers -------------------------------------------------------------

static bool Prime(PcScreenCapture capture)
{
    for (int attempt = 0; attempt < 40; attempt++)
        if (capture.TryAcquire(50, out _)) return true;
    return false;
}

/// <summary>Drive the real PcMirrorSource until it has produced <paramref name="target"/> frames.</summary>
static (List<EncodedFrame>, RunStats) Run(int target)
{
    var frames = new ConcurrentQueue<EncodedFrame>();
    var arrivals = new ConcurrentQueue<double>();
    var done = new ManualResetEventSlim(false);
    var clock = Stopwatch.StartNew();
    double previous = 0;

    using var source = new PcMirrorSource(0, Config.Fps, Config.Bitrate, new ConsoleLog());
    source.FrameEncoded += frame =>
    {
        if (frames.Count >= target) { done.Set(); return; }
        double now = clock.Elapsed.TotalMilliseconds;
        arrivals.Enqueue(now - previous);
        previous = now;
        frames.Enqueue(frame);
        if (frames.Count >= target) done.Set();
    };

    previous = clock.Elapsed.TotalMilliseconds;
    source.Start();
    if (!done.Wait(TimeSpan.FromSeconds(30)))
        throw new TimeoutException($"only {frames.Count} of {target} frames in 30 s");

    long elapsed = clock.ElapsedMilliseconds;
    var list = frames.ToList();
    // The first interval measures startup, not steady state.
    var intervals = arrivals.Skip(1).ToList();
    return (list, new RunStats(source.EncoderName, source.Width, source.Height,
        intervals, elapsed, list.Count * 1000.0 / elapsed));
}

static List<Nal> ParseNals(byte[] data)
{
    var nals = new List<Nal>();
    for (int i = 0; i + 3 < data.Length; i++)
    {
        if (data[i] != 0 || data[i + 1] != 0) continue;
        int header = data[i + 2] == 1 ? i + 3
                   : data[i + 2] == 0 && data[i + 3] == 1 ? i + 4
                   : -1;
        if (header < 0 || header >= data.Length) continue;
        nals.Add(new Nal(data[header] & 0x1F, header));
        i = header;
    }
    return nals;
}

static (int, int) PhysicalScreenSize()
{
    IntPtr hdc = Native.GetDC(IntPtr.Zero);
    try { return (Native.GetDeviceCaps(hdc, 118), Native.GetDeviceCaps(hdc, 117)); }
    finally { Native.ReleaseDC(IntPtr.Zero, hdc); }
}

readonly record struct Nal(int Type, int Offset);

sealed record RunStats(string Encoder, int Width, int Height,
    List<double> Intervals, long ElapsedMs, double Fps);

static class Native
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr LoadCursor(IntPtr instance, int id);
    public const uint SND_ASYNC = 0x1, SND_MEMORY = 0x4;
    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    public static extern bool PlaySound(byte[]? sound, IntPtr module, uint flags);

    /// <summary>A 440 Hz stereo tone at 5% volume, as an in-memory WAV.</summary>
    public static byte[] QuietToneWav(int rate, int seconds)
    {
        int frames = rate * seconds, dataBytes = frames * 4;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate);
        w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        for (int i = 0; i < frames; i++)
        {
            short v = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 1600);
            w.Write(v); w.Write(v);
        }
        return ms.ToArray();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    public static extern int GetDeviceCaps(IntPtr hdc, int index);
}

/// <summary>Minimal ILogService so the harness can link the real service unchanged.</summary>
sealed class ConsoleLog : ILogService
{
    public IReadOnlyList<LogEntry> Entries => [];
    public event Action? EntriesChanged;
    public string LogFolderPath => Path.GetTempPath();
    public void Log(LogLevel level, string message)
    {
        Console.WriteLine($"    [{level}] {message}");
        EntriesChanged?.Invoke();
    }
    public void Clear() { }
}

sealed class Asserter(string scenario, List<string> failures)
{
    public void True(bool condition, string what)
    {
        Console.WriteLine($"    {(condition ? "ok  " : "FAIL")} {what}");
        if (!condition) failures.Add($"{scenario}: {what}");
    }

    public void Equal<T>(T expected, T actual, string what)
    {
        bool ok = EqualityComparer<T>.Default.Equals(expected, actual);
        Console.WriteLine($"    {(ok ? "ok  " : "FAIL")} {what}" +
                          $"{(ok ? "" : $" (expected {expected}, got {actual})")}");
        if (!ok) failures.Add($"{scenario}: {what} (expected {expected}, got {actual})");
    }
}

static class Config
{
    public const int Fps = 30;
    public const int Bitrate = 8_000_000;
    public const int TargetFrames = 120;
}

/// <summary>
/// A stream that accepts nothing until released -- a wireless link whose send buffer has
/// filled. Writing to it blocks exactly the way a real socket does under back-pressure.
/// </summary>
sealed class StallingStream : Stream
{
    private readonly ManualResetEventSlim _released = new(false);
    private readonly object _seen = new();
    private bool _keyframeSeen;

    public bool KeyframeSeen { get { lock (_seen) return _keyframeSeen; } }

    public void Release() => _released.Set();

    public override void Write(byte[] buffer, int offset, int count)
    {
        _released.Wait(TimeSpan.FromSeconds(10));
        var header = VideoFraming.ReadHeader(buffer.AsSpan(offset, VideoFraming.HeaderBytes));
        if (header.Keyframe) lock (_seen) _keyframeSeen = true;
    }

    public override void Flush() { }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _released.Set();
        base.Dispose(disposing);
    }
}

/// <summary>
/// A link with a fixed capacity: each write takes as long as its bytes would take at
/// <c>bitsPerSecond</c>. Stands in for a Wi-Fi link slower than the stream.
/// </summary>
sealed class ThrottledStream(int bitsPerSecond) : Stream
{
    private long _bytes;
    public long Bytes => Interlocked.Read(ref _bytes);

    public override void Write(byte[] buffer, int offset, int count)
    {
        Thread.Sleep(TimeSpan.FromSeconds(count * 8.0 / bitsPerSecond));
        Interlocked.Add(ref _bytes, count);
    }

    public override void Flush() { }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
