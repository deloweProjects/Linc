using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Linc.Desktop.Protocol;

namespace Linc.Desktop.Services;

/// <summary>
/// Writes encoded frames onto the pc-video channel (5) without ever blocking the encoder,
/// throws away what a slow link cannot carry, and — the part that actually fixes Wi-Fi —
/// turns the encoder's bit rate down until the link <i>can</i> carry it.
///
/// <para><b>Why a writer thread.</b> The encoder's pump thread used to write frames itself.
/// When the link slowed, that write blocked the one thread draining the MFT's event queue, so
/// capture stalled, the picture froze, and on recovery a backlog of stale frames arrived at
/// once. Frames now go into a queue that a dedicated thread drains; <see cref="Enqueue"/>
/// never waits on the socket.</para>
///
/// <para><b>Why dropping alone was not enough (D-065 → D-066).</b> The first version of this
/// class only dropped. On a real Wi-Fi session that is a loop, not a fix: a link that carries
/// less than the stream's bit rate fills the queue, the backlog is dropped, a keyframe is
/// requested — the single most expensive frame the encoder makes — and the queue fills again
/// behind it. The log of that session shows exactly this: 11 frames dropped every ~0.37 s, for
/// seventeen seconds at a stretch, which on the phone is a frozen picture. The link was never
/// going to catch up because nothing asked the encoder to send less.</para>
///
/// <para><b>So load is shed before encoding, and the bit rate follows the link.</b> Two
/// mechanisms, at two time scales:</para>
/// <list type="bullet">
/// <item><b>Frame skipping (per frame).</b> The capture loop asks <see cref="AcceptFrame"/>
/// before every encode, and is told no while <see cref="MaxOutstanding"/> frames are still
/// queued or being written. The frame is never encoded, so nothing downstream is broken:
/// the phone sees a lower frame rate and a picture that stays current. This bounds latency
/// to a couple of frames plus the (deliberately small) socket buffer.</item>
/// <item><b>Bit rate (per second).</b> A high skip rate, or frames that take long to write,
/// means the link carries less than the encoder is producing; the bit rate is cut
/// multiplicatively. Once the link has stayed clear for a while it is raised in small steps
/// back toward what the phone asked for. AIMD, the shape TCP itself uses, applied one layer
/// up where the encoder can act on it. A lower bit rate means smaller frames, which means
/// fewer skips, which restores the frame rate.</item>
/// </list>
///
/// <para><b>Dropping encoded frames is now the last resort</b>, for a link that stops dead
/// (a frame older than <see cref="MaxQueueAgeMs"/>). That is the only path that breaks the
/// reference chain and needs an IDR.</para>
///
/// <para><b>Keyframes are rationed.</b> A dropped backlog still breaks the reference chain, so
/// the stream still waits for an IDR — but IDR requests (from a drop here, or from the phone)
/// are limited to one per <see cref="KeyFrameGapMs"/>. On a congested link a keyframe per drop
/// would spend most of the remaining bandwidth on keyframes.</para>
/// </summary>
public sealed class PcMirrorSender : IDisposable
{
    /// <summary>
    /// Frames allowed queued-or-being-written before the capture loop is told to skip. Two keeps
    /// one frame on the wire and the next ready behind it: enough to keep the link busy, too
    /// few to build a backlog anyone can see.
    /// </summary>
    private const int MaxOutstanding = 2;

    /// <summary>
    /// Oldest a queued frame may get before the link is judged to have stopped and the backlog
    /// is dropped. Skipping keeps the queue short in normal operation, so this only fires when
    /// a write has been blocked this long: a stall, not congestion.
    /// </summary>
    private const int MaxQueueAgeMs = 1500;

    /// <summary>Byte bound on the queue, for the case of a few very large frames.</summary>
    private const int MaxQueuedBytes = 2_500_000;

    /// <summary>At most one forced IDR per this interval — see the class remarks.</summary>
    private const int KeyFrameGapMs = 1000;

    // ---- congestion control ------------------------------------------------------------

    /// <summary>Send delay (EWMA) above which the link is treated as congested.</summary>
    private const double CongestedDelayMs = 150;

