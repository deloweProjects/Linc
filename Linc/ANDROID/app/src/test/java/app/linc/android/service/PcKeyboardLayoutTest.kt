package app.linc.android.service

import app.linc.android.service.PcKeyboardLayout.Action
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** v21 floating keyboard (D-068): layout geometry and what each key sends. */
class PcKeyboardLayoutTest {

    private val keys = mutableListOf<Pair<Int, Boolean>>()
    private val text = mutableListOf<String>()

    @After
    fun clear() = ModifierKeys.clear()

    private fun press(action: Action) = PcKeyboardLayout.press(action, { vk, down -> keys += vk to down }, { text += it })

    private fun charKey(c: Char) = PcKeyboardLayout.rows(fn = false).flatten()
        .first { (it.action as? Action.Char)?.normal == c }.action

    @Test
    fun `every row is exactly the full width, in both layers`() {
        for (fn in listOf(false, true)) {
            PcKeyboardLayout.rows(fn).forEachIndexed { i, row ->
                assertEquals("row $i (fn=$fn)", PcKeyboardLayout.ROW_UNITS, row.sumOf { it.width.toDouble() }.toFloat(), 0.001f)
            }
        }
    }

    @Test
    fun `the Fn layer has F1 to F12`() {
        val labels = PcKeyboardLayout.rows(fn = true).flatten().map { it.label }
        assertTrue((1..12).all { "F$it" in labels })
        val f5 = PcKeyboardLayout.rows(fn = true).flatten().first { it.label == "F5" }.action as Action.Key
        assertEquals(0x74, f5.vk)
    }

    @Test
    fun `a plain character is typed as text, layout-independent`() {
        press(charKey('2'))
        assertEquals(listOf("2"), text)
        assertTrue(keys.isEmpty())
    }

    @Test
    fun `one-shot Shift types the shifted character once`() {
        ModifierKeys.cycle(ModifierKeys.VK_SHIFT)
        press(charKey('2'))
        press(charKey('2'))
        assertEquals(listOf("@", "2"), text)
    }

    @Test
    fun `Ctrl plus a letter is a key chord, not text`() {
        ModifierKeys.cycle(ModifierKeys.VK_CONTROL)
        press(charKey('c'))
        assertTrue(text.isEmpty())
        assertEquals(listOf(0x11 to true, 0x43 to true, 0x43 to false, 0x11 to false), keys)
        assertFalse(ModifierKeys.any())
    }

    @Test
    fun `Ctrl plus punctuation uses the OEM key (Ctrl+slash)`() {
        ModifierKeys.cycle(ModifierKeys.VK_CONTROL)
        press(charKey('/'))
        assertTrue((0xBF to true) in keys)
    }

    @Test
    fun `Ctrl+Shift+T sends all three`() {
        ModifierKeys.cycle(ModifierKeys.VK_CONTROL)
        ModifierKeys.cycle(ModifierKeys.VK_SHIFT)
        press(charKey('t'))
        assertEquals(listOf(0x11, 0x10, 0x54), keys.filter { it.second }.map { it.first })
    }
}
