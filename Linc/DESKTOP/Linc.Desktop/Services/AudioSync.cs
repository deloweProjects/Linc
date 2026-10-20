using System.Diagnostics;

namespace Linc.Desktop.Services;

/// <summary>
/// The pure core of "Play everywhere" (v26, D-096): the clock, the playout controller, the group
/// delay and the test sounds. No audio API is touched here, so <c>tools/audiosyncsim</c> runs all
/// of it on any machine. The phone has the same algorithm in <c>AudioSync.kt</c>; the two must
/// agree (the sim and <c>AudioSyncTest</c> pin the same numbers).
///
/// <para><b>The idea.</b> Every block of sound carries the PC-clock time it was captured,
/// <c>Tc</c>. Every speaker, on the PC or the phone, aims to make that block <i>heard</i> at
/// <c>Tc + D</c>, where <c>D</c> (the group delay) is the same for all of them and just big
/// enough for the slowest one. Each speaker knows when the next sample it writes will be heard
/// (the device's own position clock, plus a per-device "hidden latency" that only a microphone
/// can see, such as a Bluetooth headset's buffer), so it compares that with the target and
/// corrects the difference: big gaps at once (silence in, or sound skipped), small ones by
/// playing a fraction of a percent fast or slow, which no one can hear.</para>
/// </summary>
public static class PcClock
{
    /// <summary>
    /// Now on the PC clock, in microseconds. QPC, the same timebase WASAPI stamps captured
    /// packets with, so capture times and render positions compare directly.
    /// </summary>
    public static long NowUs() => TicksToUs(Stopwatch.GetTimestamp());

    /// <summary>Stopwatch ticks to µs without overflowing (ticks × 10⁶ overflows a long after ~10 days at 10 MHz).</summary>
    public static long TicksToUs(long ticks)
    {
        long f = Stopwatch.Frequency;
        return ticks / f * 1_000_000 + ticks % f * 1_000_000 / f;
    }

    /// <summary>A WASAPI QPC position (100-ns units) in µs.</summary>
    public static long QpcHnsToUs(ulong hns) => (long)(hns / 10);
}

/// <summary>What a speaker does with the next stretch of output.</summary>
public readonly record struct PlayoutDecision(PlayoutAction Action, long Frames, double Ppm);

public enum PlayoutAction
{
    /// <summary>Ahead of time: write this many frames of silence before the next sound.</summary>
    InsertSilence,
    /// <summary>Behind: skip this many frames of sound.</summary>
    Skip,
    /// <summary>In step: play at <see cref="PlayoutDecision.Ppm"/> parts per million fast (+) or slow (−).</summary>
    Track,
}

/// <summary>
/// One speaker's control loop. Called once per write with the error: when the next frame
/// written will be heard, minus when the sound at the head of the queue should be heard (µs;
/// positive = late).
///
/// <list type="bullet">
/// <item>Not yet locked, or more than <see cref="HardUs"/> out: jump. Silence for an early
/// speaker, a skip for a late one. A gap in the PC's sound (loopback sends nothing while the PC
/// is silent) lands here too, which is how a new sound after silence starts exactly on time.</item>
/// <item>Otherwise: a smoothed proportional-integral loop sets the play rate, up to
/// ±<see cref="MaxPpm"/> (0.5%, about 9 cents of pitch, below what anyone notices on music), so
/// clock drift between devices and residual error are absorbed without a click.</item>
/// </list>
/// </summary>
public sealed class PlayoutController(int sampleRate)
{
    public const long HardUs = 40_000;
    public const double MaxPpm = 5_000;
    /// <summary>ppm per µs of error: 10 ms out runs 0.5% fast, a ~2 s time constant.</summary>
    public const double Kp = 0.5;
    /// <summary>ppm per µs·s, enough to remove a steady clock drift's residual error over a minute.</summary>
    public const double Ki = 0.02;
    private const double Smoothing = 0.2;
    private const double IntegralLimit = 1_000_000; // µs·s → at most ±20 000 ppm from I alone, clamped anyway

