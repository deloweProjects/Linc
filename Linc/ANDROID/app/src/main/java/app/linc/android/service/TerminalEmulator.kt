package app.linc.android.service

/**
 * A VT/xterm screen for the PC's terminal (D-097): what Windows' pseudo-console draws — text,
 * colours, cursor moves, erases, scrolling — kept as a grid of cells plus scrollback, ready to be
 * drawn. ConPTY speaks a modest, well-behaved subset of xterm, which is what this covers: SGR
 * colours (16, 256 and true colour), cursor positioning and movement, erase in line/display,
 * insert/delete characters and lines, scroll regions, the alternate screen, and OSC titles.
 * Anything else is read and ignored, never shown as garbage.
 */
class TerminalEmulator(cols: Int, rows: Int, private val scrollbackLimit: Int = 3000) {

    /** One character cell: the char and its look packed into [attr] (fg, bg, flags). */
    class Line(var cols: Int) {
        var chars = CharArray(cols) { ' ' }
        var attrs = LongArray(cols) { DEFAULT_ATTR }
        fun resize(n: Int) {
            if (n == cols) return
            chars = chars.copyOf(n).also { for (i in cols until n) it[i] = ' ' }
            attrs = attrs.copyOf(n).also { for (i in cols until n) it[i] = DEFAULT_ATTR }
            cols = n
        }
        fun clear(from: Int = 0, to: Int = cols, attr: Long = DEFAULT_ATTR) {
            for (i in from.coerceAtLeast(0) until to.coerceAtMost(cols)) { chars[i] = ' '; attrs[i] = attr }
        }
        fun text(): String = String(chars).trimEnd()
    }

    var cols = cols; private set
    var rows = rows; private set
    var title = ""; private set
    var cursorRow = 0; private set
    var cursorCol = 0; private set
    var cursorVisible = true; private set
    /** Bumped on every change, so a screen knows to redraw. */
    var version = 0L; private set

    private val scrollback = ArrayDeque<Line>()
    private var screen = MutableList(rows) { Line(cols) }
    private var savedScreen: MutableList<Line>? = null
    private var attr = DEFAULT_ATTR
    private var wrapPending = false
    private var top = 0
    private var bottom = rows - 1
    private var savedRow = 0
    private var savedCol = 0

    // Parser state.
    private var state = State.TEXT
    private val params = StringBuilder()
    private val osc = StringBuilder()
    private var utf8Need = 0
    private var utf8Code = 0

    private enum class State { TEXT, ESC, CSI, OSC, OSC_ESC, CHARSET }

    /** Every line: scrollback, then the screen. */
    fun lines(): List<Line> = scrollback.toList() + screen
    val scrollbackSize: Int get() = scrollback.size

    fun resize(newCols: Int, newRows: Int) {
        if (newCols == cols && newRows == rows) return
        screen.forEach { it.resize(newCols) }
        scrollback.forEach { it.resize(newCols) }
        while (screen.size > newRows) {
            // Shrinking: keep the cursor's line on screen, push lines above it into scrollback.
            if (cursorRow > 0) { pushScrollback(screen.removeAt(0)); cursorRow-- } else screen.removeAt(screen.size - 1)
        }
        while (screen.size < newRows) screen.add(Line(newCols))
        cols = newCols; rows = newRows
        top = 0; bottom = rows - 1
        cursorRow = cursorRow.coerceIn(0, rows - 1); cursorCol = cursorCol.coerceIn(0, cols - 1)
        version++
    }

    fun feed(bytes: ByteArray, length: Int = bytes.size) {
        for (i in 0 until length) {
            val b = bytes[i].toInt() and 0xFF
            if (utf8Need > 0) {
                if (b and 0xC0 == 0x80) {
                    utf8Code = (utf8Code shl 6) or (b and 0x3F)
                    if (--utf8Need == 0) char(utf8Code)
                    continue
                }
                utf8Need = 0
            }
            when {
                b < 0x80 -> char(b)
                b and 0xE0 == 0xC0 -> { utf8Need = 1; utf8Code = b and 0x1F }
                b and 0xF0 == 0xE0 -> { utf8Need = 2; utf8Code = b and 0x0F }
                b and 0xF8 == 0xF0 -> { utf8Need = 3; utf8Code = b and 0x07 }
            }
        }
        version++
    }

