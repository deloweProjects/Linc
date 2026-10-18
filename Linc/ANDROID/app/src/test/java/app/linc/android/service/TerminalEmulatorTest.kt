package app.linc.android.service

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** The PC terminal's screen (D-097): what ConPTY actually sends must land where it would in Windows Terminal. */
class TerminalEmulatorTest {

    private fun screenText(t: TerminalEmulator) = t.lines().takeLast(t.rows).map { it.text() }

    @Test
    fun `text, carriage return and line feed`() {
        val t = TerminalEmulator(20, 4)
        t.feed("PS C:\\> dir\r\nhello\r\n")
        assertEquals(listOf("PS C:\\> dir", "hello", "", ""), screenText(t))
        assertEquals(2, t.cursorRow); assertEquals(0, t.cursorCol)
    }

    @Test
    fun `long lines wrap and the screen scrolls into scrollback`() {
        val t = TerminalEmulator(5, 2)
        t.feed("abcdefgh\r\nij")
        assertEquals(listOf("fgh", "ij"), screenText(t))
        assertEquals(1, t.scrollbackSize)
        assertEquals("abcde", t.lines().first().text())
    }

    @Test
    fun `cursor positioning, erase in line and in display`() {
        val t = TerminalEmulator(10, 3)
        t.feed("1111111111\r\n2222222222\r\n3333333333")
        t.feed("\u001b[2;4H\u001b[K")
        assertEquals("222", screenText(t)[1])
        t.feed("\u001b[2J")
        assertTrue(screenText(t).all { it.isEmpty() })
        t.feed("\u001b[3;5Hx")
        assertEquals("    x", screenText(t)[2])
    }

    @Test
    fun `colours are kept per cell, and reset`() {
        val t = TerminalEmulator(10, 1)
        t.feed("\u001b[31mR\u001b[0mN\u001b[38;2;1;2;3mT\u001b[48;5;21mB")
        val line = t.lines().last()
        assertEquals(0xC50F1F, TerminalEmulator.fg(line.attrs[0]))
        assertEquals(-1, TerminalEmulator.fg(line.attrs[1]))
        assertEquals(0x010203, TerminalEmulator.fg(line.attrs[2]))
        assertEquals(0x0000FF, TerminalEmulator.bg(line.attrs[3]))
    }

    @Test
    fun `utf-8, titles and unknown sequences don't show as garbage`() {
        val t = TerminalEmulator(20, 1)
        t.feed("\u001b]0;Windows PowerShell\u0007\u001b[?25l\u001b[?2004hé✓\u001b[>4;1m!")
        assertEquals("é✓!", screenText(t)[0])
        assertEquals("Windows PowerShell", t.title)
        assertFalse(t.cursorVisible)
    }

    @Test
    fun `split bytes of one character still make that character`() {
        val t = TerminalEmulator(10, 1)
        val bytes = "✓".toByteArray()
        t.feed(bytes.copyOfRange(0, 1)); t.feed(bytes.copyOfRange(1, bytes.size))
        assertEquals("✓", screenText(t)[0])
    }

    @Test
    fun `alternate screen comes back to what was there`() {
        val t = TerminalEmulator(10, 2)
        t.feed("prompt")
        t.feed("\u001b[?1049h\u001b[Hvim stuff")
        assertEquals("vim stuff", screenText(t)[0])
        t.feed("\u001b[?1049l")
        assertEquals("prompt", screenText(t)[0])
    }

    @Test
    fun `resize keeps the cursor line`() {
        val t = TerminalEmulator(10, 4)
        t.feed("a\r\nb\r\nc\r\nd")
        t.resize(12, 2)
        assertEquals(listOf("c", "d"), screenText(t))
        assertEquals(1, t.cursorRow)
    }
}