    private bool _locked;
    private double _smoothed;
    private double _integral;

    public bool Locked => _locked;

    /// <summary>The smoothed error, µs (what the mixer shows as "in step to within …").</summary>
    public double SmoothedErrorUs => _smoothed;

    /// <summary>Forget the lock (a new stream, a new group delay, a device that came back).</summary>
    public void Reset()
    {
        _locked = false;
        _smoothed = 0;
        _integral = 0;
    }

    public PlayoutDecision Decide(double errorUs, double dtSeconds)
    {
        if (!_locked || Math.Abs(errorUs) > HardUs)
        {
            _locked = true;
            _smoothed = 0;
            _integral = 0;
            long frames = (long)Math.Round(Math.Abs(errorUs) * sampleRate / 1_000_000.0);
            if (frames == 0) return new(PlayoutAction.Track, 0, 0);
            return errorUs < 0
                ? new(PlayoutAction.InsertSilence, frames, 0)
                : new(PlayoutAction.Skip, frames, 0);
        }
        _smoothed += Smoothing * (errorUs - _smoothed);
        double p = Kp * _smoothed;
        // No wind-up: integrate only while the proportional part alone isn't already saturated.
        if (Math.Abs(p) < MaxPpm)
            _integral = Math.Clamp(_integral + _smoothed * Math.Max(0, dtSeconds), -IntegralLimit, IntegralLimit);
        double ppm = Math.Clamp(p + Ki * _integral, -MaxPpm, MaxPpm);
        return new(PlayoutAction.Track, 0, ppm);
    }
}

/// <summary>
/// The group delay <c>D</c>: what every speaker aims for, set by the slowest one. Rises at once
/// when a speaker needs more (late sound is the one thing that must not happen); falls only
/// after every speaker has needed <see cref="FallMarginUs"/> less for <see cref="FallAfterUs"/>,
/// so a link that wobbles doesn't make the whole group re-align over and over.
/// </summary>
public sealed class GroupDelay
{
    public const long MinUs = 60_000;
    public const long MaxUs = 1_500_000;
    public const long StepUs = 10_000;
    public const long FallMarginUs = 30_000;
    public const long FallAfterUs = 20_000_000;

    private long _lowSinceUs = -1;

    public long CurrentUs { get; private set; } = MinUs;

    /// <summary>The speaker that set the last change, for the log.</summary>
    public string? Reason { get; private set; }

    /// <summary>Back to the minimum, so the next <see cref="Update"/> sets what the speakers need now ("Re-sync").</summary>
    public void Reset()
    {
        CurrentUs = MinUs;
        Reason = null;
        _lowSinceUs = -1;
    }

    /// <summary>
    /// Feed the speakers' current needs (µs). Returns true when <see cref="CurrentUs"/> changed.
    /// <paramref name="extraUs"/> is the user's safety margin from settings.
    /// </summary>
    public bool Update(IEnumerable<(string Name, long NeedUs)> needs, long extraUs, long nowUs)
    {
        long max = 0;
        string? who = null;
        foreach (var (name, need) in needs)
        {
            if (need > max) { max = need; who = name; }
        }
        long wanted = Math.Clamp(RoundUp(max + Math.Max(0, extraUs)), MinUs, MaxUs);
        if (wanted > CurrentUs)
        {
            CurrentUs = wanted;
            Reason = who;
            _lowSinceUs = -1;
            return true;
        }
        if (wanted <= CurrentUs - FallMarginUs)
        {
            if (_lowSinceUs < 0) _lowSinceUs = nowUs;
            else if (nowUs - _lowSinceUs >= FallAfterUs)
            {
                CurrentUs = wanted;
                Reason = who;
                _lowSinceUs = -1;
                return true;
            }
        }
        else
        {
            _lowSinceUs = -1;
        }
        return false;
    }

    private static long RoundUp(long us) => (us + StepUs - 1) / StepUs * StepUs;
}

/// <summary>A sound scheduled on one or more speakers: heard at <see cref="AtUs"/> (PC clock).</summary>
public readonly record struct TestSound(string DeviceId, long AtUs, TestSoundKind Kind);