    fun feed(text: String) = feed(text.toByteArray(Charsets.UTF_8))

    private fun char(code: Int) {
        when (state) {
            State.TEXT -> when (code) {
                0x1B -> state = State.ESC
                0x0D -> { cursorCol = 0; wrapPending = false }
                0x0A, 0x0B, 0x0C -> lineFeed()
                0x08 -> { if (cursorCol > 0) cursorCol--; wrapPending = false }
                0x09 -> cursorCol = minOf(cols - 1, (cursorCol / 8 + 1) * 8)
                0x07, 0x00, 0x0E, 0x0F -> Unit
                else -> if (code >= 0x20) put(if (code > 0xFFFF) '�' else code.toChar())
            }
            State.ESC -> {
                state = State.TEXT
                when (code.toChar()) {
                    '[' -> { state = State.CSI; params.setLength(0) }
                    ']' -> { state = State.OSC; osc.setLength(0) }
                    '(', ')', '*', '+' -> state = State.CHARSET
                    '7' -> { savedRow = cursorRow; savedCol = cursorCol }
                    '8' -> { cursorRow = savedRow; cursorCol = savedCol }
                    'M' -> reverseIndex()
                    'D' -> lineFeed()
                    'E' -> { cursorCol = 0; lineFeed() }
                    'c' -> reset()
                    else -> Unit
                }
            }
            State.CHARSET -> state = State.TEXT
            State.CSI -> {
                if (code in 0x30..0x3F) params.append(code.toChar())
                else if (code in 0x20..0x2F) Unit // intermediates: none we act on
                else { state = State.TEXT; csi(code.toChar(), params.toString()) }
            }
            State.OSC -> when (code) {
                0x07 -> { state = State.TEXT; oscDone() }
                0x1B -> state = State.OSC_ESC
                else -> if (osc.length < 512) osc.append(code.toChar())
            }
            State.OSC_ESC -> { state = State.TEXT; oscDone() } // ESC \ (string terminator)
        }
    }

    private fun oscDone() {
        val s = osc.toString()
        if (s.startsWith("0;") || s.startsWith("2;")) title = s.substring(2)
    }

    private fun put(c: Char) {
        if (wrapPending) { cursorCol = 0; lineFeed(); wrapPending = false }
        val line = screen[cursorRow]
        line.chars[cursorCol] = c
        line.attrs[cursorCol] = attr
        if (cursorCol == cols - 1) wrapPending = true else cursorCol++
    }

    private fun lineFeed() {
        wrapPending = false
        if (cursorRow == bottom) scrollUp(1) else if (cursorRow < rows - 1) cursorRow++
    }

    private fun reverseIndex() {
        if (cursorRow == top) scrollDown(1) else if (cursorRow > 0) cursorRow--
    }

    /** Lines leave the top of the region; on the main screen with a full region, into scrollback. */
    private fun scrollUp(n: Int) {
        repeat(n) {
            val gone = screen.removeAt(top)
            if (top == 0 && savedScreen == null) pushScrollback(gone)
            screen.add(bottom, Line(cols).also { it.clear(attr = blank()) })
        }
    }

    private fun scrollDown(n: Int) {
        repeat(n) {
            screen.removeAt(bottom)
            screen.add(top, Line(cols).also { it.clear(attr = blank()) })
        }
    }

    private fun pushScrollback(line: Line) {
        scrollback.addLast(line)
        while (scrollback.size > scrollbackLimit) scrollback.removeFirst()
    }

    /** Erased cells keep the current background, as xterm does. */
    private fun blank(): Long = attr and BG_MASK

