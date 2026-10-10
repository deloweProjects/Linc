package app.linc.android.service

import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import app.linc.android.service.MirrorSettings.PointerMode
import kotlinx.serialization.json.jsonPrimitive
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** v20 PC Remote (D-067): gestures, sticky modifiers, cursor decode, audio config, sends. */
class PcRemoteV20Test {

    // ---- PointerGestures -----------------------------------------------------

    private val actions = mutableListOf<String>()

    /** A 1000x500 view showing a 2000x1000 PC: 2 PC px per view px, no letterbox. */
    private fun gestures(mode: PointerMode) = PointerGestures(
        out = object : PointerGestures.Output {
            override fun pointerTo(x: Float, y: Float) { actions += "to ${x.toInt()},${y.toInt()}" }
            override fun button(button: String, down: Boolean) { actions += "$button ${if (down) "down" else "up"}" }
            override fun twoFinger(dx: Float, dy: Float) { actions += "two ${dx.toInt()},${dy.toInt()}" }
            override fun held() { actions += "held" }
        },
        mode = mode,
        toStream = { x, y -> if (x in 0f..1000f && y in 0f..500f) (x * 2) to (y * 2) else null },
        viewPerStream = { 0.5f },
        streamSize = { 2000 to 1000 },
    )

    @Test
    fun `touch mode tap moves the pointer under the finger then left-clicks`() {
        val g = gestures(PointerMode.TOUCH)
        g.down(100f, 50f, 0)
        g.up(100f, 50f, 80)
        assertEquals(listOf("to 200,100", "left down", "left up"), actions)
    }

    @Test
    fun `touch mode drag is a left-button drag from where the finger landed`() {
        val g = gestures(PointerMode.TOUCH)
        g.down(100f, 50f, 0)
        g.move(200f, 50f, 20)
        g.up(300f, 50f, 40)
        assertEquals(listOf("to 200,100", "left down", "to 400,100", "to 600,100", "left up"), actions)
    }

    @Test
    fun `press and hold then release is a right click`() {
        val g = gestures(PointerMode.TOUCH)
        g.down(100f, 50f, 0)
        g.holdTimeout(0)
        g.up(100f, 50f, 600)
        assertEquals(listOf("to 200,100", "held", "right down", "right up"), actions)
    }

    @Test
    fun `a stale hold timer from an earlier press is ignored`() {
        val g = gestures(PointerMode.TOUCH)
        g.down(100f, 50f, 0)
        g.up(100f, 50f, 50)
        actions.clear()
        g.down(100f, 50f, 1000)
        g.holdTimeout(0)          // the first press's timer, firing late
        g.up(100f, 50f, 1050)
        assertEquals(listOf("to 200,100", "left down", "left up"), actions)
    }

    @Test
    fun `a touch on the letterbox does nothing`() {
        val g = gestures(PointerMode.TOUCH)
        g.down(-5f, 50f, 0)
        g.up(-5f, 50f, 50)
        assertTrue(actions.isEmpty())
    }

    @Test
    fun `mouse mode drag moves the pointer relatively and a tap clicks where it is`() {
        val g = gestures(PointerMode.MOUSE)
        g.syncCursor(1000f, 500f)
        g.down(100f, 100f, 0)
        g.move(150f, 100f, 1000)      // slow: 50 view px -> 100 PC px at ~1x gain
        g.up(150f, 100f, 1010)
        assertTrue("no click from a drag", actions.none { it.startsWith("left") })
        assertTrue("moved right, roughly 1:1 in view terms", g.cursorX in 1090f..1130f)
        assertEquals(500f, g.cursorY)

        actions.clear()
        g.down(400f, 300f, 2000)
        g.up(400f, 300f, 2050)
        assertEquals(listOf("left down", "left up"), actions)   // at the pointer, no jump
    }

