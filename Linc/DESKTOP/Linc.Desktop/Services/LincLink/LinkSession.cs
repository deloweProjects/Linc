namespace Linc.Desktop.Services;

/// <summary>What became of a sequenced frame that just arrived.</summary>
public enum FrameArrival
{
    /// <summary>The next frame in order: handle it.</summary>
    New,

    /// <summary>Already handled (a replay after a resume): drop it, so nothing happens twice.</summary>
    Duplicate,

    /// <summary>Frames before it never arrived. Handle it, but the session lost something.</summary>
    Gap,
}

/// <summary>
/// One LincLink session's numbering (v32, Vibe/agent/design/linclink.md §4). Every control frame
/// after the hello carries <c>seq</c> and <c>ack</c>; frames the peer has not acknowledged are kept
/// so a new transport can replay them, and a replay the receiver already has is dropped by its
/// number. That is what lets one session ride a cable, then Wi-Fi, without a request answered
/// twice or a notification lost in the gap.
///
/// Pure: no sockets and no clock of its own (times are passed in as milliseconds). Not
/// thread-safe: the caller holds its write lock around <see cref="Stamp"/>/<see cref="Keep"/> and
/// its read loop around <see cref="Accept"/>.
/// </summary>
public sealed class LinkSession(string id)
{
    /// <summary>The most unacknowledged frames kept for a replay. Past either cap the oldest go,
    /// and a transport that needs them gets a fresh session instead of a resumed one.</summary>
    public const int MaxFrames = 256;
    public const int MaxBytes = 1 << 20;

    /// <summary>Send a bare <c>link.ack</c> once this many frames, or this long, went unacknowledged.</summary>
    public const int AckEveryFrames = 32;
    public const long AckEveryMs = 2_000;

    private readonly Queue<(long Seq, string Json)> _unacked = new();
    private long _bytes;
    private long _ackTold;
    private long? _unackedSinceMs;

    /// <summary>The phone's id for this session (16 random bytes, hex), issued in its first hello reply.</summary>
    public string Id { get; } = id;

    /// <summary>The highest seq this side has stamped.</summary>
    public long Sent { get; private set; }

    /// <summary>The highest seq accepted from the peer: what this side's <c>ack</c> says.</summary>
    public long Received { get; private set; }

    /// <summary>The highest of this side's frames the peer has acknowledged.</summary>
    public long PeerAcked { get; private set; }

    public int Pending => _unacked.Count;

    /// <summary>The next seq for an outgoing frame.</summary>
    public long Stamp() => ++Sent;

    /// <summary>Keep a stamped frame until the peer acknowledges it.</summary>
    public void Keep(long seq, string json)
    {
        _unacked.Enqueue((seq, json));
        _bytes += json.Length;
        while (_unacked.Count > MaxFrames || (_bytes > MaxBytes && _unacked.Count > 1))
        {
            var (_, dropped) = _unacked.Dequeue();
            _bytes -= dropped.Length;
        }
    }

    /// <summary>The peer says it has everything up to <paramref name="ack"/>.</summary>
    public void OnAck(long ack)
    {
        if (ack <= PeerAcked || ack > Sent)
        {
            return; // stale, or nonsense from a confused peer: never trim what wasn't sent
        }
        PeerAcked = ack;
        while (_unacked.Count > 0 && _unacked.Peek().Seq <= ack)
        {
            var (_, done) = _unacked.Dequeue();
            _bytes -= done.Length;
        }
    }

    /// <summary>A sequenced frame arrived at <paramref name="nowMs"/>.</summary>
    public FrameArrival Accept(long seq, long nowMs)
    {
        if (seq <= Received)
        {
            return FrameArrival.Duplicate;
        }
        var gap = seq != Received + 1;
        Received = seq;
        _unackedSinceMs ??= nowMs;
        return gap ? FrameArrival.Gap : FrameArrival.New;
    }

    /// <summary>The ack is riding an outgoing frame (or a bare <c>link.ack</c>).</summary>
    public void AckSent(long ack)
    {
        if (ack >= Received)
        {
            _unackedSinceMs = null;
        }
        _ackTold = Math.Max(_ackTold, ack);
    }

    /// <summary>True when the peer should be told what arrived, and nothing outgoing is carrying it.</summary>
    public bool AckDue(long nowMs) =>
        Received > _ackTold &&
        (Received - _ackTold >= AckEveryFrames || (_unackedSinceMs is { } since && nowMs - since >= AckEveryMs));

    /// <summary>
    /// The frames to send again on a new transport whose peer has everything up to
    /// <paramref name="peerReceived"/>, in order; or null when some of them are gone (the buffer
    /// overflowed, or the peer claims frames that were never sent), so this cannot be a resume.
    /// </summary>
    public IReadOnlyList<string>? ReplayAfter(long peerReceived)
    {
        if (peerReceived < 0 || peerReceived > Sent)
        {
            return null;
        }
        var firstKept = _unacked.Count > 0 ? _unacked.Peek().Seq : Sent + 1;
        if (peerReceived + 1 < firstKept)
        {
            return null; // frames peerReceived+1 .. firstKept-1 were dropped from the buffer
        }
        OnAck(peerReceived);
        return _unacked.Select(f => f.Json).ToList();
    }
}
