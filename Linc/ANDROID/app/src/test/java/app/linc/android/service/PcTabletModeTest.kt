package app.linc.android.service

import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** v22 Windows tablet mode (D-074): the PC's report, and the v22 gate on asking. */
class PcTabletModeTest {

    private val sent = mutableListOf<Envelope>()
    private val regs = mutableListOf<CompanionOutbox.Registration>()

    @After
    fun release() { regs.forEach { CompanionOutbox.release(it) }; regs.clear() }

    @Test
    fun `the PC's report is read in full`() {
        PcTabletMode.onState(buildJsonObject { put("on", true); put("scale", 200); put("windows", "11") })
        val s = PcTabletMode.state.value
        assertTrue(s.known && s.on)
        assertEquals(200, s.scale)
        assertEquals("11", s.windows)

        PcTabletMode.onState(buildJsonObject { put("on", false); put("error", "declined") })
        assertFalse(PcTabletMode.state.value.on)
        assertEquals("declined", PcTabletMode.state.value.error)
    }

    @Test
    fun `only a v22 PC is asked`() {
        regs += CompanionOutbox.register(21) { sent += it }
        assertFalse(PcTabletMode.ready())
        PcTabletMode.set(true)
        assertTrue(sent.isEmpty())
        regs.forEach { CompanionOutbox.release(it) }; regs.clear()

        regs += CompanionOutbox.register(22) { sent += it }
        assertTrue(PcTabletMode.ready())
        PcTabletMode.set(true)
        assertEquals(MessageType.PC_TABLETMODE_SET, sent.single().type)
        assertEquals(JsonPrimitive(true), sent.single().payload["on"])
    }
}
