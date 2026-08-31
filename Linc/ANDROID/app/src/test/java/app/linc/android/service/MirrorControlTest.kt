package app.linc.android.service

import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import kotlinx.serialization.json.jsonPrimitive
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Coverage for the trackpad-facing [MirrorControl] sends added in M4a: a tap is a click at the
 * PC's current cursor position (down then up, no move), and a trackpad drag/scroll carry the
 * right `pc.input` shape. Uses the same fake-registration harness as [CompanionOutboxTest] so
 * this calls the real send path, not a re-implementation of it.
 */
class MirrorControlTest {

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
    fun `a tap sends down then up, both in trackpad mode, with no movement`() {
        register(14)

        MirrorControl.click(down = true)
        MirrorControl.click(down = false)

        assertEquals(2, sent.size)
        assertEquals(MessageType.PC_INPUT, sent[0].type)
        assertEquals("down", sent[0].payload["kind"]!!.jsonPrimitive.content)
        assertEquals("trackpad", sent[0].payload["mode"]!!.jsonPrimitive.content)
        assertNull(sent[0].payload["dx"])
        assertNull(sent[0].payload["dy"])
        assertEquals("up", sent[1].payload["kind"]!!.jsonPrimitive.content)
        assertEquals("trackpad", sent[1].payload["mode"]!!.jsonPrimitive.content)
    }

    @Test
    fun `a trackpad drag sends a relative move with pixel deltas`() {
        register(14)

        MirrorControl.pointerRelative("move", dx = 20, dy = -8)

        assertEquals(1, sent.size)
        val payload = sent[0].payload
        assertEquals("move", payload["kind"]!!.jsonPrimitive.content)
        assertEquals("trackpad", payload["mode"]!!.jsonPrimitive.content)
        assertEquals("20", payload["dx"]!!.jsonPrimitive.content)
        assertEquals("-8", payload["dy"]!!.jsonPrimitive.content)
    }

    @Test
    fun `a two-finger drag sends a scroll`() {
        register(14)

        MirrorControl.scroll(dx = 0, dy = 1)

        assertEquals(1, sent.size)
        val payload = sent[0].payload
        assertEquals("scroll", payload["kind"]!!.jsonPrimitive.content)
        assertEquals("1", payload["dy"]!!.jsonPrimitive.content)
    }

    @Test
    fun `a tap sends nothing when no v14 desktop is connected`() {
        MirrorControl.click(down = true)
        assertEquals(0, sent.size)
    }
}
