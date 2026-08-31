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
    private readonly PcVideoEncoder _encoder;
    private readonly ILogService _log;
    private readonly int _frameIntervalMs;
    private readonly SemaphoreSlim _inputSlots = new(0);
    private readonly CancellationTokenSource _stopping = new();
    private Thread? _pumpThread;
    private Thread? _feederThread;
    private long _submitted;
    private bool _disposed;

    /// <summary>Raised for each encoded access unit, on the pump thread.</summary>
    public event Action<EncodedFrame>? FrameEncoded;

    /// <summary>Physical pixel size of the stream — what the phone is told in the config frame.</summary>
    public int Width => _capture.Width;
    public int Height => _capture.Height;

    public string EncoderName => _encoder.EncoderName;

    public PcMirrorSource(int displayIndex, int framesPerSecond, int bitrate, ILogService log)
    {
        _log = log;
        _frameIntervalMs = 1000 / framesPerSecond;
        _capture = new PcScreenCapture(displayIndex);

        // The encoder needs the frame size up front, and only an acquired frame can be trusted
        // for that (see PcScreenCapture), so prime the capture before constructing it.
        if (!PrimeCapture())
            throw new LincException("Couldn't read this PC's screen to start mirroring.");

        _encoder = new PcVideoEncoder(_capture.Device, _capture.Width, _capture.Height,
            framesPerSecond, bitrate);
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
        _pumpThread = new Thread(Pump) { IsBackground = true, Name = "linc-mirror-pump" };
        _feederThread = new Thread(Feed) { IsBackground = true, Name = "linc-mirror-feed" };
        _pumpThread.Start();
        _feederThread.Start();
    }

    /// <summary>
    /// Drains encoder events and nothing else. Kept free of any waiting so encoded frames
    /// leave the moment they are ready.
    /// </summary>
    private void Pump()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                int mediaEvent = _encoder.NextEvent();
                if (mediaEvent == Mf.METransformNeedInput)
                {
                    _inputSlots.Release();
                }
                else if (mediaEvent == Mf.METransformHaveOutput)
                {
                    if (_encoder.Receive() is { } frame) FrameEncoded?.Invoke(frame);
                }
            }
        }
        catch (Exception ex) when (_stopping.IsCancellationRequested)
        {
            // Tearing down mid-GetEvent is the normal way this thread ends.
            _log.Log(LogLevel.Info, $"PC mirror pump ended during shutdown: {ex.Message}");
        }
        catch (Exception ex)
        {
            _log.Log(LogLevel.Warn, $"PC mirror pump stopped: {ex.Message}");
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
                // Block until the encoder actually wants a frame — this is the backpressure.
                if (!_inputSlots.Wait(500, token)) continue;

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

                // Nothing has ever been captured yet — nothing to send.
                if (!_capture.HasFrame) { nextSubmitDue = now + _frameIntervalMs; continue; }

                // On an idle desktop this resends the last frame, which is what keeps the
                // stream alive rather than silently stopping.
                _encoder.Submit(_capture.Last, _submitted++);

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
        _encoder.Dispose();
        _pumpThread?.Join(1000);
        _capture.Dispose();
        _inputSlots.Dispose();
        _stopping.Dispose();
    }
}
