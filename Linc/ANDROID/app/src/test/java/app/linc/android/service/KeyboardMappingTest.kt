package app.linc.android.service

import android.view.KeyEvent
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Pure-function coverage for [KeyboardMapping] (M4a/M05): printable characters go as text,
 * backspace/enter/arrows go as Windows virtual-key codes. This is the mapping MirrorActivity
 * documented at its header before it was lifted out here so the Tools page's keyboard could
 * share it instead of growing a second one.
 */
class KeyboardMappingTest {

    @Test
    fun `backspace maps to VK_BACK`() {
        assertEquals(
            KeyboardMapping.Mapped.VirtualKey(0x08),
            KeyboardMapping.map(KeyEvent.KEYCODE_DEL, 0),
        )
    }

    @Test
    fun `enter maps to VK_RETURN`() {
        assertEquals(
            KeyboardMapping.Mapped.VirtualKey(0x0D),
            KeyboardMapping.map(KeyEvent.KEYCODE_ENTER, 0),
        )
    }

    @Test
    fun `dpad left maps to VK_LEFT`() {
        assertEquals(
            KeyboardMapping.Mapped.VirtualKey(0x25),
            KeyboardMapping.map(KeyEvent.KEYCODE_DPAD_LEFT, 0),
        )
    }

    @Test
    fun `dpad right maps to VK_RIGHT`() {
        assertEquals(
            KeyboardMapping.Mapped.VirtualKey(0x27),
            KeyboardMapping.map(KeyEvent.KEYCODE_DPAD_RIGHT, 0),
        )
    }

    @Test
    fun `a printable character maps to text`() {
        assertEquals(
            KeyboardMapping.Mapped.Text("a"),
            KeyboardMapping.map(KeyEvent.KEYCODE_A, 'a'.code),
        )
    }

    @Test
    fun `a printable character keeps its own unicode value, not the key code`() {
        // KEYCODE_A with a shifted/unicode char of 'A' must send "A", not "a" — the unicode
        // char, not the physical key, decides what's typed.
        assertEquals(
            KeyboardMapping.Mapped.Text("A"),
            KeyboardMapping.map(KeyEvent.KEYCODE_A, 'A'.code),
        )
    }

    @Test
    fun `an unhandled key with no unicode char maps to nothing`() {
        assertNull(KeyboardMapping.map(KeyEvent.KEYCODE_VOLUME_UP, 0))
    }
}
