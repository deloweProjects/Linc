package app.linc.android.service

/**
 * The phone's redial rules for Direct TLS (D-081, Vibe/agent/design/connection.md §2.6). Pure, so
 * every rule is unit-tested; [PresenceClient] only asks it what to do.
 *
 *  - **After a poke** (NFC tap, Wi-Fi joined, screen on, Linc opened, the PC link ending) the
 *    dialler tries at once, then after 1 s and 2 s — mDNS needs a moment to answer after a network
 *    change — and then falls back to its normal pace.
 *  - **Normal pace**: 3 s while things work, doubling per miss to 30 s while the PC is away.
 *  - **Dial order**: the USB reverse tunnel (always reachable when it exists), then the last
 *    address that worked (no discovery needed — the fast path back onto the same Wi-Fi), then
 *    whatever mDNS advertised.
 *  - **Forgetting**: a remembered address that failed [FORGET_AFTER] times in a row is dropped.
 */
object ReconnectPolicy {
    const val BASE_DELAY_MS = 3_000L
    const val MAX_DELAY_MS = 30_000L
    const val FORGET_AFTER = 5

    /** Delays after a poke: the dial right after it, then these. */
    val BURST_MS = listOf(1_000L, 2_000L)

    /**
     * How long to wait before the next dial. [missStreak] counts consecutive failed dials;
     * [missesSincePoke] counts failed dials since the last poke, or null when the burst is over.
     */
    fun delayMs(missStreak: Int, missesSincePoke: Int?): Long {
        if (missesSincePoke != null && missesSincePoke in 1..BURST_MS.size) {
            return BURST_MS[missesSincePoke - 1]
        }
        if (missStreak <= 0) return BASE_DELAY_MS
        val doublings = (missStreak - 1).coerceAtMost(4)
        return (BASE_DELAY_MS shl doublings).coerceAtMost(MAX_DELAY_MS)
    }

    /** True once a poke's burst is spent and the normal pace takes over. */
    fun burstOver(missesSincePoke: Int): Boolean = missesSincePoke > BURST_MS.size

    /** Where to dial, best first, without duplicates or empty ports. */
    fun order(
        reverse: Pair<String, Int>?,
        lastGood: Pair<String, Int>?,
        advertised: List<Pair<String, Int>>,
    ): List<Pair<String, Int>> =
        (listOfNotNull(reverse, lastGood) + advertised).distinct().filter { it.second > 0 && it.first.isNotBlank() }

    /** Should the remembered address be forgotten after [failures] misses in a row? */
    fun shouldForget(failures: Int): Boolean = failures >= FORGET_AFTER

    /**
     * "host:port" as stored. IPv6 hosts are bracketed so the last colon is always the port.
     */
    fun format(endpoint: Pair<String, Int>): String =
        if (endpoint.first.contains(':')) "[${endpoint.first}]:${endpoint.second}" else "${endpoint.first}:${endpoint.second}"

    fun parse(text: String?): Pair<String, Int>? {
        val value = text?.trim().orEmpty()
        val colon = value.lastIndexOf(':')
        if (colon <= 0) return null
        val host = value.substring(0, colon).removePrefix("[").removeSuffix("]")
        val port = value.substring(colon + 1).toIntOrNull() ?: return null
        return if (host.isBlank() || port !in 1..65535) null else host to port
    }
}
