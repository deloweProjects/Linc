using System;
using System.Threading;

namespace Linc.Desktop.Services;

/// <summary>
/// The reverse mirror's frame source (M05, D-029): captures a PC display, encodes it, and
/// raises <see cref="FrameEncoded"/> for each access unit. M05b pipes those onto the
/// pc-video channel; the mirrorsim harness measures them.
///
/// <para><b>Why two threads.</b> An async MFT signals "needs input" and "has output" on one
/// event queue. A single loop that both paces submission and drains that queue necessarily
/// delays output by up to a frame interval — measured here as a p95 of 76 ms against a 16 ms
/// median, which would be pure added mirror lag. So the pump thread does nothing but drain
/// events (and hand encoded frames straight out), while the feeder thread owns capture and
/// paces submission. They meet at a semaphore counting the encoder's free input slots.</para>
///
/// <para><b>Pacing is required.</b> Left ungated this encoder runs at over 100 fps on an idle
/// desktop, which is wasted CPU and bandwidth for a mirror. The feeder submits at most one
/// frame per frame-interval.</para>
///
/// <para><b>A static screen must not stall.</b> Desktop Duplication only reports frames that
/// changed, so on an idle desktop AcquireNextFrame just times out. If nothing were submitted
/// the stream would simply stop, which on the phone looks like a frozen mirror with no error
/// anywhere — so the feeder resubmits the last frame instead.</para>
/// </summary>
public sealed class PcMirrorSource : IDisposable
{
    private readonly PcScreenCapture _capture;
    private volatile PcVideoEncoder _encoder;
    private readonly ILogService _log;
    private readonly int _frameIntervalMs;
    private readonly SemaphoreSlim _inputSlots = new(0);
    private readonly CancellationTokenSource _stopping = new();
    private Thread? _pumpThread;
    private Thread? _feederThread;
    private long _submitted;
    private readonly System.Diagnostics.Stopwatch _streamClock = new();
    private bool _disposed;
    private int _keyFrameWanted;
    private int _bitrateWanted;   // 0 = no change pending
    private readonly int _framesPerSecond;
    private int _bitrate;

    // Watchdog (D-087): a hardware encoder that started but stops answering is swapped for the
    // software one in place, so the phone keeps its stream. Written by the pump, read by the feeder.
    private volatile bool _pumpFaulted;
    private int _submittedSinceOutput;
    private long _lastSlotTick;

    /// <summary>Raised for each encoded access unit, on the pump thread.</summary>
    public event Action<EncodedFrame>? FrameEncoded;

    /// <summary>Physical pixel size of the stream — what the phone is told in the config frame.</summary>
    public int Width => _capture.Width;
    public int Height => _capture.Height;

    public string EncoderName => _encoder.EncoderName;

    /// <summary>
    /// Ask for the next submitted frame to be encoded as an IDR. Safe to call from any
    /// thread and any number of times — the request is a flag the feeder consumes once,
    /// so a burst of requests costs one keyframe, not a storm of them.
    /// </summary>
    public void RequestKeyFrame() => Interlocked.Exchange(ref _keyFrameWanted, 1);

    /// <summary>
    /// Ask the encoder to move to a new target bit rate. Like <see cref="RequestKeyFrame"/>
    /// this only records the request; the feeder applies it between submissions, so it never
    /// races a ProcessInput. Only the latest request matters.
    /// </summary>
    public void RequestBitrate(int bitrate) => Interlocked.Exchange(ref _bitrateWanted, bitrate);

