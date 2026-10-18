package app.linc.android.service

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The phone's Direct-TLS redial rules (D-081). */
class ReconnectPolicyTest {

    @Test
    fun `after a poke the dialler retries at 1 s and 2 s, then its normal pace`() {
        assertEquals(1_000L, ReconnectPolicy.delayMs(missStreak = 1, missesSincePoke = 1))
        assertEquals(2_000L, ReconnectPolicy.delayMs(missStreak = 2, missesSincePoke = 2))
        assertTrue(ReconnectPolicy.burstOver(3))
        assertFalse(ReconnectPolicy.burstOver(2))
        assertEquals(12_000L, ReconnectPolicy.delayMs(missStreak = 3, missesSincePoke = null))
    }

    @Test
    fun `normal pace is 3 s while working and doubles to a 30 s cap while the PC is away`() {
        val delays = (0..8).map { ReconnectPolicy.delayMs(it, null) }
        assertEquals(listOf(3_000L, 3_000L, 6_000L, 12_000L, 24_000L, 30_000L, 30_000L, 30_000L, 30_000L), delays)
    }

    @Test
    fun `dial order is the USB tunnel, then the address that last worked, then mDNS`() {
        val reverse = "127.0.0.1" to 46011
        val last = "192.168.1.18" to 46001
        val advertised = listOf("192.168.1.18" to 46001, "26.3.178.175" to 46001)
        assertEquals(
            listOf(reverse, last, "26.3.178.175" to 46001),
            ReconnectPolicy.order(reverse, last, advertised),
        )
        assertEquals(listOf(last), ReconnectPolicy.order(null, last, emptyList()))
        assertEquals(emptyList<Pair<String, Int>>(), ReconnectPolicy.order(null, "x" to 0, emptyList()))
    }

    @Test
    fun `a remembered address is forgotten after five misses in a row`() {
        assertFalse(ReconnectPolicy.shouldForget(4))
        assertTrue(ReconnectPolicy.shouldForget(5))
    }

    @Test
    fun `endpoints round-trip through storage, IPv6 included`() {
        for (e in listOf("192.168.137.1" to 46001, "fe80::1" to 46001)) {
            assertEquals(e, ReconnectPolicy.parse(ReconnectPolicy.format(e)))
        }
        assertNull(ReconnectPolicy.parse(null))
        assertNull(ReconnectPolicy.parse("nonsense"))
        assertNull(ReconnectPolicy.parse("host:99999"))
    }
}
