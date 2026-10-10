package app.linc.android.service

import app.linc.android.protocol.MessageType
import app.linc.android.protocol.Envelope
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** v21 Voice Access (D-070): the PC's state report, and who may be asked. */
class PcVoiceAccessTest {

    private val sent = mutableListOf<Envelope>()
    private val regs = mutableListOf<CompanionOutbox.Registration>()

    @After
    fun release() { regs.forEach { CompanionOutbox.release(it) }; regs.clear() }

    @Test
    fun `the PC's state report is read in full`() {
        PcVoiceAccess.onState(buildJsonObject {
            put("available", true); put("on", true); put("mic", "phone"); put("cable", true)
        })
        val s = PcVoiceAccess.state.value
        assertTrue(s.available && s.on && s.cable)
        assertEquals("phone", s.mic)

        PcVoiceAccess.onState(buildJsonObject { put("available", true); put("error", "nope") })
        assertFalse(PcVoiceAccess.state.value.on)
        assertEquals("nope", PcVoiceAccess.state.value.error)
    }

    @Test
    fun `a PC that can set up the microphone itself says so (D-071)`() {
        PcVoiceAccess.onState(buildJsonObject { put("available", true); put("cable", false); put("setup", "needed") })
        assertTrue(PcVoiceAccess.state.value.canSetUp)
        assertFalse(PcVoiceAccess.state.value.settingUp)

        PcVoiceAccess.onState(buildJsonObject { put("available", true); put("setup", "installing") })
        assertTrue(PcVoiceAccess.state.value.settingUp)
        assertFalse(PcVoiceAccess.state.value.canSetUp)

        PcVoiceAccess.onState(buildJsonObject { put("available", true); put("cable", true); put("setup", "ready") })
        assertFalse(PcVoiceAccess.state.value.canSetUp)

        // While downloading the phone shows how far it has got, then what to do on the PC.
        PcVoiceAccess.onState(buildJsonObject { put("available", true); put("setup", "installing"); put("progress", 40) })
        assertEquals(40, PcVoiceAccess.state.value.progress)
        assertTrue("40 %" in PcVoiceAccess.state.value.setupText)
        PcVoiceAccess.onState(buildJsonObject { put("available", true); put("setup", "installing"); put("progress", 100) })
        assertTrue("Install Driver" in PcVoiceAccess.state.value.setupText)

        // An older PC never sends "setup": the phone must not offer what it can't do.
        PcVoiceAccess.onState(buildJsonObject { put("available", true); put("cable", false) })
        assertFalse(PcVoiceAccess.state.value.canSetUp)
    }

    @Test
    fun `asking for state needs a v21 desktop`() {
        regs += CompanionOutbox.register(20) { sent += it }
        assertFalse(PcVoiceAccess.ready())
        regs.forEach { CompanionOutbox.release(it) }; regs.clear()

        regs += CompanionOutbox.register(21) { sent += it }
        assertTrue(PcVoiceAccess.ready())
        CompanionOutbox.trySend(Envelope(type = MessageType.PC_VOICEACCESS_GET, payload = buildJsonObject {}), requiredVersion = 21)
        assertEquals(MessageType.PC_VOICEACCESS_GET, sent.single().type)
        assertEquals(JsonPrimitive(21).content, "21")
    }

    @Test
    fun `the floating keyboard's bottom row has a voice key and still fits`() {
        val bottom = PcKeyboardLayout.rows(fn = false).last()
        assertTrue(bottom.any { it.action == PcKeyboardLayout.Action.Voice })
        assertEquals(PcKeyboardLayout.ROW_UNITS, bottom.sumOf { it.width.toDouble() }.toFloat(), 0.001f)
    }
}