    private fun csi(final: Char, raw: String) {
        val private = raw.startsWith("?")
        val list = raw.removePrefix("?").removePrefix(">").split(';').map { it.toIntOrNull() ?: -1 }
        fun p(i: Int, default: Int) = list.getOrNull(i)?.takeIf { it >= 0 && !(it == 0 && default > 0) } ?: default
        wrapPending = false
        when (final) {
            'A' -> cursorRow = (cursorRow - p(0, 1)).coerceAtLeast(if (cursorRow >= top) top else 0)
            'B' -> cursorRow = (cursorRow + p(0, 1)).coerceAtMost(if (cursorRow <= bottom) bottom else rows - 1)
            'C' -> cursorCol = (cursorCol + p(0, 1)).coerceAtMost(cols - 1)
            'D' -> cursorCol = (cursorCol - p(0, 1)).coerceAtLeast(0)
            'E' -> { cursorRow = (cursorRow + p(0, 1)).coerceAtMost(rows - 1); cursorCol = 0 }
            'F' -> { cursorRow = (cursorRow - p(0, 1)).coerceAtLeast(0); cursorCol = 0 }
            'G', '`' -> cursorCol = (p(0, 1) - 1).coerceIn(0, cols - 1)
            'd' -> cursorRow = (p(0, 1) - 1).coerceIn(0, rows - 1)
            'H', 'f' -> { cursorRow = (p(0, 1) - 1).coerceIn(0, rows - 1); cursorCol = (p(1, 1) - 1).coerceIn(0, cols - 1) }
            'J' -> when (p(0, 0)) {
                0 -> { screen[cursorRow].clear(cursorCol, cols, blank()); for (r in cursorRow + 1 until rows) screen[r].clear(attr = blank()) }
                1 -> { for (r in 0 until cursorRow) screen[r].clear(attr = blank()); screen[cursorRow].clear(0, cursorCol + 1, blank()) }
                2, 3 -> { screen.forEach { it.clear(attr = blank()) }; if (p(0, 0) == 3) scrollback.clear() }
            }
            'K' -> when (p(0, 0)) {
                0 -> screen[cursorRow].clear(cursorCol, cols, blank())
                1 -> screen[cursorRow].clear(0, cursorCol + 1, blank())
                2 -> screen[cursorRow].clear(attr = blank())
            }
            'X' -> screen[cursorRow].clear(cursorCol, cursorCol + p(0, 1), blank())
            'P' -> { // delete characters
                val line = screen[cursorRow]; val n = p(0, 1).coerceAtMost(cols - cursorCol)
                for (i in cursorCol until cols) {
                    val from = i + n
                    line.chars[i] = if (from < cols) line.chars[from] else ' '
                    line.attrs[i] = if (from < cols) line.attrs[from] else blank()
                }
            }
            '@' -> { // insert blanks
                val line = screen[cursorRow]; val n = p(0, 1).coerceAtMost(cols - cursorCol)
                for (i in cols - 1 downTo cursorCol + n) { line.chars[i] = line.chars[i - n]; line.attrs[i] = line.attrs[i - n] }
                line.clear(cursorCol, cursorCol + n, blank())
            }
            'L' -> if (cursorRow in top..bottom) repeat(p(0, 1)) { screen.removeAt(bottom); screen.add(cursorRow, Line(cols)) }
            'M' -> if (cursorRow in top..bottom) repeat(p(0, 1)) { screen.removeAt(cursorRow); screen.add(bottom, Line(cols)) }
            'S' -> scrollUp(p(0, 1))
            'T' -> scrollDown(p(0, 1))
            'r' -> {
                top = (p(0, 1) - 1).coerceIn(0, rows - 1)
                bottom = (p(1, rows) - 1).coerceIn(top, rows - 1)
                cursorRow = 0; cursorCol = 0
            }
            's' -> { savedRow = cursorRow; savedCol = cursorCol }
            'u' -> { cursorRow = savedRow; cursorCol = savedCol }
            'm' -> sgr(list)
            'h', 'l' -> if (private) {
                val on = final == 'h'
                for (mode in list) when (mode) {
                    25 -> cursorVisible = on
                    1049, 1047, 47 -> alternate(on)
                }
            }
            else -> Unit
        }
    }

    private fun alternate(on: Boolean) {
        if (on && savedScreen == null) {
            savedScreen = screen
            screen = MutableList(rows) { Line(cols) }
            savedRow = cursorRow; savedCol = cursorCol
        } else if (!on && savedScreen != null) {
            screen = savedScreen!!
            savedScreen = null
            cursorRow = savedRow; cursorCol = savedCol
        }
    }

