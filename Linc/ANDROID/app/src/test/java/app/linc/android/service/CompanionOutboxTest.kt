package app.linc.android.service

import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class CompanionOutboxTest {

    private val sent = mutableListOf<Envelope>()
    private val regs = mutableListOf<CompanionOutbox.Registration>()
    private fun ping() = Envelope(type = MessageType.PING)

    /** Register a control connection and remember it so tearDown can release it. */
    private fun register(version: Int) =
        CompanionOutbox.register(version) { sent += it }.also { regs += it }

    @After
    fun tearDown() {
        regs.forEach { CompanionOutbox.release(it) }
        regs.clear()
    }

    @Test
    fun `drops sends when nothing is registered`() {
        CompanionOutbox.trySend(ping(), requiredVersion = 1)
        assertEquals(0, sent.size)
    }

    @Test
    fun `drops sends requiring a newer version than negotiated`() {
        register(1)
        CompanionOutbox.trySend(ping(), requiredVersion = 2)
        assertEquals(0, sent.size)
    }

    @Test
    fun `pre-v6 links ignore topics entirely`() {
        register(5)
        CompanionOutbox.trySend(ping(), requiredVersion = 2, topic = "notifications")
        assertEquals(1, sent.size)
    }

    @Test
    fun `v6 links gate topic sends on subscription`() {
        register(6)
        CompanionOutbox.trySend(ping(), requiredVersion = 2, topic = "notifications")
        assertEquals(0, sent.size) // not subscribed yet

        CompanionOutbox.subscribe("notifications")
        CompanionOutbox.trySend(ping(), requiredVersion = 2, topic = "notifications")
        assertEquals(1, sent.size)

        CompanionOutbox.unsubscribe("notifications")
        CompanionOutbox.trySend(ping(), requiredVersion = 2, topic = "notifications")
        assertEquals(1, sent.size) // dropped again
    }

    @Test
    fun `v6 links still deliver untopiced sends`() {
        register(6)
        CompanionOutbox.trySend(ping(), requiredVersion = 1)
        assertEquals(1, sent.size)
    }

    @Test
    fun `subscriptions reset on re-register`() {
        register(6)
        CompanionOutbox.subscribe("media")
        register(6) // new control connection is the current one, with no subscriptions
        CompanionOutbox.trySend(ping(), requiredVersion = 1, topic = "media")
        assertEquals(0, sent.size)
    }

    @Test
    fun `canSend reflects the newest live registration`() {
        assertFalse(CompanionOutbox.canSend(1))
        register(14)
        assertTrue(CompanionOutbox.canSend(14))
        assertFalse(CompanionOutbox.canSend(15))
    }

    @Test
    fun `releasing one control connection falls back to another (the concurrent-transport bug)`() {
        // Two control links up at once (e.g. ADB + Direct TLS). A short-lived one dying must
        // NOT silence sends — the older, still-live connection keeps delivering.
        register(14)                     // the survivor
        val shortLived = register(14)    // the newer, currently-current connection
        CompanionOutbox.release(shortLived)
        regs.remove(shortLived)

        CompanionOutbox.trySend(ping(), requiredVersion = 14)
        assertEquals(1, sent.size) // falls back to the surviving connection
        assertTrue(CompanionOutbox.canSend(14))
    }
}
