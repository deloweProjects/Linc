using Linc.Desktop.Services;

// audiosyncsim — "Play everywhere" (v26, D-096). Runs the real PlayoutEngine against simulated
// speakers that drift, buffer and hide latency the way real ones do, and measures when each
// source sample is actually heard. Pass = every speaker hears every sample within 1 ms of the
// same moment once settled, through a gap in the sound and a change of group delay.

Console.WriteLine("=== Linc Play-everywhere sync harness (audiosyncsim) ===");
var failures = new List<string>();
void Check(bool ok, string what)
{
    Console.WriteLine($"    {(ok ? "PASS" : "FAIL")}: {what}");
    if (!ok) failures.Add(what);
}

// ---- 1. the controller ---------------------------------------------------------------------
Console.WriteLine("\n[1/6] PlayoutController");
{
    var c = new PlayoutController(48_000);
    var first = c.Decide(-25_000, 0.01);
    Check(first.Action == PlayoutAction.InsertSilence && first.Frames == 1200, "unlocked + 25 ms early → 1200 frames of silence");
    var small = c.Decide(3_000, 0.01);
    Check(small.Action == PlayoutAction.Track && small.Ppm > 0, "locked + 3 ms late → play fast");
    var late = c.Decide(55_000, 0.01);
    Check(late.Action == PlayoutAction.Skip && late.Frames == 2640, "55 ms late → skip 2640 frames");
    for (int i = 0; i < 50; i++) c.Decide(-200_000_000, 0.01); // absurd input must not blow up
    Check(Math.Abs(c.Decide(-30_000, 0.01).Ppm) <= PlayoutController.MaxPpm, "ppm stays within ±0.5%");
}

// ---- 2. group delay ----------------------------------------------------------------------
Console.WriteLine("\n[2/6] GroupDelay");
{
    var g = new GroupDelay();
    Check(g.Update([("PC", 35_000), ("Phone", 112_000)], 0, 0) && g.CurrentUs == 120_000 && g.Reason == "Phone",
        "rises at once to the slowest speaker, rounded up to 10 ms");
    Check(!g.Update([("PC", 35_000), ("Phone", 30_000)], 0, 1_000_000), "doesn't fall at once");
    Check(!g.Update([("PC", 35_000), ("Phone", 30_000)], 0, 15_000_000), "still not after 14 s");
    Check(!g.Update([("PC", 100_000)], 0, 16_000_000), "a need back near the delay restarts the wait");
    Check(!g.Update([("PC", 35_000), ("Phone", 30_000)], 0, 17_000_000), "low again: the 20 s start over");
    Check(g.Update([("PC", 35_000), ("Phone", 30_000)], 0, 37_000_000) && g.CurrentUs == 60_000,
        "falls after 20 s of needing 30 ms less (floor 60 ms)");
    Check(g.Update([("BT", 230_000)], 50_000, 38_000_000) && g.CurrentUs == 280_000, "the user's margin is added");
}

// ---- 3. test sounds land where they are scheduled --------------------------------------------
Console.WriteLine("\n[3/6] TestSignal.Mix");
{
    const int rate = 48_000;
    var chirp = TestSignal.Chirp(rate);
    var click = TestSignal.Click(rate);
    var output = new float[4800 * 2];
    // Output frame 0 is heard at 1 000 000 µs; the chirp is due at 1 050 000 µs → frame 2400.
    TestSignal.Mix(output, 2, rate, 1_000_000, [new TestSound("x", 1_050_000, TestSoundKind.Chirp)], chirp, click);
    int firstNonZero = Array.FindIndex(output, v => v != 0) / 2;
    Check(firstNonZero is >= 2400 and <= 2402, $"chirp starts at frame 2400 (got {firstNonZero})");
    Check(chirp.Length == 2880 && chirp.Max() <= 0.5f, "chirp is 60 ms, peak 0.5");
}

// ---- 4. calibration bookkeeping --------------------------------------------------------------
Console.WriteLine("\n[4/6] Calibration");
{
    var schedule = Calibration.Schedule(["a", "b"], 10_000_000);
    Check(schedule.Count == 6 && schedule[1].DeviceId == "b" && schedule[1].AtUs == 10_800_000, "round-robin, 800 ms apart");
    var d = new EverywhereDeviceSettings { AutoUs = 100_000, ManualUs = 5_000 };
    Check(Calibration.Apply(d, new CalibrationResult("a", true, 42_000, 20)) && d.AutoUs == 142_000 && d.HiddenUs == 147_000,
        "a measured error adds to the hidden latency");
    Check(!Calibration.Apply(d, new CalibrationResult("a", true, 900_000, 20)) && d.AutoUs == 142_000, "an absurd result is refused");
    Check(!Calibration.Apply(d, new CalibrationResult("a", false, 1_000, 2)) && d.AutoUs == 142_000, "a failed result changes nothing");
}