    /// <summary>
    /// Asked before each frame is encoded: may another frame go out now? When it answers false
    /// the frame is simply not captured into the encoder, and the stream's frame rate falls to
    /// what the link can carry.
    ///
    /// <para><b>This is the right place to shed load, not after encoding.</b> Discarding an
    /// encoded frame breaks the H.264 reference chain, so the stream then has to wait for an
    /// IDR — the largest frame there is — and on a slow link sending that IDR is itself what
    /// makes the next backlog. mirrorsim measured that loop directly: a drop-only sender on a
    /// 2.5 Mb/s link kept dropping ~18 frames a second even after the bit rate had been cut far
    /// below the link. A frame that was never encoded references nothing and is referenced by
    /// nothing, so skipping it costs a little smoothness and nothing else.</para>
    /// </summary>
    public Func<bool>? AcceptFrame { get; set; }

    /// <summary>Test harness only: force the NV12 input path on any encoder (D-077).</summary>
    public static bool PreferNv12 { get; set; }

    /// <summary>Test harness only: skip the hardware encoders and use Windows' software one (D-087).</summary>
    public static bool PreferSoftware { get; set; }

    /// <summary>Test harness only: after this many frames, act as if the hardware encoder failed (D-087).</summary>
    public static int SimulateEncoderFailureAfter { get; set; }

    public PcMirrorSource(int displayIndex, int framesPerSecond, int bitrate, ILogService log, bool softwareOnly = false)
    {
        _log = log;
        _framesPerSecond = framesPerSecond;
        _bitrate = bitrate;
        _frameIntervalMs = 1000 / framesPerSecond;
        _capture = new PcScreenCapture(displayIndex);
        try
        {
            // The encoder needs the frame size up front, and only an acquired frame can be trusted
            // for that (see PcScreenCapture), so prime the capture before constructing it.
            if (!PrimeCapture())
                throw new LincException("Couldn't read this PC's screen to start mirroring.");

            _encoder = new PcVideoEncoder(_capture.Device, _capture.Width, _capture.Height,
                framesPerSecond, bitrate, PreferNv12, PreferSoftware || softwareOnly);
        }
        catch
        {
            // Let go of the screen: a capture left behind by a failed start held the display's
            // duplication, and every retry then failed with E_INVALIDARG (D-077).
            _capture.Dispose();
            throw;
        }
        _log.Log(LogLevel.Info,
            $"PC mirror: {_capture.Width}x{_capture.Height} @ {framesPerSecond} fps via {_encoder.EncoderName}");
    }

    /// <summary>Wait for the first real frame so the stream size is known. </summary>
    private bool PrimeCapture()
    {
        for (int attempt = 0; attempt < 40; attempt++)
            if (_capture.TryAcquire(50, out _)) return true;
        return false;
    }

    public void Start()
    {
        _streamClock.Start();
        _lastSlotTick = Environment.TickCount64;
        StartPump(_encoder);
        _feederThread = new Thread(Feed) { IsBackground = true, Name = "linc-mirror-feed" };
        _feederThread.Start();
    }

    private void StartPump(PcVideoEncoder encoder)
    {
        _pumpThread = new Thread(() => Pump(encoder)) { IsBackground = true, Name = "linc-mirror-pump" };
        _pumpThread.Start();
    }

    /// <summary>
    /// Has the hardware encoder stopped working? Its pump died, or it has not asked for a frame in
    /// five seconds, or a second's worth of frames went in and nothing came out. Encoders hold a
    /// frame or three, never thirty; and a busy link does not trip this, because frames the link
    /// refuses are never submitted and their input slot goes straight back.
    /// </summary>
    private string? StallReason(long now)
    {
        if (_encoder.IsSoftware) return null;
        if (_pumpFaulted) return "failed";
        if (now - Interlocked.Read(ref _lastSlotTick) > 5_000) return "stopped asking for frames";
        if (Volatile.Read(ref _submittedSinceOutput) >= Math.Max(_framesPerSecond, 10)) return "took frames but gave nothing back";
        return null;
    }