    @Test
    fun `mouse mode hold then drag is a left drag`() {
        val g = gestures(PointerMode.MOUSE)
        g.syncCursor(1000f, 500f)
        g.down(100f, 100f, 0)
        g.holdTimeout(0)
        g.move(200f, 100f, 600)
        g.up(200f, 100f, 650)
        assertEquals("held", actions[0])
        assertEquals("left down", actions[1])
        assertEquals("left up", actions.last())
    }

    @Test
    fun `the pointer cannot leave the PC's screen`() {
        val g = gestures(PointerMode.MOUSE)
        g.syncCursor(1990f, 10f)
        g.down(100f, 100f, 0)
        g.move(900f, 0f, 10)
        assertEquals(1999f, g.cursorX)
        assertEquals(0f, g.cursorY)
    }

    @Test
    fun `a quick two-finger tap is a right click, a two-finger drag is not`() {
        val g = gestures(PointerMode.MOUSE)
        g.down(100f, 100f, 0)
        g.secondFingerDown(150f, 100f, 10)
        g.secondFingerUp(120)
        g.up(100f, 100f, 130)
        assertEquals(listOf("right down", "right up"), actions)

        actions.clear()
        g.down(100f, 100f, 1000)
        g.secondFingerDown(150f, 100f, 1010)
        g.twoFingerMove(150f, 160f, pinching = false)
        g.secondFingerUp(1100)
        g.up(100f, 160f, 1110)
        assertEquals(listOf("two 0,60"), actions)
    }

    @Test
    fun `a second finger ends a touch drag cleanly`() {
        val g = gestures(PointerMode.TOUCH)
        g.down(100f, 50f, 0)
        g.move(200f, 50f, 20)
        g.secondFingerDown(250f, 50f, 30)
        assertEquals("left up", actions.last())
    }

    @Test
    fun `the PC's pointer reports are ignored while a finger is down`() {
        val g = gestures(PointerMode.MOUSE)
        g.syncCursor(100f, 100f)
        g.down(0f, 0f, 0)
        g.syncCursor(900f, 900f)
        assertEquals(100f, g.cursorX)
        g.up(0f, 0f, 10)
        g.syncCursor(900f, 900f)
        assertEquals(900f, g.cursorX)
    }

    @Test
    fun `acceleration is 1x when slow and capped when fast`() {
        assertEquals(1f, PointerGestures.gain(0f), 0.001f)
        assertEquals(3.5f, PointerGestures.gain(100f), 0.001f)
    }

    // ---- ModifierKeys ----------------------------------------------------------

    @After
    fun clearModifiers() = ModifierKeys.clear()

    @Test
    fun `a one-shot Ctrl wraps the next key and then releases`() {
        val sent = mutableListOf<Pair<Int, Boolean>>()
        ModifierKeys.cycle(ModifierKeys.VK_CONTROL)
        ModifierKeys.chord(0x43) { vk, down -> sent += vk to down }   // C
        assertEquals(listOf(0x11 to true, 0x43 to true, 0x43 to false, 0x11 to false), sent)
        assertFalse(ModifierKeys.any())
    }

    @Test
    fun `a locked modifier stays on, and a third tap releases it`() {
        ModifierKeys.cycle(ModifierKeys.VK_SHIFT)
        ModifierKeys.cycle(ModifierKeys.VK_SHIFT)
        ModifierKeys.chord(0x41) { _, _ -> }
        assertTrue(ModifierKeys.any())
        ModifierKeys.cycle(ModifierKeys.VK_SHIFT)
        assertFalse(ModifierKeys.any())
    }

    @Test
    fun `modifiers are pressed Win-Ctrl-Alt-Shift and released in reverse`() {
        val sent = mutableListOf<Int>()
        ModifierKeys.cycle(ModifierKeys.VK_SHIFT)
        ModifierKeys.cycle(ModifierKeys.VK_CONTROL)
        ModifierKeys.chord(0x1B) { vk, down -> if (vk != 0x1B) sent += if (down) vk else -vk }
        assertEquals(listOf(0x11, 0x10, -0x10, -0x11), sent)
    }