public enum TestSoundKind { Chirp, Click }

/// <summary>
/// The sounds the alignment uses. A chirp (a 60 ms sweep, 1.5 → 6 kHz) has one sharp
/// correlation peak, so the phone's microphone can time it to a fraction of a millisecond even
/// in a noisy room; Bluetooth codecs carry that band cleanly. The click is for the user's ear.
/// </summary>
public static class TestSignal
{
    public const int ChirpMs = 60;
    public const double ChirpFromHz = 1_500;
    public const double ChirpToHz = 6_000;
    public const int ClickMs = 4;
    public const double ClickHz = 3_000;

    public static float[] Chirp(int sampleRate)
    {
        int n = sampleRate * ChirpMs / 1000;
        var s = new float[n];
        double duration = n / (double)sampleRate;
        double k = (ChirpToHz - ChirpFromHz) / duration;
        int ramp = sampleRate * 5 / 1000;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)sampleRate;
            double phase = 2 * Math.PI * (ChirpFromHz * t + k * t * t / 2);
            s[i] = (float)(0.5 * Math.Sin(phase) * Edge(i, n, ramp));
        }
        return s;
    }

    public static float[] Click(int sampleRate)
    {
        int n = sampleRate * ClickMs / 1000;
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)sampleRate;
            double hann = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
            s[i] = (float)(0.6 * Math.Sin(2 * Math.PI * ClickHz * t) * hann);
        }
        return s;
    }

    private static double Edge(int i, int n, int ramp)
    {
        if (i < ramp) return 0.5 - 0.5 * Math.Cos(Math.PI * i / ramp);
        if (i >= n - ramp) return 0.5 - 0.5 * Math.Cos(Math.PI * (n - 1 - i) / ramp);
        return 1;
    }

    /// <summary>
    /// Mix the scheduled sounds into <paramref name="output"/> (interleaved, <paramref name="channels"/>):
    /// output frame <c>i</c> will be heard at <paramref name="firstHeardUs"/> + i/rate.
    /// </summary>
    public static void Mix(Span<float> output, int channels, int sampleRate, long firstHeardUs,
        IReadOnlyList<TestSound> sounds, float[] chirp, float[] click)
    {
        int frames = output.Length / channels;
        foreach (var sound in sounds)
        {
            var signal = sound.Kind == TestSoundKind.Chirp ? chirp : click;
            // Index into the signal of output frame 0.
            long start = (long)Math.Round((firstHeardUs - sound.AtUs) * (double)sampleRate / 1_000_000);
            if (start >= signal.Length || start + frames <= 0) continue;
            int from = (int)Math.Max(0, -start);
            int to = (int)Math.Min(frames, signal.Length - start);
            for (int i = from; i < to; i++)
            {
                float v = signal[start + i];
                for (int c = 0; c < channels; c++) output[i * channels + c] += v;
            }
        }
    }
}

/// <summary>One block of the PC's sound: interleaved 16-bit stereo, captured at <see cref="CaptureUs"/>.</summary>
public sealed class SoundBlock(short[] pcm, long captureUs)
{
    public short[] Pcm { get; } = pcm;
    public long CaptureUs { get; } = captureUs;
    public int Frames => Pcm.Length / 2;
}

/// <summary>What one <see cref="PlayoutEngine.Render"/> call did, for the log and the mixer.</summary>
public struct PlayoutStats
{
    public long SilenceFrames;
    public long SkippedFrames;
    public bool Underrun;
    public double Ppm;
}

/// <summary>
/// One speaker's playout: a queue of timestamped blocks in, device frames out, each placed so
/// it is heard at capture time + the group delay. The PC's WASAPI sinks and audiosyncsim both
/// run exactly this code; the phone runs its Kotlin twin.
///
/// <para>Two ways to play off-rate. When the device runs at the source rate (the common
/// 48 kHz → 48 kHz case) samples are copied as they are and the controller's ppm is applied by
/// dropping or repeating one frame now and then: at 100 ppm that is one frame in 10 000, a
/// 21 µs step no one can hear, and nothing is filtered. When the rates differ (a 44.1 kHz
/// headset) a linear resampler converts, and the ppm simply trims its ratio.</para>
/// </summary>
public sealed class PlayoutEngine
{
    /// <summary>A jump in capture times bigger than this between blocks is a gap, re-aligned at once.</summary>
    public const long GapUs = 2_000;