// ---- 5. speakers in step -------------------------------------------------------------------
Console.WriteLine("\n[5/6] Four simulated speakers, 20 s, a 1.2 s silence and a delay change");
{
    var speakers = new[]
    {
        new SimSpeaker("PC speakers (48 kHz, +60 ppm)", 48_000, +60, bufferUs: 30_000, hiddenUs: 0),
        new SimSpeaker("Bluetooth headset on the PC (44.1 kHz, −90 ppm)", 44_100, -90, bufferUs: 40_000, hiddenUs: 180_000),
        new SimSpeaker("Monitor speakers (48 kHz, −20 ppm)", 48_000, -20, bufferUs: 25_000, hiddenUs: 12_000),
        new SimSpeaker("USB DAC (96 kHz, +150 ppm)", 96_000, +150, bufferUs: 20_000, hiddenUs: 4_000),
    };
    const int sourceRate = 48_000;
    long delayUs = 250_000;
    long sourceFrame = 0;
    long nextBlockUs = 0;
    var pending = new Queue<(long ArriveUs, SoundBlock Block)>();

    for (long t = 0; t <= 20_000_000; t += 1_000)
    {
        // The PC "captures" 1024-frame blocks in real time, except for a silence 8.0–9.2 s
        // (loopback sends nothing then); each reaches the speakers 25 ms later.
        if (t >= nextBlockUs)
        {
            bool silent = t is >= 8_000_000 and < 9_200_000;
            if (!silent)
            {
                var pcm = new short[1024 * 2];
                for (int i = 0; i < 1024; i++)
                {
                    short v = SimSpeaker.Encode(sourceFrame + i);
                    pcm[i * 2] = v; pcm[i * 2 + 1] = v;
                }
                pending.Enqueue((t + 25_000, new SoundBlock(pcm, nextBlockUs)));
            }
            sourceFrame += 1024;
            nextBlockUs += 1_000_000L * 1024 / sourceRate;
        }
        while (pending.Count > 0 && pending.Peek().ArriveUs <= t)
        {
            var block = pending.Dequeue().Block;
            foreach (var s in speakers) s.Engine.Enqueue(block, delayUs + 1_000_000);
        }
        if (t == 14_000_000) delayUs = 290_000; // a slower speaker joined: everyone moves together
        if (t % 10_000 == 0)
            foreach (var s in speakers) s.Tick(t, delayUs, sourceRate);
    }

    if (Environment.GetEnvironmentVariable("SIM_DEBUG") is not null) Debug.Series(speakers[1]) /* SIM_DEBUG=1: the 44.1 kHz speaker's error over time */;
    foreach (var s in speakers)
    {
        var settled = s.Errors.Where(e => e.HeardUs is >= 3_000_000 and < 8_000_000 or >= 10_000_000 and < 14_000_000 or >= 17_000_000)
            .Select(e => Math.Abs(e.ErrorUs)).ToList();
        double worst = settled.Count > 0 ? settled.Max() : double.MaxValue;
        Check(settled.Count > 1000 && worst < 1_000, $"{s.Name}: worst {worst / 1000:0.00} ms off once settled ({settled.Count} samples)");
        var afterGap = s.Errors.Where(e => e.HeardUs is >= 9_500_000 and < 10_000_000).Select(e => Math.Abs(e.ErrorUs)).DefaultIfEmpty(double.MaxValue).Max();
        Check(afterGap < 1_000, $"{s.Name}: first sound after the silence is on time ({afterGap / 1000:0.00} ms)");
    }
}

// ---- 6. a speaker that hides 180 ms nobody told it about, then gets calibrated -----------------
Console.WriteLine("\n[6/6] An uncalibrated Bluetooth speaker, then one calibration round");
{
    var bt = new SimSpeaker("BT", 48_000, 0, bufferUs: 30_000, hiddenUs: 180_000) { AssumedHiddenUs = 0 };
    var settings = new EverywhereDeviceSettings();
    long sourceFrame = 0, next = 0;
    for (long t = 0; t <= 6_000_000; t += 1_000)
    {
        if (t >= next)
        {
            var pcm = new short[2048];
            for (int i = 0; i < 1024; i++) { short v = SimSpeaker.Encode(sourceFrame + i); pcm[i * 2] = v; pcm[i * 2 + 1] = v; }
            bt.Engine.Enqueue(new SoundBlock(pcm, next), 2_000_000);
            sourceFrame += 1024; next += 1_000_000L * 1024 / 48_000;
        }
        if (t == 3_000_000)
        {
            // What the phone's microphone would report for a chirp aimed at this speaker.
            long heardLate = bt.Errors.Where(e => e.HeardUs > 2_000_000).Select(e => e.ErrorUs).Average() is var avg ? (long)avg : 0;
            Calibration.Apply(settings, new CalibrationResult("BT", true, heardLate, 30));
            bt.AssumedHiddenUs = settings.HiddenUs;
            bt.Engine.Realign();
        }
        if (t % 10_000 == 0) bt.Tick(t, 300_000, 48_000);
    }
    var before = bt.Errors.Where(e => e.HeardUs is >= 2_000_000 and < 3_000_000).Average(e => e.ErrorUs);
    var after = bt.Errors.Where(e => e.HeardUs > 4_000_000).Max(e => Math.Abs(e.ErrorUs));
    Check(before > 170_000, $"before: heard {before / 1000:0} ms late");
    Check(after < 1_000, $"after one calibration: within {after / 1000:0.00} ms");
}