    @Test
    fun `typed letters and digits map to their virtual keys`() {
        assertEquals(0x43, ModifierKeys.virtualKeyFor('c'))
        assertEquals(0x43, ModifierKeys.virtualKeyFor('C'))
        assertEquals(0x34, ModifierKeys.virtualKeyFor('4'))
        assertNull(ModifierKeys.virtualKeyFor('é'))
    }

    // ---- cursor image, link quality, audio config ------------------------------

    @Test
    fun `cursor pixels arrive as BGRA and become Android ARGB`() {
        val bytes = byteArrayOf(0x10, 0x20, 0x30, 0xFF.toByte(), 0, 0, 0, 0)  // B G R A, then clear
        val argb = MirrorSession.bgraToArgb(bytes, 2, 1)!!
        assertEquals(0xFF302010.toInt(), argb[0])
        assertEquals(0, argb[1])
        assertNull("wrong size is refused", MirrorSession.bgraToArgb(bytes, 3, 1))
    }

    @Test
    fun `link quality reads like Windows App's three steps`() {
        val healthy = LinkStats(6000, 3000, 30.0, 20, 0, 0)
        assertEquals(LinkStats.Quality.GOOD, healthy.quality(25))
        assertEquals(LinkStats.Quality.FAIR, healthy.quality(120))
        assertEquals(LinkStats.Quality.POOR, healthy.copy(delayMs = 400).quality(25))
        assertEquals(LinkStats.Quality.POOR, healthy.copy(fps = 4.0).quality(25))
    }

    @Test
    fun `AAC-LC config bytes for the PC's mix rates`() {
        assertArrayEquals(byteArrayOf(0x11, 0x90.toByte()), AudioReceiver.audioSpecificConfig(48000, 2))
        assertArrayEquals(byteArrayOf(0x12, 0x10), AudioReceiver.audioSpecificConfig(44100, 2))
    }

    // ---- MirrorControl v20 sends ------------------------------------------------

    private val sent = mutableListOf<Envelope>()
    private val regs = mutableListOf<CompanionOutbox.Registration>()

    @After
    fun releaseOutbox() {
        regs.forEach { CompanionOutbox.release(it) }
        regs.clear()
    }

    private fun connect(version: Int) {
        regs += CompanionOutbox.register(version) { sent += it }
    }

    @Test
    fun `a right click is sent with its button to a v20 desktop`() {
        connect(20)
        MirrorControl.pointer("down", 10, 20, "touch", "right")
        assertEquals("right", sent.single().payload["button"]!!.jsonPrimitive.content)
    }

    @Test
    fun `a right click is withheld from an older desktop that would left-click`() {
        connect(19)
        MirrorControl.pointer("down", 10, 20, "touch", "right")
        assertTrue(sent.isEmpty())
        MirrorControl.pointer("down", 10, 20, "touch")
        assertNull(sent.single().payload["button"])
    }

    @Test
    fun `smooth scroll falls back to whole notches on an older desktop`() {
        connect(19)
        MirrorControl.scrollPrecise(0, 250)
        val payload = sent.single().payload
        assertEquals(2, payload["dy"]!!.jsonPrimitive.content.toInt())
        assertNull(payload["precise"])
    }

    @Test
    fun `ping, audio and smooth scroll go out on v20`() {
        connect(20)
        MirrorControl.ping(1234)
        MirrorControl.audio(true)
        MirrorControl.scrollPrecise(0, -60)
        assertEquals(MessageType.PC_MIRROR_PING, sent[0].type)
        assertEquals("1234", sent[0].payload["t"]!!.jsonPrimitive.content)
        assertEquals(MessageType.PC_AUDIO_START, sent[1].type)
        assertEquals("-60", sent[2].payload["dy"]!!.jsonPrimitive.content)
        assertEquals("true", sent[2].payload["precise"]!!.jsonPrimitive.content)
    }
}
