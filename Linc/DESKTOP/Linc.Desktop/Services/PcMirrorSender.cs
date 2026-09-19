using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Linc.Desktop.Protocol;

namespace Linc.Desktop.Services;

/// <summary>
/// Writes encoded frames onto the pc-video channel (5) without ever blocking the encoder,
/// and throws away what a slow link cannot carry instead of queueing it.
///
/// <para><b>Why this exists.</b> The encoder's pump thread used to write frames to the socket
/// itself. Over the cable that is invisible; over Wi-Fi it is the whole problem. When the link
/// slows, the socket's send buffer fills and the write blocks — and because that thread is also
/// the one draining the MFT's event queue, the encoder stops being serviced, capture stalls
/// behind it, and the mirror freezes. When the link recovers, everything that piled up is
/// delivered at once: the phone shows a burst of stale frames and the picture is now seconds
/// behind the PC, with nothing to bring it back.</para>
///
/// <para><b>So: a mirror is a live view, not a recording.</b> Frames go into a small queue that
/// a dedicated writer thread drains. If the queue grows past <see cref="MaxQueuedFrames"/> or
/// <see cref="MaxQueuedBytes"/> the link is not keeping up, and the right answer is to discard
/// the backlog rather than deliver it late — old frames of a screen nobody is looking at any
/// more are worth nothing. Dropping breaks the H.264 reference chain, so a keyframe is
/// requested at the same time and everything queued is dropped until it arrives; the phone
/// discards the same span (it waits for a keyframe after any gap), so the picture resumes
/// clean and current instead of smeared.</para>
/// </summary>
public sealed class PcMirrorSender : IDisposable
{
    /// <summary>Roughly a third of a second at 30 fps — beyond this, latency is worse than a drop.</summary>
    private const int MaxQueuedFrames = 10;

    /// <summary>A second of the highest bitrate the phone can ask for, as a second bound for big frames.</summary>
    private const int MaxQueuedBytes = 2_500_000;

    private readonly Stream _channel;
    private readonly ILogService _log;
    private readonly Action _requestKeyFrame;
    private readonly Queue<EncodedFrame> _queue = new();
    private readonly SemaphoreSlim _pending = new(0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Thread _writer;
    private int _queuedBytes;
    private long _dropped;
    private bool _waitingForKeyFrame;
    private bool _disposed;

    /// <summary>Raised once when the channel fails or ends. The mirror session is over.</summary>
    public event Action<string>? Ended;

    /// <summary>How many frames have been discarded to keep the view live. For logs and the harness.</summary>
    public long DroppedFrames => Interlocked.Read(ref _dropped);

    public PcMirrorSender(Stream channel, Action requestKeyFrame, ILogService log)
    {
        _channel = channel;
        _requestKeyFrame = requestKeyFrame;
        _log = log;
        _writer = new Thread(Write) { IsBackground = true, Name = "linc-mirror-send" };
        _writer.Start();
    }

    /// <summary>
    /// Queue one encoded frame. Called on the encoder's pump thread, so it never blocks,
    /// never throws, and never waits on the socket.
    /// </summary>
    public void Enqueue(EncodedFrame frame)
    {
        if (_stopping.IsCancellationRequested) return;

        lock (_queue)
        {
            // After a drop the reference chain is broken: anything before the next keyframe
            // would decode to garbage on the phone, so it is not worth the bandwidth.
            if (_waitingForKeyFrame && !frame.Keyframe)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            if (frame.Keyframe) _waitingForKeyFrame = false;

            _queue.Enqueue(frame);
            _queuedBytes += frame.Data.Length;

            if (_queue.Count > MaxQueuedFrames || _queuedBytes > MaxQueuedBytes)
            {
                DropBacklog();
                return; // DropBacklog leaves the semaphore matching the queue
            }
        }
        _pending.Release();
    }

    /// <summary>
    /// Throw the whole backlog away and ask for a fresh keyframe. Called under the queue lock.
    /// The semaphore is drained to match, so the writer never wakes for a frame that is gone.
    /// </summary>
    private void DropBacklog()
    {
        int dropped = _queue.Count;
        _queue.Clear();
        _queuedBytes = 0;
        _waitingForKeyFrame = true;
        Interlocked.Add(ref _dropped, dropped);
        while (_pending.CurrentCount > 0 && _pending.Wait(0)) { /* drain */ }

        try { _requestKeyFrame(); } catch { /* best-effort */ }
        _log.Log(LogLevel.Info,
            $"PC mirror: the link fell behind, skipped {dropped} frame(s) to stay live");
    }

    private void Write()
    {
        var token = _stopping.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!_pending.Wait(250, token)) continue;

                EncodedFrame frame;
                lock (_queue)
                {
                    if (_queue.Count == 0) continue;
                    frame = _queue.Dequeue();
                    _queuedBytes -= frame.Data.Length;
                }

                WritePacket(frame, token);
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
    /// Header and payload in one buffer and one write: two writes per frame put the 12-byte
    /// header in its own TCP segment, which on a wireless link is a wasted round of framing
    /// per frame and interacts badly with delayed ACKs.
    /// </summary>
    private void WritePacket(EncodedFrame frame, CancellationToken token)
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
