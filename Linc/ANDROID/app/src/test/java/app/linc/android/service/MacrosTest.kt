package app.linc.android.service

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Control → Macros' pure half ([MacroBook], [MacroKinds]): what each step needs filled in, the
 * starters, reordering, and the file a macro is kept in, including one written by a newer Linc.
 */
class MacrosTest {

    @Test
    fun `every kind is listed once`() {
        assertEquals(MacroKinds.all.size, MacroKinds.all.map { it.kind }.toSet().size)
    }

    @Test
    fun `the starters begin with Presenting and every step is complete`() {
        val presenting = MacroBook.starters.first()
        assertEquals("Presenting", presenting.name)
        assertEquals(
            listOf(MacroKinds.PHONE_DND, MacroKinds.PC_VOLUME, MacroKinds.PC_MIRROR),
            presenting.steps.map { it.kind },
        )
        for (m in MacroBook.starters) for (s in m.steps) assertNull("${m.name}: ${s.kind}", MacroBook.problem(s))
        assertEquals(MacroBook.starters.size, MacroBook.starters.map { it.id }.toSet().size)
    }

    @Test
    fun `levels are 0 to 100`() {
        assertNull(MacroBook.problem(MacroStep(MacroKinds.PC_VOLUME, "0")))
        assertNull(MacroBook.problem(MacroStep(MacroKinds.PC_BRIGHTNESS, "100")))
        assertNotNull(MacroBook.problem(MacroStep(MacroKinds.PC_VOLUME, "101")))
        assertNotNull(MacroBook.problem(MacroStep(MacroKinds.PC_VOLUME, "loud")))
    }

    @Test
    fun `switches are on or off, and the box is tidied`() {
        assertEquals("on", MacroBook.fromInput(MacroKinds.PHONE_DND, " ON ").value)
        assertNull(MacroBook.problem(MacroBook.fromInput(MacroKinds.PC_DARK, "Off")))
        assertNotNull(MacroBook.problem(MacroStep(MacroKinds.PC_CURTAIN, "maybe")))
    }

    @Test
    fun `media actions are the PC's`() {
        for (a in MacroKinds.mediaActions) assertNull(MacroBook.problem(MacroStep(MacroKinds.PC_MEDIA, a)))
        assertNotNull(MacroBook.problem(MacroStep(MacroKinds.PC_MEDIA, "stop")))
    }

    @Test
    fun `a wait is between a tenth of a second and ten minutes`() {
        assertEquals(1.5, MacroBook.waitSeconds(MacroBook.fromInput(MacroKinds.WAIT, "1,5"))!!, 0.0)
        assertNotNull(MacroBook.problem(MacroStep(MacroKinds.WAIT, "0")))
        assertNotNull(MacroBook.problem(MacroStep(MacroKinds.WAIT, "601")))
    }

    @Test
    fun `an app is a package name`() {
        assertNull(MacroBook.problem(MacroStep(MacroKinds.PHONE_APP, "com.spotify.music")))
        assertNotNull(MacroBook.problem(MacroStep(MacroKinds.PHONE_APP, "Spotify")))
    }

    @Test
    fun `a step with nothing to fill in drops what was typed`() {
        assertEquals("", MacroBook.fromInput(MacroKinds.PC_LOCK, "now").value)
    }

    @Test
    fun `an unknown step can't run`() {
        assertNotNull(MacroBook.problem(MacroStep("teleport")))
    }

    @Test
    fun `steps move up and down, and a move off the end changes nothing`() {
        assertEquals(listOf("c", "a", "b"), MacroBook.moved(listOf("a", "b", "c"), 2, 0))
        assertEquals(listOf("b", "a"), MacroBook.moved(listOf("a", "b"), 0, 1))
        assertEquals(listOf("a", "b"), MacroBook.moved(listOf("a", "b"), 1, 2))
    }

    @Test
    fun `a macro needs the newest version among its steps`() {
        val m = Macro("x", "X", listOf(MacroStep(MacroKinds.WAIT, "1"), MacroStep(MacroKinds.PC_CURTAIN, "on"), MacroStep(MacroKinds.PC_LOCK)))
        assertEquals(28, MacroBook.pcVersion(m))
        assertEquals(0, MacroBook.pcVersion(Macro("y", "Y", listOf(MacroStep(MacroKinds.PHONE_DND, "on")))))
    }

    @Test
    fun `macros come back from the file the same`() {
        val macros = listOf(
            Macro("m1", "Night", listOf(MacroStep(MacroKinds.PHONE_DND, "on"), MacroStep(MacroKinds.WAIT, "2"), MacroStep(MacroKinds.SEND_TEXT, "say \"hi\""))),
            Macro("m2", "Empty"),
        )
        assertEquals(macros, MacroBook.decode(MacroBook.encode(macros)))
    }

    @Test
    fun `a newer Linc's file opens, without the steps this one doesn't know`() {
        val newer = """{"version":2,"macros":[{"id":"x","name":"X","colour":"red","steps":[{"kind":"hologram","value":"1"},{"kind":"pc_lock"}]}]}"""
        val macros = MacroBook.decode(newer)!!
        assertEquals(1, macros.size)
        assertEquals(listOf(MacroStep(MacroKinds.PC_LOCK)), macros[0].steps)
    }

    @Test
    fun `a broken file reads as none, not as empty`() {
        assertNull(MacroBook.decode("not json"))
        assertNull(MacroBook.decode("[1,2]"))
        assertTrue(MacroBook.decode("""{"macros":[{"id":"","name":"no id"}]}""")!!.isEmpty())
    }

    @Test
    fun `a failure names the step`() {
        assertEquals(
            "Step 2 (PC volume 50) failed: no PC is connected.",
            MacroBook.failureText(1, MacroStep(MacroKinds.PC_VOLUME, "50"), "no PC is connected."),
        )
    }
}
