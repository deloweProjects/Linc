package app.linc.android.service

import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Dial every candidate address at once and keep the first that answers.
 *
 * The PC advertises all its LAN addresses (D-022), and on a multi-adapter PC — Wi-Fi plus a
 * hotspot, a VPN, a virtual switch — some of them are unreachable from the phone. Dialled one
 * after another, each dead address cost a full connect timeout (4 s) before the live one was
 * tried, and PC Remote opens two channels (video, then sound) that way: that was most of its
 * "Loading your PC…" wait. Raced, a channel opens as fast as the reachable route allows.
 *
 * Losers that connect after the winner are closed straight away. Callers race cheap probes
 * (a plain TCP connect), not the real authenticated connection — see PresenceClient.
 */
object EndpointRace {

    fun <E, T : Any> first(
        endpoints: List<E>,
        timeoutMs: Long,
        connect: (E) -> T?,
        close: (T) -> Unit,
    ): T? {
        if (endpoints.isEmpty()) return null
        if (endpoints.size == 1) return runCatching { connect(endpoints[0]) }.getOrNull()

        // One slot per attempt: a result, or a miss (null) so the caller knows when all failed.
        val results = LinkedBlockingQueue<Result<T?>>()
        val decided = AtomicBoolean(false)
        endpoints.forEach { endpoint ->
            Thread({
                val outcome = runCatching { connect(endpoint) }
                // A winner already taken: this late connection is surplus.
                if (decided.get()) outcome.getOrNull()?.let { runCatching { close(it) } }
                else results.put(outcome)
            }, "linc-dial").apply { isDaemon = true }.start()
        }

        val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMs)
        var misses = 0
        while (misses < endpoints.size) {
            val left = deadline - System.nanoTime()
            if (left <= 0) break
            val outcome = results.poll(left, TimeUnit.NANOSECONDS) ?: break
            val value = outcome.getOrNull()
            if (value == null) { misses++; continue }
            if (decided.compareAndSet(false, true)) {
                // Close any others that finished between the winner and now.
                generateSequence { results.poll() }.forEach { late -> late.getOrNull()?.let { runCatching { close(it) } } }
                return value
            }
            runCatching { close(value) }
        }
        decided.set(true)
        generateSequence { results.poll() }.forEach { late -> late.getOrNull()?.let { runCatching { close(it) } } }
        return null
    }
}