    private readonly object _lock = new();
    private readonly Queue<SoundBlock> _queue = new();
    private readonly PlayoutController _controller;
    private readonly int _sourceRate;
    private readonly int _deviceRate;
    private readonly bool _sameRate;

    private SoundBlock? _head;
    private double _headPos;      // source frames of the head already played (fractional when resampling)
    private double _slip;         // same-rate mode: frames owed (+ = drop one) from the ppm
    private bool _resetPending;
    private long _droppedBlocks;
    private long _lastDelayUs = -1;
    /// <summary>Frames left of the fade-in after a jump: sound that starts mid-wave would click.</summary>
    private int _fadeLeft;
    private readonly int _fadeFrames;
    public const int FadeMs = 5;

    public PlayoutEngine(int sourceRate, int deviceRate)
    {
        _sourceRate = sourceRate;
        _deviceRate = deviceRate;
        _sameRate = sourceRate == deviceRate;
        _controller = new PlayoutController(deviceRate);
        _fadeFrames = deviceRate * FadeMs / 1000;
    }

    public PlayoutController Controller => _controller;

    /// <summary>Blocks thrown away because the queue grew past what any delay needs.</summary>
    public long DroppedBlocks => Interlocked.Read(ref _droppedBlocks);

    public long QueuedUs
    {
        get
        {
            lock (_lock)
            {
                long frames = (_head?.Frames ?? 0) - (long)_headPos;
                foreach (var b in _queue) frames += b.Frames;
                return frames * 1_000_000 / _sourceRate;
            }
        }
    }

