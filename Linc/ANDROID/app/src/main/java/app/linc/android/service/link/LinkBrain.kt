package app.linc.android.service.link

import java.security.MessageDigest
import kotlin.random.Random
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

/**
 * The phone's share of LincLink's decisions (linclink.md §5), pure; the PC's `LinkBrain.cs` holds
 * the rest. Times in milliseconds.
 */
object LinkBrain {
    /** The protocol version that brings LincLink. */
    const val VERSION = 32

    /** This phone's LincLink TLS listener, which the PC dials. */
    const val LISTEN_PORT = 46002

    /** A session with no transport is kept this long, waiting for the PC to resume it. */
    const val SESSION_KEEP_MS = 60_000L

    /** The PC echoes every 4 s when idle; this long with nothing from it means the transport is dead. */
    const val PEER_SILENCE_MS = 12_000L

    const val BACKOFF_BASE_MS = 500L
    const val BACKOFF_CAP_MS = 30_000L

    /** Decorrelated jitter, as on the PC: random between the base and three times the last wait, capped. */
    fun nextBackoffMs(previousMs: Long, random: Random = Random.Default): Long {
        val high = maxOf(BACKOFF_BASE_MS, minOf(BACKOFF_CAP_MS, previousMs * 3))
        return if (high <= BACKOFF_BASE_MS) BACKOFF_BASE_MS else random.nextLong(BACKOFF_BASE_MS, high + 1)
    }

    /** "192.168.1.0/24" for the IPv4 address bytes [address] with [prefix] bits. */
    fun subnetOf(address: ByteArray, prefix: Int): String {
        val bits = address.fold(0L) { acc, b -> (acc shl 8) or (b.toLong() and 0xff) }
        val mask = if (prefix <= 0) 0L else (0xffffffffL shl (32 - prefix)) and 0xffffffffL
        val net = bits and mask
        return "${net shr 24 and 0xff}.${net shr 16 and 0xff}.${net shr 8 and 0xff}.${net and 0xff}/$prefix"
    }

    /**
     * A short id for the network the phone is on, from what Android gives without the location
     * permission (the gateway and the subnet; the SSID when it happens to be readable). Nothing
     * about the network can be read back from it.
     */
    fun fingerprint(gateway: String?, subnet: String?, ssid: String? = null): String {
        val raw = listOf(gateway.orEmpty().trim(), subnet.orEmpty().trim(), ssid.orEmpty().trim().removeSurrounding("\"")).joinToString("|")
        return MessageDigest.getInstance("SHA-256").digest(raw.toByteArray()).joinToString("") { "%02x".format(it) }.substring(0, 16)
    }
}

/**
 * The address of each PC that last worked, per network (linclink.md §6), so the dial after a
 * network change starts with the right one instead of the last network's. Holds addresses only,
 * never a secret. Pure: serialised to one string by the caller.
 */
class NetMemory {
    @Serializable
    data class Entry(val network: String, val pcId: String, val endpoint: String, val lastOkMs: Long, val failStreak: Int = 0)

    private val entries = mutableListOf<Entry>()

    fun remember(network: String, pcId: String, endpoint: String, nowMs: Long) {
        entries.removeAll { it.network == network && it.pcId == pcId }
        entries += Entry(network, pcId, endpoint, nowMs)
        while (entries.size > MAX_ENTRIES) entries.remove(entries.minBy { it.lastOkMs })
    }

    /** A remembered endpoint failed; forgotten after [FORGET_AFTER] in a row. */
    fun failed(network: String, pcId: String, endpoint: String) {
        val index = entries.indexOfFirst { it.network == network && it.pcId == pcId && it.endpoint == endpoint }
        if (index < 0) return
        val streak = entries[index].failStreak + 1
        if (streak >= FORGET_AFTER) entries.removeAt(index) else entries[index] = entries[index].copy(failStreak = streak)
    }

    fun best(network: String, pcId: String): String? =
        entries.firstOrNull { it.network == network && it.pcId == pcId }?.endpoint

    fun forgetPc(pcId: String) {
        entries.removeAll { it.pcId == pcId }
    }

    fun save(): String = json.encodeToString(entries.toList())

    companion object {
        const val MAX_ENTRIES = 32
        const val FORGET_AFTER = 5
        private val json = Json { ignoreUnknownKeys = true }

        fun load(text: String?): NetMemory = NetMemory().also { memory ->
            if (!text.isNullOrBlank()) {
                runCatching { json.decodeFromString<List<Entry>>(text) }.getOrNull()?.let { memory.entries += it }
            }
        }
    }
}
