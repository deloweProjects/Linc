package app.linc.android.service

import android.content.Context
import android.os.Build
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/**
 * Which PC may have the phone right now (D-088). Every control connection — over ADB or Direct
 * TLS — asks here after its `hello`, before the phone answers:
 *
 *  1. **One PC at a time.** While a PC is connected, another one is turned away with a plain
 *     reason (error code `busy`). The same PC is always let through: that is a handoff between
 *     links (Wi-Fi to USB, D-081), not a second PC.
 *  2. **The picked PC.** When the owner picked a PC on the phone, the other PCs it knows are
 *     turned away even while the phone is free. A PC it has never paired with is let through —
 *     setting the phone up on a new PC is a choice in itself — and becomes the picked PC once it
 *     has paired ([paired]).
 *
 * A v32 session whose transport died and that waits for its PC to come back ([setWaiting]) is not
 * a connected PC: another PC may come in, and the waiting session ends once that one is admitted.
 *
 * Picking a PC on the phone drops any other PC's link at once ([pick]).
 */
class PcGate(private val context: Context) {

    sealed interface Answer {
        data class Admit(val ticket: Long) : Answer
        data class Refuse(val message: String) : Answer
    }

    internal class Live(var key: String, val name: String, val close: () -> Unit, var waiting: Boolean = false)

    private val live = mutableMapOf<Long, Live>()
    private var nextTicket = 0L
    private var lastRefusal: Pair<String, Long>? = null

    /** The id of the PC connected now, when it has one (older PCs don't send it until paired). */
    @Volatile
    var currentPcId: String? = null
        private set

    @Synchronized
    fun admit(pcId: String?, host: String?, close: () -> Unit): Answer {
        val known = TransportStore.pc(context, pcId) ?: if (pcId == null) TransportStore.pcNamed(context, host) else null
        val id = pcId ?: known?.id
        val key = id ?: "host:${host ?: "unknown"}"
        val name = host ?: known?.name ?: "this PC"

        blocking(live.values, key)?.let { other ->
            return refuse("${Build.MODEL} is connected to ${other.name} right now. Disconnect it there, " +
                "or pick $name in Linc on the phone (Connection › PCs).")
        }
        val selected = TransportStore.selectedPcId(context)
        if (selected != null && known != null && known.id != selected) {
            val selectedName = TransportStore.pc(context, selected)?.name ?: "another PC"
            return refuse("${Build.MODEL} is set to connect to $selectedName only. To use it with $name, " +
                "pick $name in Linc on the phone (Connection › PCs).")
        }

        val ticket = ++nextTicket
        live[ticket] = Live(key, name, close)
        currentPcId = id
        _connectedName.value = name
        if (known != null) TransportStore.markSeen(context, known.id, host)
        return Answer.Admit(ticket)
    }

    /** A v32 session lost its transport and waits for a resume ([waiting]), or got one back. */
    @Synchronized
    fun setWaiting(ticket: Long, waiting: Boolean) {
        live[ticket]?.waiting = waiting
    }

    @Synchronized
    fun release(ticket: Long) {
        live.remove(ticket)
        if (live.isEmpty()) {
            currentPcId = null
            _connectedName.value = null
        }
    }

    /**
     * The live link's PC handed over its certificate (`tls.exchange`, on every connect): store it,
     * and know the link by the PC's id from now on. A PC that was new to the phone becomes the
     * picked one, if a different one was picked — the owner just set the phone up there, so that
     * is where they mean to use it.
     */
    @Synchronized
    fun exchanged(ticket: Long, host: String?, certB64: String, tlsPort: Int, reversePort: Int) {
        val wasNew = TransportStore.pc(context, TransportStore.idOf(certB64)) == null
        val pc = TransportStore.saveExchange(context, host, certB64, tlsPort, reversePort)
        live[ticket]?.key = pc.id
        currentPcId = pc.id
        val selected = TransportStore.selectedPcId(context)
        if (wasNew && selected != null && selected != pc.id) {
            TransportStore.selectPc(context, pc.id)
            LogStore.log(LogLevel.INFO, "Now connecting to ${pc.name}: you just set this phone up on it")
        }
    }

    /** The owner picked a PC (null = automatic): drop any link to a different one. */
    fun pick(id: String?) {
        TransportStore.selectPc(context, id)
        val drops = synchronized(this) {
            if (id == null) emptyList() else live.values.filter { it.key != id }.map { it.name to it.close }
        }
        for ((name, close) in drops) {
            LogStore.log(LogLevel.INFO, "Disconnected from $name: you picked another PC")
            runCatching { close() }
        }
        ReconnectSignal.poke("you picked a PC")
    }

    /** Linc is stopping: end every PC link, so a later Start begins clean (D-091). */
    fun dropAll() {
        val drops = synchronized(this) { live.values.map { it.close } }
        drops.forEach { runCatching { it() } }
    }

    /** Forget a PC: drop its link if it is the one connected. */
    fun forget(id: String) {
        val drops = synchronized(this) { live.values.filter { it.key == id }.map { it.close } }
        TransportStore.forgetPc(context, id)
        drops.forEach { runCatching { it() } }
    }

    private fun refuse(message: String): Answer {
        // A turned-away PC retries every few seconds; say so in the log once a minute, not each time.
        val now = System.currentTimeMillis()
        val last = lastRefusal
        if (last == null || last.first != message || now - last.second > 60_000) {
            LogStore.log(LogLevel.INFO, "Turned a PC away: $message")
            lastRefusal = message to now
        }
        return Answer.Refuse(message)
    }

    companion object {
        /** Rule 1: the live link that keeps the PC known as [key] out, if any. A session waiting for its PC doesn't. */
        internal fun blocking(live: Collection<Live>, key: String): Live? = live.firstOrNull { it.key != key && !it.waiting }

        private val _connectedName = MutableStateFlow<String?>(null)

        /** The name of the PC the phone is connected to now, for the UI. */
        val connectedName: StateFlow<String?> = _connectedName

        /** The live gate, while the companion service runs — the UI picks and forgets through it. */
        @Volatile
        var instance: PcGate? = null
    }
}
