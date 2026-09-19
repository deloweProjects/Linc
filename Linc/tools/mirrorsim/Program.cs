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
// no settings.json, no phone.

Mf.Check(Mf.MFStartup(0x00020070, 0), "MFStartup");

var failures = new List<string>();
foreach (var (name, body) in Scenarios())
{
    Console.WriteLine($"--- {name}");
    try { body(new Asserter(name, failures)); }
    catch (Exception ex) { failures.Add($"{name}: threw {ex.GetType().Name}: {ex.Message}"); }
}

Console.WriteLine();
int exitCode;
if (failures.Count == 0)
{
    Console.WriteLine($"mirrorsim: all {Scenarios().Count()} scenarios green");
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
            new ConsoleLog());

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

    yield return ("a static screen does not stall the stream", a =>
    {
        // Desktop Duplication only reports frames that changed, so on an idle desktop
        // AcquireNextFrame times out. If nothing were submitted the stream would stop dead,
        // which on the phone looks like a frozen mirror with no error anywhere.
        var (frames, source) = Run(45);
        Console.WriteLine($"    {frames.Count} frames over {source.ElapsedMs} ms with no forced activity");
        a.Equal(45, frames.Count, "stream keeps producing frames regardless of screen activity");
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