    /// <summary>Add a block. Anything queued beyond <paramref name="maxQueuedUs"/> is dropped, oldest first.</summary>
    public void Enqueue(SoundBlock block, long maxQueuedUs)
    {
        lock (_lock)
        {
            _queue.Enqueue(block);
            long total = 0;
            foreach (var b in _queue) total += b.Frames;
            long max = maxQueuedUs * _sourceRate / 1_000_000;
            while (total > max && _queue.Count > 1)
            {
                total -= _queue.Dequeue().Frames;
                Interlocked.Increment(ref _droppedBlocks);
                _resetPending = true;
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _queue.Clear();
            _head = null;
            _headPos = 0;
            _slip = 0;
            _controller.Reset();
        }
    }

    /// <summary>Re-align from scratch on the next render (the group delay changed a lot, settings changed).</summary>
    public void Realign()
    {
        lock (_lock) _resetPending = true;
    }

    /// <summary>
    /// Fill <paramref name="output"/> (interleaved stereo float, <paramref name="frames"/> frames).
    /// Output frame 0 will be heard at <paramref name="firstHeardUs"/> (PC clock, hidden latency
    /// included); every source frame is aimed at its capture time + <paramref name="delayUs"/>.
    /// </summary>
    public PlayoutStats Render(Span<float> output, int frames, long firstHeardUs, long delayUs, double dtSeconds)
    {
        var stats = new PlayoutStats();
        output[..(frames * 2)].Clear();
        lock (_lock)
        {
            // The group delay moved: every speaker jumps together rather than slewing for seconds
            // out of step with the others.
            if (_lastDelayUs >= 0 && Math.Abs(delayUs - _lastDelayUs) > GapUs) _resetPending = true;
            _lastDelayUs = delayUs;
            int produced = 0;
            bool decided = false;
            double ppm = 0;
            while (produced < frames)
            {
                if (!TryHead())
                {
                    // Ran dry (the PC went quiet, or the link stalled): whatever comes next is
                    // placed by a jump, however close it lands, never slewed in.
                    stats.Underrun = true;
                    _resetPending = true;
                    _fadeLeft = _fadeFrames;
                    break;
                }
                var head = _head!;
                long heardUs = firstHeardUs + (long)(produced * 1_000_000L / _deviceRate);
                double targetUs = head.CaptureUs + _headPos * 1_000_000 / _sourceRate + delayUs;
                double error = heardUs - targetUs;

                if (!decided || _resetPending)
                {
                    if (_resetPending) { _controller.Reset(); _resetPending = false; }
                    var d = _controller.Decide(error, decided ? 0 : dtSeconds);
                    decided = true;
                    if (d.Action == PlayoutAction.InsertSilence)
                    {
                        int n = (int)Math.Min(frames - produced, d.Frames);
                        produced += n;
                        stats.SilenceFrames += n;
                        _fadeLeft = _fadeFrames;
                        // More silence owed than this write holds: stay unlocked, so the next
                        // write jumps the rest exactly instead of slewing it.
                        if (n < d.Frames) _controller.Reset();
                        continue;
                    }
                    if (d.Action == PlayoutAction.Skip)
                    {
                        // The decision is in device frames; skip the same span of source time.
                        stats.SkippedFrames += SkipSource(d.Frames * (double)_sourceRate / _deviceRate);
                        _fadeLeft = _fadeFrames;
                        continue;
                    }
                    ppm = d.Ppm;
                    stats.Ppm = ppm;
                }

                int made = _sameRate
                    ? CopyWithSlip(output, produced, frames - produced, ppm)
                    : Resample(output, produced, frames - produced, ppm);
                for (int i = produced; i < produced + made && _fadeLeft > 0; i++, _fadeLeft--)
                {
                    float gain = 1f - _fadeLeft / (float)_fadeFrames;
                    output[i * 2] *= gain;
                    output[i * 2 + 1] *= gain;
                }
                produced += made;
                if (made == 0 && _head is not null && _headPos >= _head.Frames) NextBlock();
            }
        }
        return stats;
    }

    private bool TryHead()
    {
        while (_head is null || _headPos >= _head.Frames)
        {
            if (_head is not null) { NextBlock(); continue; }
            if (_queue.Count == 0) return false;
            _head = _queue.Dequeue();
            _headPos = 0;
        }
        return true;
    }

    /// <summary>Move to the next queued block, flagging a re-align when its capture time doesn't follow on.</summary>
    private void NextBlock()
    {
        var done = _head!;
        long expectedUs = done.CaptureUs + done.Frames * 1_000_000L / _sourceRate;
        // The resampler steps in fractions and may have stepped past the end; carry that on,
        // or every block boundary would replay a fraction of a frame (~500 ppm slow at 44.1 kHz).
        double overshoot = Math.Max(0, _headPos - done.Frames);
        _head = null;
        _headPos = 0;
        if (_queue.Count == 0) return;
        _head = _queue.Dequeue();
        if (Math.Abs(_head.CaptureUs - expectedUs) > GapUs) _resetPending = true;
        else _headPos = overshoot;
    }

    private long SkipSource(double frames)
    {
        double left = frames;
        long skipped = 0;
        while (left > 0 && TryHead())
        {
            double room = _head!.Frames - _headPos;
            double take = Math.Min(room, left);
            _headPos += take;
            left -= take;
            skipped += (long)take;
        }
        return skipped;
    }

    private int CopyWithSlip(Span<float> output, int at, int frames, double ppm)
    {
        var head = _head!;
        var pcm = head.Pcm;
        int made = 0;
        while (made < frames && _headPos < head.Frames)
        {
            _slip += ppm / 1_000_000;
            if (_slip >= 1) { _slip -= 1; _headPos += 1; if (_headPos >= head.Frames) break; } // a frame dropped: play faster
            int i = (int)_headPos;
            output[(at + made) * 2] = pcm[i * 2] / 32768f;
            output[(at + made) * 2 + 1] = pcm[i * 2 + 1] / 32768f;
            made++;
            if (_slip <= -1) { _slip += 1; continue; } // the same frame again: play slower
            _headPos += 1;
        }
        return made;
    }

    private int Resample(Span<float> output, int at, int frames, double ppm)
    {
        var head = _head!;
        var pcm = head.Pcm;
        double step = _sourceRate / (double)_deviceRate * (1 + ppm / 1_000_000);
        int made = 0;
        while (made < frames && _headPos < head.Frames)
        {
            int i = (int)_headPos;
            double frac = _headPos - i;
            int j = Math.Min(i + 1, head.Frames - 1);
            output[(at + made) * 2] = (float)((pcm[i * 2] + (pcm[j * 2] - pcm[i * 2]) * frac) / 32768.0);
            output[(at + made) * 2 + 1] = (float)((pcm[i * 2 + 1] + (pcm[j * 2 + 1] - pcm[i * 2 + 1]) * frac) / 32768.0);
            made++;
            _headPos += step;
        }
        return made;
    }
}

/// <summary>A speaker's saved settings (per device id, in audio-everywhere.json).</summary>
public sealed class EverywhereDeviceSettings
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Volume { get; set; } = 100;
    public bool Muted { get; set; }
    /// <summary>Hidden latency the microphone measured (µs). Positive = heard later than its clock says.</summary>
    public long AutoUs { get; set; }
    /// <summary>The user's own nudge on top (µs), from the alignment slider.</summary>
    public long ManualUs { get; set; }
    public bool Calibrated { get; set; }