    /// <summary>Send delay (EWMA) below which the link has headroom to probe upward.</summary>
    private const double HealthyDelayMs = 60;

    /// <summary>Fraction of frames skipped over a window above which the link is congested.</summary>
    private const double CongestedSkipRatio = 0.25;

    /// <summary>Fraction of frames skipped below which the link has headroom.</summary>
    private const double HealthySkipRatio = 0.05;

    /// <summary>How often the skip ratio is evaluated.</summary>
    private const int WindowMs = 1000;

    /// <summary>Never go below this: under it a 1080p desktop stops being legible text.</summary>
    public const int MinBitrate = 600_000;

    /// <summary>
    /// Minimum time between two cuts. The encoder needs about a second to settle on a new rate
    /// (mirrorsim), and the queue needs time to drain what was encoded at the old one; cutting
    /// again before then reacts to the past and overshoots. The first controller, cutting every
    /// 700 ms, measured all the way down to the floor on a link that could carry four times it.
    /// </summary>
    private const int CutIntervalMs = 1500;

    /// <summary>How long the link must stay healthy after any cut before probing upward.</summary>
    private const int HoldAfterCutMs = 4000;

    /// <summary>Minimum time between two raises.</summary>
    private const int RaiseIntervalMs = 2000;

    private readonly Stream _channel;
    private readonly ILogService _log;
    private readonly Action _requestKeyFrame;
    private readonly Action<int> _setBitrate;
    private readonly int _maxBitrate;
    private readonly Queue<(EncodedFrame Frame, long EnqueuedMs)> _queue = new();
    private readonly SemaphoreSlim _pending = new(0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Thread _writer;
    private int _queuedBytes;
    private long _dropped;
    private bool _waitingForKeyFrame;
    private bool _disposed;

    // Controller state — touched from the pump thread (on a drop) and the writer thread, so
    // every read-modify-write happens under _control.
    private readonly object _control = new();
    private int _bitrate;
    private double _delayEwmaMs;
    private long _lastCutMs = long.MinValue / 2;
    private long _lastRaiseMs = long.MinValue / 2;
    private long _lastKeyFrameMs = long.MinValue / 2;
    private bool _keyFrameOwed;
    private int _outstanding;          // queued + being written; guarded by _queue
    private int _windowAccepted;       // guarded by _control
    private int _windowSkipped;
    private long _windowStartMs;
    private double _skipRatio;

    /// <summary>Raised once when the channel fails or ends. The mirror session is over.</summary>
    public event Action<string>? Ended;

    /// <summary>How many frames have been discarded to keep the view live.</summary>
    public long DroppedFrames => Interlocked.Read(ref _dropped);

    /// <summary>The bit rate the controller currently has the encoder at.</summary>
    public int CurrentBitrate { get { lock (_control) return _bitrate; } }

    private long _sentFrames;
    private long _sentBytes;

    /// <summary>
    /// A snapshot of the link as the controller sees it, for the phone's connection bar (v20
    /// <c>pc.mirror.stats</c>). Counters are cumulative; the caller differences them.
    /// </summary>
    public (int Bitrate, double DelayMs, double SkipRatio, long SentFrames, long SentBytes, long Dropped) Snapshot()
    {
        lock (_control)
            return (_bitrate, _delayEwmaMs, _skipRatio,
                Interlocked.Read(ref _sentFrames), Interlocked.Read(ref _sentBytes), Interlocked.Read(ref _dropped));
    }

    /// <param name="channel">The pc-video stream.</param>
    /// <param name="requestKeyFrame">Ask the encoder for an IDR (rationed by this class).</param>
    /// <param name="setBitrate">Ask the encoder for a new target bit rate.</param>
    /// <param name="maxBitrate">What the phone asked for: the controller never exceeds it.</param>
    /// <param name="log">Where bit-rate changes are reported.</param>
    public PcMirrorSender(Stream channel, Action requestKeyFrame, Action<int> setBitrate,
        int maxBitrate, ILogService log)
    {
        _channel = channel;
        _requestKeyFrame = requestKeyFrame;
        _setBitrate = setBitrate;
        _maxBitrate = Math.Max(maxBitrate, MinBitrate);
        _bitrate = _maxBitrate;
        _log = log;
        _writer = new Thread(Write) { IsBackground = true, Name = "linc-mirror-send" };
        _writer.Start();
    }

    /// <summary>
    /// The capture loop's question before each encode: may another frame go out now? False
    /// while <see cref="MaxOutstanding"/> frames are still queued or being written (see the
    /// class remarks). Every answer is counted: the skip ratio is the controller's main
    /// congestion signal.
    /// </summary>
    public bool AcceptFrame()
    {
        bool accept;
        lock (_queue)
        {
            // While waiting for a keyframe after a stall, frames must still be encoded or the
            // keyframe itself is never produced, so a keyframe wait never skips.
            accept = _waitingForKeyFrame || _outstanding < MaxOutstanding;
        }
        lock (_control)
        {
            if (accept) _windowAccepted++; else _windowSkipped++;
        }
        return accept;
    }

    /// <summary>
    /// Queue one encoded frame. Called on the encoder's pump thread, so it never blocks,
    /// never throws, and never waits on the socket.
    /// </summary>
    public void Enqueue(EncodedFrame frame)
    {
        if (_stopping.IsCancellationRequested) return;
        long now = _clock.ElapsedMilliseconds;
        bool dropped = false;
        bool stillWaiting = false;

        lock (_queue)
        {
            // After a drop the reference chain is broken: anything before the next keyframe
            // would decode to garbage on the phone, so it is not worth the bandwidth.
            if (_waitingForKeyFrame && !frame.Keyframe)
            {
                Interlocked.Increment(ref _dropped);
                stillWaiting = true;
            }
            else
            {
                if (frame.Keyframe) _waitingForKeyFrame = false;

                _queue.Enqueue((frame, now));
                _queuedBytes += frame.Data.Length;
                _outstanding++;

                if (now - _queue.Peek().EnqueuedMs > MaxQueueAgeMs || _queuedBytes > MaxQueuedBytes)
                {
                    DropBacklog();
                    dropped = true;
                }
            }
        }

        if (stillWaiting)
        {
            // The keyframe for this gap may have been deferred by the ration; ask again once
            // it allows, or the picture stays frozen until the scheduled one.
            RequestKeyFrame();
            return;
        }
        if (dropped)
        {
            // The link stopped dead for over a second: the strongest signal there is. Cut hard.
            Cut(now, 0.5, "the link stalled");
            RequestKeyFrame();
            return; // DropBacklog left the semaphore matching the (empty) queue
        }
        _pending.Release();
    }

    /// <summary>
    /// Ask for an IDR — from a drop here, or from the phone (v19 <c>pc.mirror.keyframe</c>).
    /// Rationed to one per <see cref="KeyFrameGapMs"/>; a request inside the gap is remembered
    /// and paid as soon as the gap allows.
    /// </summary>
    public void RequestKeyFrame()
    {
        bool send;
        lock (_control)
        {
            long now = _clock.ElapsedMilliseconds;
            send = now - _lastKeyFrameMs >= KeyFrameGapMs;
            if (send) { _lastKeyFrameMs = now; _keyFrameOwed = false; }
            else _keyFrameOwed = true;
        }
        if (send) { try { _requestKeyFrame(); } catch { /* best-effort */ } }
    }

    /// <summary>Throw the backlog away. Called under the queue lock.</summary>
    private void DropBacklog()
    {
        int dropped = _queue.Count;
        _queue.Clear();
        _queuedBytes = 0;
        _outstanding -= dropped;
        _waitingForKeyFrame = true;
        Interlocked.Add(ref _dropped, dropped);
        while (_pending.Wait(0)) { /* drain to match the empty queue */ }
    }

    private void Write()
    {
        var token = _stopping.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!_pending.Wait(250, token)) { Tick(); continue; }

                EncodedFrame frame;
                long enqueuedMs;
                lock (_queue)
                {
                    if (_queue.Count == 0) continue;
                    (frame, enqueuedMs) = _queue.Dequeue();
                    _queuedBytes -= frame.Data.Length;
                }

                try { WritePacket(frame); }
                finally { lock (_queue) _outstanding--; }
                Interlocked.Increment(ref _sentFrames);
                Interlocked.Add(ref _sentBytes, frame.Data.Length + VideoFraming.HeaderBytes);
                Observe(_clock.ElapsedMilliseconds - enqueuedMs);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            // The phone hanging up is the ordinary end of a mirror session, not a fault.
            if (!token.IsCancellationRequested) Ended?.Invoke(ex.Message);
        }
    }

    /// <summary>
    /// Feed one frame's send delay into the controller. The delay spans queueing here and any
    /// time the write spent blocked on a full socket buffer, i.e. how far behind the link is.
    /// </summary>
    private void Observe(long delayMs)
    {
        lock (_control) _delayEwmaMs = _delayEwmaMs * 0.8 + delayMs * 0.2;
        Tick();
    }

    /// <summary>
    /// The controller's clock: close the skip window when due, then cut on congestion, raise
    /// on sustained headroom, and pay any keyframe the ration deferred.
    /// </summary>
    private void Tick()
    {
        long now = _clock.ElapsedMilliseconds;
        bool owed;
        bool congested;
        int raiseTo = 0;
        lock (_control)
        {
            if (now - _windowStartMs >= WindowMs)
            {
                int total = _windowAccepted + _windowSkipped;
                _skipRatio = total == 0 ? 0 : (double)_windowSkipped / total;
                _windowAccepted = _windowSkipped = 0;
                _windowStartMs = now;
            }

            owed = _keyFrameOwed;
            congested = _skipRatio > CongestedSkipRatio || _delayEwmaMs > CongestedDelayMs;

            if (!congested
                && _skipRatio < HealthySkipRatio
                && _delayEwmaMs < HealthyDelayMs
                && _bitrate < _maxBitrate
                && now - _lastCutMs >= HoldAfterCutMs
                && now - _lastRaiseMs >= RaiseIntervalMs)
            {
                raiseTo = Math.Min(_maxBitrate, (int)(_bitrate * 1.15) + 50_000);
                _bitrate = raiseTo;
                _lastRaiseMs = now;
            }
        }

        if (congested) Cut(now, 0.75, "the link can't keep up");
        if (raiseTo > 0) Apply(raiseTo, $"the link has headroom, raising to {raiseTo / 1000} kb/s");
        if (owed) RequestKeyFrame();
    }

    /// <summary>Multiply the bit rate by <paramref name="factor"/>, at most once per <see cref="CutIntervalMs"/>.</summary>
    private void Cut(long now, double factor, string why)
    {
        int cutTo;
        lock (_control)
        {
            if (now - _lastCutMs < CutIntervalMs || _bitrate <= MinBitrate) return;
            cutTo = Math.Max(MinBitrate, (int)(_bitrate * factor));
            _bitrate = cutTo;
            _lastCutMs = now;
        }
        Apply(cutTo, $"{why}, lowering to {cutTo / 1000} kb/s");
    }

    private void Apply(int bitrate, string message)
    {
        try { _setBitrate(bitrate); } catch { /* best-effort */ }
        double skip;
        lock (_control) skip = _skipRatio;
        _log.Log(LogLevel.Info,
            $"PC mirror: {message} (skipping {skip:P0} of frames, {DroppedFrames} dropped so far)");
    }

    /// <summary>
    /// Header and payload in one buffer and one write: two writes per frame put the 12-byte
    /// header in its own TCP segment, which on a wireless link is a wasted round of framing.
    /// </summary>
    private void WritePacket(EncodedFrame frame)
    {
        var packet = new byte[VideoFraming.HeaderBytes + frame.Data.Length];
        VideoFraming.WriteHeader(packet, frame.TimestampUs,
            config: false, keyframe: frame.Keyframe, frame.Data.Length);
        frame.Data.CopyTo(packet, VideoFraming.HeaderBytes);

        _channel.Write(packet, 0, packet.Length);
        _channel.Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stopping.Cancel();
        _writer.Join(1000);
        _stopping.Dispose();
        _pending.Dispose();
    }
}
