package app.linc.android.service.link

/** What became of a sequenced frame that just arrived. */
enum class FrameArrival {
    /** The next frame in order: handle it. */
    NEW,

    /** Already handled (a replay after a resume): drop it, so nothing happens twice. */
    DUPLICATE,

    /** Frames before it never arrived. Handle it, but the session lost something. */
    GAP,
}

/**
 * One LincLink session's numbering (v32, Vibe/agent/design/linclink.md §4) — the same rules as the
 * PC's `LinkSession.cs`. Every control frame after the hello carries `seq` and `ack`; frames the PC
 * has not acknowledged are kept so a new transport can replay them, and a replay already handled is
 * dropped by its number, so a cable pulled mid-stream loses nothing and repeats nothing.
 *
 * Pure: no sockets, times passed in. Not thread-safe: the caller holds its write lock around
 * [stamp]/[keep] and its read loop around [accept].
 */
class LinkSession(val id: String) {
    private val unacked = ArrayDeque<Pair<Long, String>>()
    private var bytes = 0L
    private var ackTold = 0L
    private var unackedSinceMs: Long? = null

    /** The highest seq this side has stamped. */
    var sent = 0L
        private set

    /** The highest seq accepted from the PC: what this side's `ack` says. */
    var received = 0L
        private set

    /** The highest of this side's frames the PC has acknowledged. */
    var peerAcked = 0L
        private set

    val pending: Int get() = unacked.size

    /** The oldest seq still kept: the PC can be resumed only if it has everything before it. */
    val firstKept: Long get() = unacked.firstOrNull()?.first ?: (sent + 1)

    fun stamp(): Long = ++sent

    fun keep(seq: Long, json: String) {
        unacked.addLast(seq to json)
        bytes += json.length
        while (unacked.size > MAX_FRAMES || (bytes > MAX_BYTES && unacked.size > 1)) {
            bytes -= unacked.removeFirst().second.length
        }
    }

    fun onAck(ack: Long) {
        if (ack <= peerAcked || ack > sent) return // stale, or never sent: never trim on nonsense
        peerAcked = ack
        while (unacked.isNotEmpty() && unacked.first().first <= ack) {
            bytes -= unacked.removeFirst().second.length
        }
    }

    fun accept(seq: Long, nowMs: Long): FrameArrival {
        if (seq <= received) return FrameArrival.DUPLICATE
        val gap = seq != received + 1
        received = seq
        if (unackedSinceMs == null) unackedSinceMs = nowMs
        return if (gap) FrameArrival.GAP else FrameArrival.NEW
    }

    fun ackSent(ack: Long) {
        if (ack >= received) unackedSinceMs = null
        ackTold = maxOf(ackTold, ack)
    }

    fun ackDue(nowMs: Long): Boolean =
        received > ackTold &&
            (received - ackTold >= ACK_EVERY_FRAMES || unackedSinceMs?.let { nowMs - it >= ACK_EVERY_MS } == true)

    /**
     * The frames to send again on a new transport whose PC has everything up to [peerReceived], or
     * null when some are gone (the buffer overflowed) or the PC claims frames never sent.
     */
    fun replayAfter(peerReceived: Long): List<String>? {
        if (peerReceived < 0 || peerReceived > sent) return null
        // What the PC has proven it holds: what it says now, or what it acked earlier on the old route.
        if (maxOf(peerReceived, peerAcked) + 1 < firstKept) return null
        onAck(peerReceived)
        return unacked.map { it.second }
    }

    companion object {
        const val MAX_FRAMES = 256
        const val MAX_BYTES = 1 shl 20
        const val ACK_EVERY_FRAMES = 32
        const val ACK_EVERY_MS = 2_000L
    }
}