    private fun sgr(list: List<Int>) {
        var i = 0
        val codes = if (list.isEmpty()) listOf(0) else list
        while (i < codes.size) {
            when (val c = codes[i].coerceAtLeast(0)) {
                0 -> attr = DEFAULT_ATTR
                1 -> attr = attr or BOLD
                22 -> attr = attr and BOLD.inv()
                4 -> attr = attr or UNDERLINE
                24 -> attr = attr and UNDERLINE.inv()
                7 -> attr = attr or INVERSE
                27 -> attr = attr and INVERSE.inv()
                in 30..37 -> attr = withFg(palette(c - 30))
                in 90..97 -> attr = withFg(palette(c - 90 + 8))
                39 -> attr = attr and FG_MASK.inv() or (DEFAULT_ATTR and FG_MASK)
                in 40..47 -> attr = withBg(palette(c - 40))
                in 100..107 -> attr = withBg(palette(c - 100 + 8))
                49 -> attr = attr and BG_MASK.inv() or (DEFAULT_ATTR and BG_MASK)
                38, 48 -> {
                    val color = when (codes.getOrNull(i + 1)) {
                        5 -> codes.getOrNull(i + 2)?.let { i += 2; palette(it) }
                        2 -> if (i + 4 < codes.size) {
                            val rgb = (codes[i + 2].coerceIn(0, 255) shl 16) or (codes[i + 3].coerceIn(0, 255) shl 8) or codes[i + 4].coerceIn(0, 255)
                            i += 4; rgb or 0x1000000
                        } else null
                        else -> null
                    }
                    if (color != null) attr = if (c == 38) withFg(color) else withBg(color)
                }
            }
            i++
        }
    }

    private fun withFg(rgb: Int) = attr and FG_MASK.inv() or ((rgb.toLong() and 0x1FFFFFF) shl FG_SHIFT)
    private fun withBg(rgb: Int) = attr and BG_MASK.inv() or ((rgb.toLong() and 0x1FFFFFF) shl BG_SHIFT)

    private fun reset() {
        screen.forEach { it.clear() }
        attr = DEFAULT_ATTR; cursorRow = 0; cursorCol = 0; top = 0; bottom = rows - 1; cursorVisible = true
    }

    companion object {
        /*
         * A cell's look in one Long: foreground in bits 0–24 and background in bits 25–49 (each
         * 0 = "default", else 0x1RRGGBB), then flags. Default = default fg, default bg.
         */
        private const val FG_SHIFT = 0
        private const val BG_SHIFT = 25
        private const val FG_MASK = 0x1FFFFFFL shl FG_SHIFT
        private const val BG_MASK = 0x1FFFFFFL shl BG_SHIFT
        const val BOLD = 1L shl 50
        const val UNDERLINE = 1L shl 51
        const val INVERSE = 1L shl 52
        const val DEFAULT_ATTR = 0L

        /** Foreground colour of [attr] as 0xRRGGBB, or -1 for the default. */
        fun fg(attr: Long): Int = ((attr and FG_MASK) shr FG_SHIFT).toInt().let { if (it == 0) -1 else it and 0xFFFFFF }
        fun bg(attr: Long): Int = ((attr and BG_MASK) shr BG_SHIFT).toInt().let { if (it == 0) -1 else it and 0xFFFFFF }

        /** xterm's 256 colours (Windows Terminal's "Campbell" for the first 16), with the "set" bit. */
        fun palette(n: Int): Int {
            val base = intArrayOf(
                0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00, 0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
                0x767676, 0xE74856, 0x16C60C, 0xF9F1A5, 0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2,
            )
            val rgb = when {
                n < 0 -> 0xCCCCCC
                n < 16 -> base[n]
                n < 232 -> {
                    val i = n - 16
                    val steps = intArrayOf(0, 95, 135, 175, 215, 255)
                    (steps[i / 36] shl 16) or (steps[(i / 6) % 6] shl 8) or steps[i % 6]
                }
                n < 256 -> (8 + (n - 232) * 10).let { (it shl 16) or (it shl 8) or it }
                else -> 0xCCCCCC
            }
            return rgb or 0x1000000
        }
    }
}
