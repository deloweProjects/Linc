package app.linc.android.service

import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import kotlinx.serialization.json.jsonPrimitive
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

/**
 * Coverage for the slideshow clicker (M4c Part B): the pure [PresentationKeys] mapping, and
 * [PresentationControl]'s real send path through the same [CompanionOutbox] registration harness
 * [PcControlTest] and [MirrorControlTest] use — so this calls the production send, not a copy of it.
 *
 * The virtual-key codes are asserted as literals on purpose. Asserting `keyFor(Next)` equals
 * `KeyboardMapping.VK_RIGHT` would pass no matter what either side was changed to; the PC end of
 * this is Win32's VK_RIGHT and the number is the contract.
 */
class PresentationKeysTest {

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
    fun `next sends VK_RIGHT`() {
        assertEquals(0x27, PresentationKeys.keyFor(PresentationKeys.Action.Next))
    }

    @Test
    fun `previous sends VK_LEFT`() {
        assertEquals(0x25, PresentationKeys.keyFor(PresentationKeys.Action.Previous))
    }

    @Test
    fun `start sends VK_F5`() {
        assertEquals(0x74, PresentationKeys.keyFor(PresentationKeys.Action.Start))
    }

    @Test
    fun `end sends VK_ESCAPE`() {
        assertEquals(0x1B, PresentationKeys.keyFor(PresentationKeys.Action.End))
    }

    @Test
    fun `black screen sends VK_B`() {
        assertEquals(0x42, PresentationKeys.keyFor(PresentationKeys.Action.Black))
    }

    @Test
    fun `next and previous reuse KeyboardMapping's codes rather than a second copy`() {
        // M4a merged two drifting keyboard mappings into one; this fails if a third starts.
        assertEquals(KeyboardMapping.VK_RIGHT, PresentationKeys.keyFor(PresentationKeys.Action.Next))
        assertEquals(KeyboardMapping.VK_LEFT, PresentationKeys.keyFor(PresentationKeys.Action.Previous))
    }

    @Test
    fun `every action maps to a distinct key`() {
        val codes = PresentationKeys.Action.entries.map { PresentationKeys.keyFor(it) }
        assertEquals(PresentationKeys.Action.entries.size, codes.toSet().size)
    }

    @Test
    fun `the alternates the mapping deliberately does not send are different keys`() {
        // Right/Down/Space all mean "next" to presentation software and Left/Up both mean
        // "previous", but exactly one may be sent or one tap would advance three slides.
        val next = PresentationKeys.keyFor(PresentationKeys.Action.Next)
        assertNotEquals(PresentationKeys.VK_DOWN, next)
        assertNotEquals(PresentationKeys.VK_SPACE, next)
        assertNotEquals(PresentationKeys.VK_UP, PresentationKeys.keyFor(PresentationKeys.Action.Previous))
    }

    @Test
    fun `send emits a pc_input key down then up with that key code`() {
        register(14)

        PresentationControl.send(PresentationKeys.Action.Next)

        assertEquals(2, sent.size)
        assertEquals(MessageType.PC_INPUT, sent[0].type)
        assertEquals("key", sent[0].payload["kind"]!!.jsonPrimitive.content)
        assertEquals("39", sent[0].payload["keyCode"]!!.jsonPrimitive.content) // 0x27
        assertEquals("true", sent[0].payload["down"]!!.jsonPrimitive.content)

        assertEquals(MessageType.PC_INPUT, sent[1].type)
        assertEquals("39", sent[1].payload["keyCode"]!!.jsonPrimitive.content)
        assertEquals("false", sent[1].payload["down"]!!.jsonPrimitive.content)
    }

    @Test
    fun `send emits no mirror start - the clicker needs no streaming session`() {
        register(14)

        PresentationControl.send(PresentationKeys.Action.Start)

        // Both envelopes are pc.input; nothing asks the PC to start streaming. "Start" here is
        // F5 on the PC's keyboard, not a mirror session.
        assertEquals(2, sent.size)
        assertEquals(listOf(MessageType.PC_INPUT, MessageType.PC_INPUT), sent.map { it.type })
        assertEquals("116", sent[0].payload["keyCode"]!!.jsonPrimitive.content) // 0x74 = VK_F5
    }

    @Test
    fun `nothing sends when no v14 desktop is connected`() {
        register(13) // pc.input didn't exist yet on this link

        PresentationControl.send(PresentationKeys.Action.Next)

        assertEquals(0, sent.size)
    }
}