    /// <summary>The hidden latency the playout uses: measured plus the user's nudge.</summary>
    public long HiddenUs => AutoUs + ManualUs;
}

public sealed class EverywhereSettings
{
    /// <summary>Extra safety margin on the group delay (ms), for a shaky Wi-Fi.</summary>
    public int MarginMs { get; set; }
    public Dictionary<string, EverywhereDeviceSettings> Devices { get; set; } = new();
}

/// <summary>The result of timing a test sound from the phone's microphone.</summary>
public readonly record struct CalibrationResult(string DeviceId, bool Ok, long ErrorUs, double Snr);

public static class Calibration
{
    public const int Rounds = 3;
    public const long SpacingUs = 800_000;
    /// <summary>Two of the three must agree to within this, or the device is reported as unclear.</summary>
    public const long AgreeUs = 4_000;
    /// <summary>A measured hidden latency larger than this is not believed (a reflection, the wrong peak).</summary>
    public const long MaxHiddenUs = 600_000;

    /// <summary>
    /// The schedule: each device chirps <see cref="Rounds"/> times, one device at a time,
    /// <see cref="SpacingUs"/> apart, round-robin so a slow drift can't bias one device.
    /// </summary>
    public static List<TestSound> Schedule(IReadOnlyList<string> deviceIds, long startUs)
    {
        var list = new List<TestSound>();
        long at = startUs;
        for (int r = 0; r < Rounds; r++)
            foreach (var id in deviceIds)
            {
                list.Add(new TestSound(id, at, TestSoundKind.Chirp));
                at += SpacingUs;
            }
        return list;
    }

    /// <summary>The clicks a user aligns by ear: every device at once, twice a second.</summary>
    public static List<TestSound> Clicks(IReadOnlyList<string> deviceIds, long startUs, int count = 12)
    {
        var list = new List<TestSound>();
        for (int i = 0; i < count; i++)
            foreach (var id in deviceIds)
                list.Add(new TestSound(id, startUs + i * 500_000L, TestSoundKind.Click));
        return list;
    }

    /// <summary>
    /// Apply one round of results: the new measured latency is the old plus the error the
    /// microphone heard. A result that failed, or would put the device beyond
    /// <see cref="MaxHiddenUs"/>, leaves the device as it was.
    /// </summary>
    public static bool Apply(EverywhereDeviceSettings device, CalibrationResult result)
    {
        if (!result.Ok) return false;
        long next = device.AutoUs + result.ErrorUs;
        if (Math.Abs(next) > MaxHiddenUs) return false;
        device.AutoUs = next;
        device.Calibrated = true;
        return true;
    }
}
