package app.linc.android.service

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** The gate's one-PC rule (D-088), with v32 sessions that wait for their PC to come back. */
class PcGateTest {
    private fun live(key: String, waiting: Boolean = false) = PcGate.Live(key, "PC $key", {}, waiting)

    @Test
    fun `a connected PC keeps another one out, never itself`() {
        val a = live("a")
        assertEquals(a, PcGate.blocking(listOf(a), "b"))
        assertNull(PcGate.blocking(listOf(a), "a"))
        assertNull(PcGate.blocking(emptyList(), "b"))
    }

    @Test
    fun `a session waiting for its PC doesn't keep another PC out`() {
        assertNull(PcGate.blocking(listOf(live("a", waiting = true)), "b"))
        // ...but a live link next to it still does.
        val connected = live("c")
        assertEquals(connected, PcGate.blocking(listOf(live("a", waiting = true), connected), "b"))
    }
}
