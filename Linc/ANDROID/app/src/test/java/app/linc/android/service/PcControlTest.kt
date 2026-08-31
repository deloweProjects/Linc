package app.linc.android.service

import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Coverage for [PcControl]'s send path (v17, M4b, D-042): every action sends `pc.control` AND
 * follows it with a `pc.state.get` (PROTOCOL.md has no unsolicited state push, so this is the
 * only re-sync point), and [PcControl.onState] parses a real `pc.state` reply into [PcControl.state].
 * Uses the same real-registration harness as [MirrorControlTest] so this calls the actual send
 * path, not a re-implementation of it.
 */
class PcControlTest {

    private val sent = mutableListOf<Envelope>()
    private val regs = mutableListOf<CompanionOutbox.Registration>()

    private fun register(version: Int) =
        CompanionOutbox.register(version) { sent += it }.also { regs += it }

    @After
    fun tearDown() {
        regs.forEach { CompanionOutbox.release(it) }
        regs.clear()
    }

    @Test
    fun `shutdown sends pc_control with confirm true, then pc_state_get`() {
        register(17)

        PcControl.shutdown()

        assertEquals(2, sent.size)
        assertEquals(MessageType.PC_CONTROL, sent[0].type)
        assertEquals("shutdown", sent[0].payload["action"]!!.jsonPrimitive.content)
        assertEquals(true, sent[0].payload["confirm"]!!.jsonPrimitive.content.toBoolean())
        assertEquals(MessageType.PC_STATE_GET, sent[1].type)
    }

    @Test
    fun `lock sends pc_control with no confirm, then pc_state_get`() {
        register(17)

        PcControl.lock()

        assertEquals(2, sent.size)
        assertNull(sent[0].payload["confirm"])
        assertEquals(MessageType.PC_STATE_GET, sent[1].type)
    }

    @Test
    fun `nothing sends when no v17 desktop is connected`() {
        register(16) // v17 didn't exist yet on this link

        PcControl.shutdown()

        assertEquals(0, sent.size)
    }

    @Test
    fun `requestState alone sends only pc_state_get`() {
        register(17)

        PcControl.requestState()

        assertEquals(1, sent.size)
        assertEquals(MessageType.PC_STATE_GET, sent[0].type)
    }

    @Test
    fun `onState parses a real pc_state payload into the exposed state`() {
        val payload = buildJsonObject {
            put("volume", 42)
            put("muted", true)
            put("brightness", 80)
            put("canBrightness", true)
            put("canSleep", false)
            put("canShutdown", true)
        }

        PcControl.onState(payload)

        val state = PcControl.state.value
        assertEquals(42, state?.volume)
        assertEquals(true, state?.muted)
        assertEquals(80, state?.brightness)
        assertTrue(state?.canBrightness == true)
        assertEquals(false, state?.canSleep)
        assertEquals(true, state?.canShutdown)
    }

    @Test
    fun `onState with a null brightness parses to a null brightness (unreadable)`() {
        val payload = buildJsonObject {
            put("volume", 10)
            put("muted", false)
            put("canBrightness", false)
            put("canSleep", true)
            put("canShutdown", true)
        }

        PcControl.onState(payload)

        assertNull(PcControl.state.value?.brightness)
    }
}
