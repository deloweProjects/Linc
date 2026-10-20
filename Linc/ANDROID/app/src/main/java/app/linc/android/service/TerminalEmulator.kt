package app.linc.android.service

/**
 * A VT/xterm screen for the PC's terminal (D-097): what Windows' pseudo-console draws — text,
 * colours, cursor moves, erases, scrolling — kept as a grid of cells plus scrollback, ready to be
 * drawn. ConPTY speaks a modest, well-behaved subset of xterm, which is what this covers: SGR
 * colours (16, 256 and true colour), cursor positioning and movement, erase in line/display,
 * insert/delete characters and lines, scroll regions, the alternate screen, and OSC titles.
 * Anything else is read and ignored, never shown as garbage.
 *
 * Wide characters (CJK, emoji) take two cells, the second holding [WIDE_TAIL] (or, for a character
 * past U+FFFF, its low surrogate). Lines remember when text ran on past their last column, so a
 * change of width re-wraps earlier output the way Termux does, instead of leaving it cut where the
 * old width cut it.
 */
class TerminalEmulator(cols: Int, rows: Int, private val scrollbackLimit: Int = 3000) {

    /** One character cell: the char and its look packed into [attr] (fg, bg, flags). */
    class Line(var cols: Int) {
        var chars = CharArray(cols) { ' ' }
        var attrs = LongArray(cols) { DEFAULT_ATTR }
        /** The text ran on past the last column into the next line (an automatic wrap, not a line break). */
        var wrapped = false
        fun resize(n: Int) {
            if (n == cols) return
            chars = chars.copyOf(n).also { for (i in cols until n) it[i] = ' ' }
            attrs = attrs.copyOf(n).also { for (i in cols until n) it[i] = DEFAULT_ATTR }
            cols = n
        }
        fun clear(from: Int = 0, to: Int = cols, attr: Long = DEFAULT_ATTR) {
            for (i in from.coerceAtLeast(0) until to.coerceAtMost(cols)) { chars[i] = ' '; attrs[i] = attr }
            if (to >= cols) wrapped = false
        }
        fun text(): String = String(chars).replace(WIDE_TAIL.toString(), "").trimEnd()
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

    /**
     * A new size. A new width re-wraps the main screen and scrollback (an alternate screen, a
     * full-screen program's, is only cut or padded: the program redraws it); a new height alone,
     * the keyboard opening or closing, only moves lines between the screen and scrollback.
     */
    fun resize(newCols: Int, newRows: Int) {
        if (newCols == cols && newRows == rows) return
        val saved = savedScreen
        if (newCols != cols) {
            reflow(newCols, newRows)
            if (saved != null) {
                screen.forEach { it.resize(newCols) }
                cursorRow = fitRows(screen, cursorRow, newRows, newCols, toScrollback = false)
            }
        } else if (saved == null) {
            cursorRow = fitRows(screen, cursorRow, newRows, newCols, toScrollback = true)
        } else {
            cursorRow = fitRows(screen, cursorRow, newRows, newCols, toScrollback = false)
            savedRow = fitRows(saved, savedRow, newRows, newCols, toScrollback = true)
        }
        cols = newCols; rows = newRows
        top = 0; bottom = rows - 1
        cursorRow = cursorRow.coerceIn(0, rows - 1); cursorCol = cursorCol.coerceIn(0, cols - 1)
        savedRow = savedRow.coerceIn(0, rows - 1); savedCol = savedCol.coerceIn(0, cols - 1)
        if (cursorCol < cols - 1) wrapPending = false
        version++
    }

    /** [lines] made [newRows] long, keeping the cursor's line; returns the cursor's new row. */
    private fun fitRows(lines: MutableList<Line>, row: Int, newRows: Int, newCols: Int, toScrollback: Boolean): Int {
        var r = row
        while (lines.size > newRows) {
            // Shrinking: blank lines under the cursor go first, then lines above it, into scrollback.
            val blankBelow = lines.size - 1 > r && lines.last().text().isEmpty()
            if (!blankBelow && r > 0) { val gone = lines.removeAt(0); if (toScrollback) pushScrollback(gone); r-- } else lines.removeAt(lines.size - 1)
        }
        while (lines.size < newRows) lines.add(Line(newCols))
        return r
    }