Console.WriteLine(failures.Count == 0 ? "\nALL PASS" : $"\n{failures.Count} FAILED");
return failures.Count == 0 ? 0 : 1;

/// <summary>
/// A speaker: plays at its own (drifting) rate, holds <c>bufferUs</c> in its device buffer and
/// hides <c>hiddenUs</c> beyond what its position clock shows. Each source sample carries its
/// own index, so what comes out can be traced back to when it should have been heard.
/// </summary>
sealed class SimSpeaker
{
    private readonly int _rate;
    private readonly double _trueRate;
    private readonly long _bufferUs;
    private readonly long _hiddenUs;
    private long _written;
    private long _lastTick = -1;
    private double _lastFrame = double.NaN;

    public string Name { get; }
    public PlayoutEngine Engine { get; }
    public long AssumedHiddenUs { get; set; }
    public List<(long HeardUs, double ErrorUs)> Errors { get; } = new();

    public SimSpeaker(string name, int rate, double driftPpm, long bufferUs, long hiddenUs)
    {
        Name = name;
        _rate = rate;
        _trueRate = rate * (1 + driftPpm / 1_000_000);
        _bufferUs = bufferUs;
        _hiddenUs = hiddenUs;
        AssumedHiddenUs = hiddenUs;
        Engine = new PlayoutEngine(48_000, rate);
    }

    public static short Encode(long frame) => (short)(frame % 30_000 - 15_000);

    public void Tick(long nowUs, long delayUs, int sourceRate)
    {
        // Frames the device has played by now, at its true rate (the clock it reports is its own).
        double played = nowUs * _trueRate / 1_000_000;
        long target = (long)(played + _bufferUs * (double)_rate / 1_000_000);
        int frames = (int)Math.Max(0, target - _written);
        if (frames == 0) return;
        // The device reports its position in its own frames; it thinks they last 1/rate each.
        long firstHeard = nowUs + (long)((_written - played) * 1_000_000 / _rate) + AssumedHiddenUs;
        var output = new float[frames * 2];
        double dt = _lastTick < 0 ? 0.01 : (nowUs - _lastTick) / 1_000_000.0;
        _lastTick = nowUs;
        Engine.Render(output, frames, firstHeard, delayUs, dt);
        for (int i = 0; i < frames; i++)
        {
            float v = output[i * 2];
            if (v == 0) { _lastFrame = double.NaN; continue; }
            double code = v * 32768.0 + 15_000; // fractional when the speaker resamples
            long w = _written + i;
            double heardUs = w * 1_000_000 / _trueRate + _hiddenUs; // when it truly reaches the ear
            // Which source frame this is: the one with this code nearest where it should be.
            double expected = (heardUs - delayUs) * sourceRate / 1_000_000;
            double frame = code + Math.Round((expected - code) / 30_000) * 30_000;
            // Interpolating across the code's wrap (29 999 → 0) gives nonsense; a real frame
            // always moves on by about one from the last.
            bool steady = !double.IsNaN(_lastFrame) && frame - _lastFrame is > 0 and < 4;
            _lastFrame = frame;
            if (!steady) continue;
            double shouldUs = frame * 1_000_000.0 / sourceRate + delayUs;
            Errors.Add(((long)heardUs, heardUs - shouldUs));
        }
        _written += frames;
    }

}
static class Debug
{
    public static void Series(SimSpeaker s)
    {
        foreach (var g in s.Errors.GroupBy(e => e.HeardUs / 500_000))
            Console.WriteLine($"      {g.Key * 0.5,5:0.0}s mean {g.Average(e => e.ErrorUs) / 1000,8:0.00} ms  min {g.Min(e => e.ErrorUs) / 1000,8:0.00}  max {g.Max(e => e.ErrorUs) / 1000,8:0.00}");
    }
}
