package app.linc.android.service

import java.util.Collections
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Channel dial-backs race the PC's addresses instead of waiting out each dead one. */
class EndpointRaceTest {

    private val closed = Collections.synchronizedList(mutableListOf<String>())

    /** "dead:N" waits N ms then fails; "live:N" answers after N ms. */
    private fun connect(endpoint: String): String? {
        val (kind, ms) = endpoint.split(":")
        Thread.sleep(ms.toLong())
        return if (kind == "live") endpoint else throw java.io.IOException("unreachable")
    }

    @Test
    fun aDeadAddressFirstNoLongerCostsItsTimeout() {
        val started = System.nanoTime()
        val winner = EndpointRace.first(listOf("dead:3000", "live:50"), 5_000, ::connect) { closed += it }
        val tookMs = (System.nanoTime() - started) / 1_000_000
        assertEquals("live:50", winner)
        assertTrue("took $tookMs ms", tookMs < 1_000)
    }

    @Test
    fun theFastestLiveAddressWinsAndLateOnesAreClosed() {
        val winner = EndpointRace.first(listOf("live:300", "live:20"), 5_000, ::connect) { closed += it }
        assertEquals("live:20", winner)
        Thread.sleep(500)
        assertEquals(listOf("live:300"), closed.toList())
    }

    @Test
    fun allDeadReturnsNullAsSoonAsTheyHaveAllFailed() {
        val started = System.nanoTime()
        assertNull(EndpointRace.first(listOf("dead:30", "dead:60"), 5_000, ::connect) { closed += it })
        assertTrue((System.nanoTime() - started) / 1_000_000 < 1_000)
    }

    @Test
    fun theOverallTimeoutIsHonoured() {
        val started = System.nanoTime()
        assertNull(EndpointRace.first(listOf("live:2000", "dead:2000"), 200, ::connect) { closed += it })
        assertTrue((System.nanoTime() - started) / 1_000_000 < 1_000)
        Thread.sleep(2_200)
        assertEquals(listOf("live:2000"), closed.toList()) // the straggler is not leaked
    }

    @Test
    fun aSingleEndpointIsJustDialled() {
        assertEquals("live:0", EndpointRace.first(listOf("live:0"), 1_000, ::connect) { closed += it })
        assertNull(EndpointRace.first(emptyList<String>(), 1_000, ::connect) { closed += it })
    }
}