    /**
     * The main screen and scrollback re-wrapped at [newCols]: each run of wrapped lines is joined
     * back into the one line it was and laid out again at the new width, wide characters kept
     * whole, the cursor kept on the same character. The screen is then the last [newRows] lines
     * (blank lines under the cursor dropped first, so the prompt doesn't scroll away).
     */
    private fun reflow(newCols: Int, newRows: Int) {
        val alt = savedScreen != null
        val main = savedScreen ?: screen
        val mainRow = (if (alt) savedRow else cursorRow).coerceIn(0, main.size - 1)
        val mainCol = if (alt) savedCol else cursorCol
        val pending = !alt && wrapPending
        val all = ArrayList<Line>(scrollback.size + main.size).apply { addAll(scrollback); addAll(main) }
        val cursorAt = scrollback.size + mainRow
        val out = ArrayList<Line>()
        var newRow = -1
        var newCol = 0
        var newPending = false
        var start = 0
        while (start < all.size) {
            var end = start
            while (end < all.size - 1 && all[end].wrapped) end++
            // The run's cells end to end; the last line's trailing blanks dropped, unless the cursor is past them.
            val last = all[end]
            var lastLen = last.cols
            while (lastLen > 0 && last.chars[lastLen - 1] == ' ' && last.attrs[lastLen - 1] and BG_MASK == 0L) lastLen--
            var cursorOff = -1
            if (cursorAt in start..end) {
                cursorOff = mainCol.coerceIn(0, all[cursorAt].cols - 1) + (if (pending) 1 else 0)
                for (k in start until cursorAt) cursorOff += all[k].cols
            }
            var total = lastLen
            for (k in start until end) total += all[k].cols
            val length = maxOf(total, cursorOff)
            val lc = CharArray(length) { ' ' }
            val la = LongArray(length) { DEFAULT_ATTR }
            var p = 0
            for (k in start..end) {
                val l = all[k]
                val m = if (k == end) lastLen else l.cols
                l.chars.copyInto(lc, p, 0, m); l.attrs.copyInto(la, p, 0, m)
                p += m
            }
            // Laid out again at the new width.
            var line = Line(newCols)
            var x = 0
            var i = 0
            while (i < length) {
                val c = lc[i]
                val wide = i + 1 < length && isWideHead(c, lc[i + 1])
                if (!wide && isTail(c)) { // a padding cell, or half of a character something overwrote
                    if (i == cursorOff) { newRow = out.size; newCol = x.coerceAtMost(newCols - 1) }
                    i++; continue
                }
                val w = if (wide) 2 else 1
                if (x + w > newCols) {
                    if (x < newCols) { line.chars[x] = WIDE_TAIL; line.attrs[x] = la[i] } // a wide character that doesn't fit
                    line.wrapped = true
                    out.add(line); line = Line(newCols); x = 0
                }
                if (i == cursorOff || (wide && i + 1 == cursorOff)) { newRow = out.size; newCol = x }
                line.chars[x] = c; line.attrs[x] = la[i]
                if (wide) { line.chars[x + 1] = lc[i + 1]; line.attrs[x + 1] = la[i + 1] }
                x += w; i += w
            }
            if (cursorOff == length) {
                newRow = out.size
                if (x >= newCols) { newCol = newCols - 1; newPending = true } else newCol = x
            }
            out.add(line)
            start = end + 1
        }
        if (newRow < 0) newRow = (out.size - 1).coerceAtLeast(0)
        while (out.size - 1 > newRow && out.last().let { !it.wrapped && it.text().isEmpty() }) out.removeAt(out.size - 1)
        val first = maxOf(0, minOf(out.size - newRows, newRow))
        val newMain = MutableList(minOf(newRows, out.size - first)) { out[first + it] }
        while (newMain.size < newRows) newMain.add(Line(newCols))
        scrollback.clear()
        for (k in 0 until first) pushScrollback(out[k])
        if (alt) {
            savedScreen = newMain; savedRow = newRow - first; savedCol = newCol
        } else {
            screen = newMain; cursorRow = newRow - first; cursorCol = newCol; wrapPending = newPending
        }
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
                else -> if (code >= 0x20 && code != 0x7F) put(code)
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

    private fun put(code: Int) {
        if (isZeroWidth(code)) return // combining marks, joiners, variation selectors: no cell of their own
        val wide = isWide(code) && cols >= 2
        val head: Char
        val tail: Char
        if (code > 0xFFFF) {
            val pair = Character.toChars(code)
            if (wide) { head = pair[0]; tail = pair[1] } else { head = '�'; tail = WIDE_TAIL }
        } else { head = code.toChar(); tail = WIDE_TAIL }
        if (wrapPending || (wide && cursorCol == cols - 1)) {
            // An automatic wrap: remembered on the line, so a resize can join it back up.
            val full = screen[cursorRow]
            if (!wrapPending) { full.chars[cursorCol] = WIDE_TAIL; full.attrs[cursorCol] = attr } // the wide one doesn't fit
            full.wrapped = true
            cursorCol = 0; lineFeed()
        }
        val line = screen[cursorRow]
        unsplit(line, cursorCol)
        if (wide) unsplit(line, cursorCol + 1)
        line.chars[cursorCol] = head; line.attrs[cursorCol] = attr
        if (wide) { line.chars[cursorCol + 1] = tail; line.attrs[cursorCol + 1] = attr }
        val next = cursorCol + if (wide) 2 else 1
        if (next >= cols) { cursorCol = cols - 1; wrapPending = true } else cursorCol = next
    }

    /** Before cell [i] is written: the other half of a wide character there is blanked, not left dangling. */
    private fun unsplit(line: Line, i: Int) {
        if (i > 0 && isTail(line.chars[i])) line.chars[i - 1] = ' '
        if (i + 1 < line.cols && isTail(line.chars[i + 1])) line.chars[i + 1] = ' '
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
        // Colours and modes (ConPTY hides the cursor around every repaint) leave a pending wrap alone, as in xterm.
        if (final != 'm' && final != 'h' && final != 'l') wrapPending = false
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

        /** The second cell of a wide character (or padding where one didn't fit at the end of a line). */
        const val WIDE_TAIL = '\uFFFF'

        fun isTail(c: Char): Boolean = c == WIDE_TAIL || c.isLowSurrogate()

        /** [c] followed by [next] is one wide character over two cells. */
        fun isWideHead(c: Char, next: Char): Boolean =
            if (c.isHighSurrogate()) next.isLowSurrogate() else next == WIDE_TAIL && isWide(c.code)

        /*
         * East Asian Wide and Fullwidth, roughly as wcwidth has it: Hangul Jamo, CJK and its
         * punctuation, kana, Hangul syllables, fullwidth forms, and the emoji blocks. Box drawing
         * (U+2500–U+257F) and other symbols stay one cell, as Windows' console has them.
         */
        private val WIDE = intArrayOf(
            0x1100, 0x115F, 0x231A, 0x231B, 0x2329, 0x232A, 0x23E9, 0x23EC, 0x23F0, 0x23F0, 0x23F3, 0x23F3,
            0x25FD, 0x25FE, 0x2614, 0x2615, 0x2648, 0x2653, 0x267F, 0x267F, 0x2693, 0x2693, 0x26A1, 0x26A1,
            0x26AA, 0x26AB, 0x26BD, 0x26BE, 0x26C4, 0x26C5, 0x26CE, 0x26CE, 0x26D4, 0x26D4, 0x26EA, 0x26EA,
            0x26F2, 0x26F3, 0x26F5, 0x26F5, 0x26FA, 0x26FA, 0x26FD, 0x26FD, 0x2705, 0x2705, 0x270A, 0x270B,
            0x2728, 0x2728, 0x274C, 0x274C, 0x274E, 0x274E, 0x2753, 0x2755, 0x2757, 0x2757, 0x2795, 0x2797,
            0x27B0, 0x27B0, 0x27BF, 0x27BF, 0x2B1B, 0x2B1C, 0x2B50, 0x2B50, 0x2B55, 0x2B55,
            0x2E80, 0x303E, 0x3041, 0x33FF, 0x3400, 0x4DBF, 0x4E00, 0x9FFF, 0xA000, 0xA4CF, 0xA960, 0xA97F,
            0xAC00, 0xD7A3, 0xF900, 0xFAFF, 0xFE10, 0xFE19, 0xFE30, 0xFE6F, 0xFF00, 0xFF60, 0xFFE0, 0xFFE6,
            0x16FE0, 0x18AFF, 0x1B000, 0x1B2FF, 0x1F004, 0x1F004, 0x1F0CF, 0x1F0CF, 0x1F18E, 0x1F18E,
            0x1F191, 0x1F19A, 0x1F200, 0x1F251, 0x1F300, 0x1F64F, 0x1F680, 0x1F6FF, 0x1F7E0, 0x1F7EB,
            0x1F900, 0x1F9FF, 0x1FA70, 0x1FAFF, 0x20000, 0x3FFFD,
        )

        /** Whether [code] takes two cells. */
        fun isWide(code: Int): Boolean {
            if (code < 0x1100) return false
            var i = 0
            while (i < WIDE.size) {
                if (code < WIDE[i]) return false
                if (code <= WIDE[i + 1]) return true
                i += 2
            }
            return false
        }

        /** Characters that take no cell: combining marks, zero-width spaces and joiners, variation selectors. */
        fun isZeroWidth(code: Int): Boolean =
            code in 0x0300..0x036F || code in 0x200B..0x200F || code in 0x2060..0x2064 || code in 0x20D0..0x20FF ||
                code in 0xFE00..0xFE0F || code in 0xFE20..0xFE2F || code in 0xE0100..0xE01EF

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