    /// <summary>
    /// Replace a failed hardware encoder with Windows' software one, on the feeder thread (which
    /// owns submission, so nothing races). The new encoder opens with an IDR plus SPS/PPS, which is
    /// the clean boundary the phone's decoder needs to carry on.
    /// </summary>
    private bool SwapToSoftware(string why)
    {
        var failed = _encoder;
        _log.Log(LogLevel.Warn, $"PC mirror: {failed.EncoderName} {why}; switching to Windows' software encoder");
        failed.Distrust();
        // Wakes its pump, which then ends quietly (it sees Disposed). Bounded like Dispose (D-091).
        if (!Task.Run(failed.Dispose).Wait(TimeSpan.FromSeconds(3)))
            _log.Log(LogLevel.Warn, $"PC mirror: {failed.EncoderName} didn't shut down within 3 s; leaving it behind");
        _pumpThread?.Join(1000);
        try
        {
            _encoder = new PcVideoEncoder(_capture.Device, _capture.Width, _capture.Height,
                _framesPerSecond, _bitrate, softwareOnly: true);
        }
        catch (Exception ex)
        {
            _log.Log(LogLevel.Warn, $"PC mirror stopped: the software encoder wouldn't start either ({ex.Message})");
            return false;
        }
        while (_inputSlots.CurrentCount > 0) _inputSlots.Wait(0);   // the old encoder's slots
        _pumpFaulted = false;
        Volatile.Write(ref _submittedSinceOutput, 0);
        Interlocked.Exchange(ref _lastSlotTick, Environment.TickCount64);
        StartPump(_encoder);
        _log.Log(LogLevel.Info, $"PC mirror: now encoding with {_encoder.EncoderName}");
        return true;
    }

    /// <summary>
    /// Drains encoder events and nothing else. Kept free of any waiting so encoded frames
    /// leave the moment they are ready.
    /// </summary>
    private void Pump(PcVideoEncoder encoder)
    {
        try
        {
            // Runs until the encoder is shut down (GetEvent then fails), NOT until stopping is
            // requested: an async encoder holding a finished frame nobody collects never finishes
            // shutting down — AMD at 4K hung in END_STREAMING, then in IMFShutdown, while this
            // loop had already left on cancellation (D-091). So while stopping it keeps
            // collecting, and simply stops handing frames on.
            while (true)
            {
                int mediaEvent = encoder.NextEvent();
                bool stopping = _stopping.IsCancellationRequested;
                if (mediaEvent == Mf.METransformNeedInput)
                {
                    Interlocked.Exchange(ref _lastSlotTick, Environment.TickCount64);
                    if (!stopping) _inputSlots.Release();
                }
                else if (mediaEvent == Mf.METransformHaveOutput)
                {
                    if (encoder.Receive() is { } frame && !stopping)
                    {
                        Volatile.Write(ref _submittedSinceOutput, 0);
                        FrameEncoded?.Invoke(frame);
                    }
                }
            }
        }
        catch (Exception ex) when (_stopping.IsCancellationRequested || encoder.Disposed)
        {
            // Tearing down mid-GetEvent is the normal way this thread ends — at shutdown, or
            // when the watchdog has just replaced this encoder.
            _log.Log(LogLevel.Info, $"PC mirror pump ended: {ex.Message}");
        }
        catch (Exception ex)
        {
            _log.Log(LogLevel.Warn, $"PC mirror pump stopped: {ex.Message}");
            _pumpFaulted = true;
        }
    }

    /// <summary>Captures and submits, paced to the target frame rate.</summary>
    private void Feed()
    {
        var token = _stopping.Token;

        // Pace against an absolute deadline, never by sleeping a fixed interval after the
        // work. Sleeping afterwards makes each cycle cost (wait + capture + sleep), which
        // silently settles well under target -- it measured 20 fps against a 30 fps setting.
        long nextSubmitDue = Environment.TickCount64;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (StallReason(Environment.TickCount64) is { } why && !SwapToSoftware(why)) return;

                // Block until the encoder actually wants a frame — this is the backpressure.
                if (!_inputSlots.Wait(500, token)) continue;
                // A slot in hand is the encoder still asking — including one handed back while the
                // link was too busy to take a frame, which is no sign of a stuck encoder.
                Interlocked.Exchange(ref _lastSlotTick, Environment.TickCount64);

                // Keep capturing until the frame is due. Draining rather than submitting the
                // first arrival is what actually enforces the rate: submitting as soon as a
                // frame appears looks like low latency but simply free-runs the encoder
                // (measured 56 fps against a 30 fps setting) and wastes CPU and bandwidth.
                // Whatever landed last is the freshest, so nothing is lost by waiting.
                long now = Environment.TickCount64;
                int untilDue;
                while ((untilDue = (int)(nextSubmitDue - now)) > 0 && !token.IsCancellationRequested)
                {
                    _capture.TryAcquire(Math.Min(untilDue, _frameIntervalMs), out _);
                    now = Environment.TickCount64;
                }

                // Nothing has ever been captured yet — nothing to send. Hand the encoder's input
                // slot back, or it is lost and the stream eventually starves of slots.
                if (!_capture.HasFrame)
                {
                    _inputSlots.Release();
                    nextSubmitDue = now + _frameIntervalMs;
                    continue;
                }

                // The link is still busy with earlier frames: skip this one rather than add to
                // the backlog (see AcceptFrame). The slot goes back for the next attempt.
                if (AcceptFrame is { } accept && !accept())
                {
                    _inputSlots.Release();
                    nextSubmitDue = Math.Max(nextSubmitDue + _frameIntervalMs, now);
                    continue;
                }

                // A pending keyframe request is applied here, on the thread that owns
                // submission, so the ICodecAPI call can never race a ProcessInput.
                int bitrate = Interlocked.Exchange(ref _bitrateWanted, 0);
                if (bitrate > 0) _bitrate = bitrate;
                if (bitrate > 0 && !_encoder.SetBitrate(bitrate))
                    _log.Log(LogLevel.Warn, $"PC mirror: the encoder refused a bit rate change to {bitrate / 1000} kb/s");
                if (Interlocked.Exchange(ref _keyFrameWanted, 0) == 1) _encoder.ForceKeyFrame();

                // On an idle desktop this resends the last frame, which is what keeps the
                // stream alive rather than silently stopping.
                //
                // The timestamp is wall-clock, not frame-count: with frames skipped for the link,
                // a frame-count clock would tell the encoder's rate control that time passes at
                // 30 fps when it does not, and every frame would be sized for a rate that is not
                // happening. On the wall clock, "N kb/s" means N kb per real second.
                _encoder.Submit(_capture.Last, _streamClock.Elapsed.Ticks);
                _submitted++;
                Interlocked.Increment(ref _submittedSinceOutput);
                if (_submitted == SimulateEncoderFailureAfter) _pumpFaulted = true;

                // Advance the deadline by exactly one interval; if we have fallen behind,
                // resynchronise to now rather than trying to catch up in a burst.
                nextSubmitDue = Math.Max(nextSubmitDue + _frameIntervalMs, now);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Log(LogLevel.Warn, $"PC mirror capture stopped: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stopping.Cancel();
        _feederThread?.Join(1000);

        // The pump may be parked in a blocking GetEvent; disposing the encoder is what
        // releases it, so it is not joined first.
        // Never wait for ever on a graphics driver (D-091): an encoder that will not shut down in
        // 3 s is left behind rather than freezing whoever is stopping the mirror.
        var encoder = _encoder;
        if (!Task.Run(encoder.Dispose).Wait(TimeSpan.FromSeconds(3)))
            _log.Log(LogLevel.Warn, $"PC mirror: {encoder.EncoderName} didn't shut down within 3 s; leaving it behind");
        _pumpThread?.Join(1000);
        _capture.Dispose();
        _inputSlots.Dispose();
        _stopping.Dispose();
    }
}
