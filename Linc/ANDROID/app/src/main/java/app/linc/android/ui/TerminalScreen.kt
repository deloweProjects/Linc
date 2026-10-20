package app.linc.android.ui

import android.content.Context
import android.graphics.Paint
import android.graphics.Typeface
import android.text.InputType
import android.view.KeyEvent
import android.view.View
import android.view.inputmethod.BaseInputConnection
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection
import android.view.inputmethod.InputMethodManager
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.calculatePan
import androidx.compose.foundation.gestures.calculateZoom
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.MaterialShapes
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.toShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.graphics.nativeCanvas
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.positionChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.edit
import app.linc.android.PcToolActivity
import app.linc.android.service.Explanations
import app.linc.android.service.PcTools
import app.linc.android.service.TerminalEmulator
import app.linc.android.ui.kit.HelperText
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.LincScaffold
import app.linc.android.ui.kit.SectionHeader
import app.linc.android.ui.kit.pagePadding
import kotlin.math.abs

/*
 * Tools → Terminal (D-097): PowerShell, Command Prompt, WSL and the rest, on the PC, in a real
 * Windows console — the same prompts, colours and programs as at the PC.
 *
 * The page lists the PC's shells; picking one opens it full screen ([TerminalSession]): the
 * console in this phone's own colours, typed into directly (the phone's keyboard goes straight
 * to the shell, as in Termux), with a row of the keys a phone keyboard lacks.
 */

/** A shell the PC offers: its id on the wire, and how it reads here. */
internal data class ShellLook(val title: String, val subtitle: String, val icon: ImageVector)

internal fun shellLook(id: String): ShellLook = when {
    id == "powershell" -> ShellLook("PowerShell", "The modern Windows shell", LincSymbols.Terminal)
    id == "winps" -> ShellLook("Windows PowerShell", "PowerShell 5, built into Windows", LincSymbols.Terminal)
    id == "cmd" -> ShellLook("Command Prompt", "cmd.exe, the classic", LincSymbols.Code)
    id == "wsl" -> ShellLook("WSL", "Linux, your default distribution", LincSymbols.Developer)
    id.startsWith("wsl:") -> ShellLook(id.removePrefix("wsl:"), "Linux on WSL", LincSymbols.Developer)
    id == "gitbash" -> ShellLook("Git Bash", "Bash from Git for Windows", LincSymbols.Code)
    else -> ShellLook(id, "A shell on the PC", LincSymbols.Terminal)
}

/** The Terminal page: the PC's shells, as big cards; one tap opens it full screen. */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun TerminalHome(onBack: (() -> Unit)? = null, onSettings: (() -> Unit)? = null) {
    val context = LocalContext.current
    val shells by PcTools.shells.collectAsState()
    val open by PcTools.termOpen.collectAsState()
    val error by PcTools.termError.collectAsState()
    val linkState by app.linc.android.service.CompanionStateHolder.state.collectAsState()
    val ready = remember(linkState) { PcTools.ready() }
    LaunchedEffect(ready) { if (ready) PcTools.refreshShells() }

    LincScaffold(title = "Terminal", subtitle = "On your PC", onBack = onBack, onSettings = onSettings) { inner ->
        LazyColumn(contentPadding = pagePadding(inner), verticalArrangement = Arrangement.spacedBy(Kit.groupGap)) {
            if (!ready) item {
                HelperText("Connect the PC, and update Linc on it, to open a terminal.", kind = Explanations.Kind.Blocked,
                    modifier = Modifier.padding(horizontal = Kit.gutter))
            }
            error?.let { item { HelperText(it, kind = Explanations.Kind.Error, modifier = Modifier.padding(horizontal = Kit.gutter)) } }
            if (open) item {
                Surface(
                    onClick = { PcToolActivity.openTerminal(context, null) },
                    shape = RoundedCornerShape(28.dp), color = MaterialTheme.colorScheme.primaryContainer,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter),
                ) {
                    Row(Modifier.padding(20.dp), verticalAlignment = Alignment.CenterVertically) {
                        Icon(LincSymbols.Terminal, contentDescription = null)
                        Column(Modifier.weight(1f).padding(horizontal = 16.dp)) {
                            Text("Back to your terminal", style = MaterialTheme.typography.titleMedium)
                            Text(PcTools.screen.title.ifEmpty { "Still running on the PC" }, style = MaterialTheme.typography.bodyMedium,
                                maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                        Icon(LincSymbols.ChevronRight, contentDescription = null)
                    }
                }
            }
            item { SectionHeader("Open a shell") }
            for ((index, id) in shells.withIndex()) item(id) {
                val look = shellLook(id)
                Surface(
                    onClick = { PcToolActivity.openTerminal(context, id) },
                    enabled = ready,
                    shape = app.linc.android.ui.kit.segmentShape(app.linc.android.ui.kit.segmentOf(index, shells.size)),
                    color = MaterialTheme.colorScheme.surfaceContainer,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter),
                ) {
                    Row(Modifier.padding(horizontal = 16.dp, vertical = 18.dp), verticalAlignment = Alignment.CenterVertically) {
                        Box(Modifier.size(48.dp).clip(MaterialShapes.Cookie9Sided.toShape()).background(MaterialTheme.colorScheme.secondaryContainer),
                            contentAlignment = Alignment.Center) {
                            Icon(look.icon, contentDescription = null, tint = MaterialTheme.colorScheme.onSecondaryContainer)
                        }
                        Column(Modifier.weight(1f).padding(horizontal = 16.dp)) {
                            Text(look.title, style = MaterialTheme.typography.titleMedium)
                            Text(look.subtitle, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                        Icon(LincSymbols.OpenInNew, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
            item {
                HelperText("Runs on the PC as you, with your files and programs — the same as opening it there. Pinch to change the text size; swipe sideways to see the rest of long lines.",
                    modifier = Modifier.padding(horizontal = Kit.gutter))
            }
        }
    }
}

// ---- the session -------------------------------------------------------------------------------

/**
 * One shell, full screen: the console drawn in this phone's colours, the phone's keyboard typing
 * straight into it, and Termux's two rows of extra keys (Esc, Tab, Ctrl, Alt, arrows, Home, End,
 * Page Up/Down) in Material's shapes. Pinch to zoom, drag up and down to scroll back, sideways to see
 * the rest of long lines (the console is [WIDE_COLS] wide, not wrapped to the phone).
 * [shell] null re-attaches to the one already running.
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun TerminalSession(shell: String?, onClose: () -> Unit) {
    val context = LocalContext.current
    val prefs = remember { context.getSharedPreferences("linc", Context.MODE_PRIVATE) }
    val open by PcTools.termOpen.collectAsState()
    val error by PcTools.termError.collectAsState()
    val version by PcTools.screenVersion.collectAsState()
    val linkState by app.linc.android.service.CompanionStateHolder.state.collectAsState()
    val ready = remember(linkState) { PcTools.ready() }
    var started by remember { mutableStateOf(shell == null) }
    var fontSp by remember { mutableFloatStateOf(prefs.getFloat(KEY_FONT, 13f).coerceIn(MIN_FONT, MAX_FONT)) }
    var input by remember { mutableStateOf<TerminalInputView?>(null) }
    var ctrl by remember { mutableStateOf(false) }
    var alt by remember { mutableStateOf(false) }
    var scrollBack by remember { mutableIntStateOf(0) }
    // Sideways: the console is wider than the phone (WIDE_COLS), so lines don't wrap; a sideways drag pans it.
    var panX by remember { mutableFloatStateOf(0f) }
    var lastPanAt by remember { mutableStateOf(0L) } // when the user last panned; the cursor isn't followed for a moment after
    var follow by remember { mutableIntStateOf(0) } // bumped to bring the cursor into view now

    val colors = MaterialTheme.colorScheme
    val dark = colors.surface.luminance() < 0.5f
    val view = LocalView.current

    fun send(text: String) {
        if (text.isEmpty()) return
        var out = text
        if (ctrl && out.length == 1) {
            val c = out[0]
            out = when (c) {
                in 'a'..'z' -> (c - 'a' + 1).toChar().toString()
                in 'A'..'Z' -> (c - 'A' + 1).toChar().toString()
                ' ', '@' -> "\u0000"
                '[' -> "\u001b"; '\\' -> "\u001c"; ']' -> "\u001d"
                else -> out
            }
        }
        if (alt) out = "\u001b" + out
        ctrl = false; alt = false
        scrollBack = 0
        lastPanAt = 0L; follow++ // typing: show where it goes
        PcTools.type(out)
    }
    fun showKeyboard() {
        val v = input ?: return
        v.requestFocus()
        context.getSystemService(InputMethodManager::class.java)?.showSoftInput(v, InputMethodManager.SHOW_IMPLICIT)
    }
    fun close() {
        if (open) PcTools.closeTerminal()
        onClose()
    }
    // The pinched size, kept once the fingers settle (not on every step of the pinch).
    LaunchedEffect(fontSp) {
        kotlinx.coroutines.delay(500)
        prefs.edit { putFloat(KEY_FONT, fontSp) }
    }

    // No Surface holds this page, so text and icons would default to black: on a dark theme, unreadable.
    CompositionLocalProvider(LocalContentColor provides colors.onSurface) {
        Column(Modifier.fillMaxSize().background(colors.surface).statusBarsPadding().navigationBarsPadding().imePadding()) {
            // ---- top bar: the shell's title, keyboard, close --------------------------------------
            Row(Modifier.fillMaxWidth().height(56.dp).padding(start = 4.dp, end = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                IconButton(onClick = onClose) { Icon(LincSymbols.ArrowBack, contentDescription = "Leave it running") }
                Column(Modifier.weight(1f)) {
                    Text(shell?.let { shellLook(it).title } ?: "Terminal", style = MaterialTheme.typography.titleMedium,
                        color = colors.onSurface, maxLines = 1)
                    val title = remember(version) { PcTools.screen.title }
                    Text(if (!open && started) "Closed" else title.ifEmpty { "On your PC" }, style = MaterialTheme.typography.bodySmall,
                        color = colors.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                IconButton(onClick = { showKeyboard() }) { Icon(LincSymbols.Keyboard, contentDescription = "Show the keyboard") }
                IconButton(onClick = { close() }) { Icon(LincSymbols.Close, contentDescription = "Close the shell") }
            }

            // ---- the console ----------------------------------------------------------------------
            BoxWithConstraints(
                Modifier.weight(1f).fillMaxWidth().padding(horizontal = 8.dp).clip(RoundedCornerShape(20.dp))
                    .background(if (dark) colors.surfaceContainerLowest else colors.surfaceContainerLow),
            ) {
                val density = LocalDensity.current
                val paint = remember { Paint(Paint.ANTI_ALIAS_FLAG).apply { typeface = Typeface.MONOSPACE } }
                val textPx = with(density) { fontSp.sp.toPx() }
                paint.textSize = textPx
                val cellW = paint.measureText("M")
                val metrics = paint.fontMetrics
                val cellH = (metrics.descent - metrics.ascent) * 1.08f
                val pad = with(density) { 10.dp.toPx() }
                // The PC's console is wide (WIDE_COLS, or the screen's width if that is more), so long lines
                // stay whole and are seen by panning sideways, not wrapped onto the next line. Pinching changes
                // the cell size, and so the rows, but not the columns (unless the screen fits more than WIDE_COLS).
                // The keyboard only takes height (imePadding above), so it changes the rows and never the columns.
                val viewW = constraints.maxWidth.toFloat()
                val screenCols = ((viewW - 2 * pad) / cellW).toInt()
                val cols = maxOf(WIDE_COLS, screenCols).coerceIn(20, 300)
                val rows = ((constraints.maxHeight - 2 * pad) / cellH).toInt().coerceIn(5, 200)
                val contentW = 2 * pad + cols * cellW
                val maxPan = (contentW - viewW).coerceAtLeast(0f)
                val px = panX.coerceIn(0f, maxPan)
                val maxPanNow by rememberUpdatedState(maxPan)

                // Bring the cursor's column into view: on typing at once, on output once the user has
                // stopped panning for a moment (FOLLOW_IDLE_MS), and never while scrolled back.
                LaunchedEffect(version, follow) {
                    if (scrollBack > 0) return@LaunchedEffect
                    val idle = System.currentTimeMillis() - lastPanAt
                    if (idle < FOLLOW_IDLE_MS) kotlinx.coroutines.delay(FOLLOW_IDLE_MS - idle)
                    val col = synchronized(PcTools.screen) { PcTools.screen.cursorCol }
                    val x = pad + col * cellW
                    val margin = 4 * cellW
                    val cur = panX.coerceIn(0f, maxPan)
                    panX = when {
                        x < cur -> (x - margin).coerceIn(0f, maxPan)
                        x + cellW > cur + viewW -> (x + cellW + margin - viewW).coerceIn(0f, maxPan)
                        else -> cur
                    }
                }

                // The invisible view the keyboard types into; the console is drawn over it.
                AndroidView(
                    modifier = Modifier.fillMaxSize(),
                    factory = { ctx ->
                        TerminalInputView(ctx).also {
                            it.onText = { t -> send(t) }
                            input = it
                        }
                    },
                )
                LaunchedEffect(cols, rows) {
                    if (!started && shell != null && ready) {
                        PcTools.openTerminal(shell, cols, rows)
                        started = true
                    } else if (open) {
                        // Settled first: a pinch or the keyboard sliding in would otherwise resize the PC's console every frame.
                        kotlinx.coroutines.delay(150)
                        PcTools.resize(cols, rows)
                    }
                }
                LaunchedEffect(input) {
                    kotlinx.coroutines.delay(300)
                    showKeyboard()
                }

                val fg = colors.onSurface
                val cursor = colors.primary
                val track = colors.onSurfaceVariant
                val lineH by rememberUpdatedState(cellH) // the pinch changes it; the gesture below outlives one size
                Canvas(
                    Modifier.fillMaxSize()
                        .pointerInput(Unit) { detectTapGestures(onTap = { showKeyboard() }) }
                        .pointerInput(Unit) {
                            // One finger: past the touch slop, the drag's main direction decides — sideways pans,
                            // up and down scrolls back through the history. Two fingers: a pinch, the text size.
                            awaitEachGesture {
                                awaitFirstDown(requireUnconsumed = false)
                                var mode = 0 // 0 undecided, 1 sideways, 2 up/down, 3 pinch
                                var moved = Offset.Zero // dragged before the direction was decided
                                var dragged = 0f // pixels dragged up/down and not yet a whole line
                                while (true) {
                                    val event = awaitPointerEvent()
                                    if (event.changes.none { it.pressed }) break
                                    val fingers = event.changes.count { it.pressed }
                                    if (fingers >= 2) {
                                        mode = 3
                                        val zoom = event.calculateZoom()
                                        if (zoom != 1f && zoom.isFinite() && zoom > 0f) {
                                            val before = fontSp
                                            fontSp = (fontSp * zoom).coerceIn(MIN_FONT, MAX_FONT)
                                            panX = (panX * (fontSp / before)).coerceAtLeast(0f) // keep roughly the same columns in view
                                        }
                                        event.changes.forEach { if (it.positionChanged()) it.consume() }
                                        continue
                                    }
                                    if (mode == 3) continue // one finger left after a pinch: nothing until it lifts
                                    var pan = event.calculatePan()
                                    if (mode == 0) {
                                        moved += pan
                                        if (moved.getDistance() < viewConfiguration.touchSlop) continue
                                        mode = if (abs(moved.x) > abs(moved.y)) 1 else 2
                                        pan = moved
                                    }
                                    if (mode == 1) {
                                        panX = (panX.coerceIn(0f, maxPanNow) - pan.x).coerceIn(0f, maxPanNow)
                                        lastPanAt = System.currentTimeMillis()
                                    } else {
                                        dragged += pan.y
                                        val steps = (dragged / lineH).toInt()
                                        if (steps != 0) {
                                            dragged -= steps * lineH
                                            val max = synchronized(PcTools.screen) { PcTools.screen.scrollbackSize }
                                            scrollBack = (scrollBack + steps).coerceIn(0, max)
                                        }
                                    }
                                    event.changes.forEach { if (it.positionChanged()) it.consume() }
                                }
                            }
                        }
                        .semantics { contentDescription = "Terminal" },
                ) {
                    val drawn = version // read here, so the console redraws when it changes
                    if (drawn < 0) return@Canvas
                    val screen = PcTools.screen
                    val (lines, cRow, cCol, cVisible) = synchronized(screen) {
                        Quad(screen.lines().map { Snapshot(String(it.chars), it.attrs.copyOf()) }, screen.cursorRow, screen.cursorCol, screen.cursorVisible)
                    }
                    val first = (lines.size - rows - scrollBack).coerceAtLeast(0)
                    val canvas = drawContext.canvas.nativeCanvas
                    canvas.save()
                    canvas.translate(-px, 0f) // the columns panned off to the left
                    for (r in 0 until rows) {
                        val line = lines.getOrNull(first + r) ?: break
                        val y = pad + r * cellH
                        val baseline = y - metrics.ascent
                        var i = 0
                        val n = line.text.length
                        while (i < n) {
                            val attr = line.attrs[i]
                            var j = i
                            while (j < n && line.attrs[j] == attr) j++
                            val inverse = attr and TerminalEmulator.INVERSE != 0L
                            var f = TerminalEmulator.fg(attr).let { if (it < 0) fg else themed(Color(0xFF000000 or it.toLong()), dark, fg) }
                            var b = TerminalEmulator.bg(attr).let { if (it < 0) null else Color(0xFF000000 or it.toLong()) }
                            if (inverse) { val t = b ?: Color.Transparent; b = f; f = if (t == Color.Transparent) colors.surface else t }
                            val x = pad + i * cellW
                            if (b != null) {
                                paint.color = b.toArgb()
                                canvas.drawRect(x, y, x + (j - i) * cellW, y + cellH, paint)
                            }
                            if (line.text.substring(i, j).isNotBlank()) {
                                paint.color = f.toArgb()
                                paint.isFakeBoldText = attr and TerminalEmulator.BOLD != 0L
                                paint.isUnderlineText = attr and TerminalEmulator.UNDERLINE != 0L
                                drawCells(canvas, paint, line.text, i, j, pad, baseline, cellW)
                            }
                            i = j
                        }
                    }
                    // The cursor: a block in the theme's primary colour, on the live screen only.
                    val cursorLine = lines.size - screen.rows + cRow
                    if (cVisible && open && scrollBack == 0 && cursorLine >= first && cursorLine < first + rows) {
                        val x = pad + cCol * cellW
                        val y = pad + (cursorLine - first) * cellH
                        paint.color = cursor.copy(alpha = 0.75f).toArgb()
                        canvas.drawRect(x, y, x + cellW, y + cellH, paint)
                    }
                    canvas.restore()
                    // A thin bar along the bottom: how much of the width is in view, and where.
                    if (maxPan > 0f) {
                        val barH = 3.dp.toPx()
                        val barY = size.height - barH - 3.dp.toPx()
                        val inset = 12.dp.toPx()
                        val trackW = size.width - 2 * inset
                        val thumbW = (trackW * viewW / contentW).coerceIn(24.dp.toPx().coerceAtMost(trackW), trackW)
                        val thumbX = inset + (trackW - thumbW) * (px / maxPan)
                        drawRoundRect(track.copy(alpha = 0.12f), Offset(inset, barY), Size(trackW, barH), CornerRadius(barH / 2))
                        drawRoundRect(track.copy(alpha = 0.55f), Offset(thumbX, barY), Size(thumbW, barH), CornerRadius(barH / 2))
                    }
                }
                if (!ready) {
                    HelperText("Connect the PC, and update Linc on it, to open a terminal.", kind = Explanations.Kind.Blocked,
                        modifier = Modifier.align(Alignment.Center).padding(24.dp))
                } else if (started && !open && shell == null) {
                    Button(onClick = onClose, modifier = Modifier.align(Alignment.Center)) { Text("The shell has closed") }
                }
                val cursorCol = remember(version) { synchronized(PcTools.screen) { PcTools.screen.cursorCol } }
                val cursorX = pad + cursorCol * cellW
                val cursorOff = cursorX < px || cursorX + cellW > px + viewW
                if (scrollBack > 0 || (open && cursorOff)) {
                    Surface(onClick = { scrollBack = 0; lastPanAt = 0L; panX = 0f; follow++ }, shape = RoundedCornerShape(16.dp), color = colors.secondaryContainer,
                        modifier = Modifier.align(Alignment.BottomEnd).padding(12.dp)) {
                        Row(Modifier.padding(horizontal = 12.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                            Icon(LincSymbols.ArrowDown, contentDescription = null, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.width(6.dp))
                            Text(if (scrollBack > 0) "Back to the bottom" else "Back to the cursor", style = MaterialTheme.typography.labelLarge)
                        }
                    }
                }
            }
            error?.let { HelperText(it, kind = Explanations.Kind.Error, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp)) }

            // ---- the extra keys -----------------------------------------------------------------
            ExtraKeys(ctrl = ctrl, alt = alt,
                onCtrl = { ctrl = !ctrl; view.performHapticFeedback(android.view.HapticFeedbackConstants.CLOCK_TICK) },
                onAlt = { alt = !alt; view.performHapticFeedback(android.view.HapticFeedbackConstants.CLOCK_TICK) },
                onKey = { seq -> view.performHapticFeedback(android.view.HapticFeedbackConstants.KEYBOARD_TAP); send(seq) })
        }
    }

    DisposableEffect(Unit) { onDispose { input = null } }
}

private data class Snapshot(val text: String, val attrs: LongArray)

/**
 * Cells [from] until [to] of one line, each at its own column. Plain ASCII is drawn in runs (the
 * monospace font's advance is the cell); anything else — box drawing, accents, CJK, emoji — is
 * drawn one character at a time, fitted to its one or two cells, since a fallback font's glyphs
 * are rarely the cell's width and would push the rest of the line out of its columns.
 */
private fun drawCells(canvas: android.graphics.Canvas, paint: Paint, text: String, from: Int, to: Int,
                      left: Float, baseline: Float, cellW: Float) {
    var k = from
    while (k < to) {
        var e = k
        while (e < to && text[e].code in 0x20..0x7E) e++
        if (e > k) { canvas.drawText(text, k, e, left + k * cellW, baseline, paint); k = e; continue }
        val c = text[k]
        val next = if (k + 1 < text.length) text[k + 1] else ' '
        if (TerminalEmulator.isTail(c) || (c.isHighSurrogate() && !next.isLowSurrogate())) { k++; continue }
        val wide = TerminalEmulator.isWideHead(c, next)
        val glyph = if (c.isHighSurrogate()) text.substring(k, k + 2) else c.toString()
        val want = (if (wide) 2 else 1) * cellW
        val have = paint.measureText(glyph)
        val x = left + k * cellW
        if (have > 0f && (have > want + 0.5f || (c.code in 0x2500..0x259F && abs(have - want) > 0.5f))) {
            // Too wide for its cells, or a line-drawing piece that must meet its neighbours: fitted.
            paint.textScaleX = want / have
            canvas.drawText(glyph, x, baseline, paint)
            paint.textScaleX = 1f
        } else canvas.drawText(glyph, x + (want - have).coerceAtLeast(0f) / 2, baseline, paint)
        k += if (wide) 2 else 1
    }
}
private data class Quad(val lines: List<Snapshot>, val row: Int, val col: Int, val visible: Boolean)

/**
 * Console colours that keep to the theme: on a light page, the console's near-white text (made for
 * a black window) becomes the page's own text colour; on a dark page, its near-black text does.
 */
private fun themed(c: Color, dark: Boolean, fg: Color): Color {
    val l = c.luminance()
    return when {
        !dark && l > 0.7f -> fg
        dark && l < 0.03f -> fg
        else -> c
    }
}

/** A key on the extra-keys rows: what it shows, and what it sends ("" for Ctrl/Alt). */
private data class Key(val label: String, val send: String, val icon: ImageVector? = null)

private val ROW_1 = listOf(
    Key("Esc", "\u001b"), Key("/", "/"), Key("-", "-"), Key("Home", "\u001b[H"),
    Key("Up", "\u001b[A", LincSymbols.ArrowUp), Key("End", "\u001b[F"), Key("PgUp", "\u001b[5~"),
)
private val ROW_2 = listOf(
    Key("Tab", "\t", LincSymbols.KeyboardTab), Key("Ctrl", ""), Key("Alt", ""), Key("Left", "\u001b[D", LincSymbols.ArrowLeft),
    Key("Down", "\u001b[B", LincSymbols.ArrowDown), Key("Right", "\u001b[C", LincSymbols.ArrowRight), Key("PgDn", "\u001b[6~"),
)

@Composable
private fun ExtraKeys(ctrl: Boolean, alt: Boolean, onCtrl: () -> Unit, onAlt: () -> Unit, onKey: (String) -> Unit) {
    Column(Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 6.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        for (row in listOf(ROW_1, ROW_2)) Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
            for (k in row) {
                val latched = (k.label == "Ctrl" && ctrl) || (k.label == "Alt" && alt)
                Surface(
                    onClick = {
                        when (k.label) {
                            "Ctrl" -> onCtrl()
                            "Alt" -> onAlt()
                            else -> onKey(k.send)
                        }
                    },
                    shape = RoundedCornerShape(12.dp),
                    color = if (latched) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surfaceContainerHigh,
                    contentColor = if (latched) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurface,
                    modifier = Modifier.weight(1f).height(42.dp).semantics { contentDescription = k.label },
                ) {
                    Box(contentAlignment = Alignment.Center) {
                        if (k.icon != null) Icon(k.icon, contentDescription = null, modifier = Modifier.size(20.dp))
                        else Text(k.label, style = MaterialTheme.typography.labelLarge, fontWeight = FontWeight.Medium, maxLines = 1)
                    }
                }
            }
        }
    }
}

/**
 * What the phone's keyboard types into: no text box, every key straight to the shell, as in
 * Termux. A visible-password field so keyboards don't suggest, correct or hold back letters.
 */
class TerminalInputView(context: Context) : View(context) {

    var onText: (String) -> Unit = {}

    init {
        isFocusable = true
        isFocusableInTouchMode = true
    }

    override fun onCheckIsTextEditor() = true

    override fun onCreateInputConnection(outAttrs: EditorInfo): InputConnection {
        outAttrs.inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
        outAttrs.imeOptions = EditorInfo.IME_FLAG_NO_FULLSCREEN or EditorInfo.IME_FLAG_NO_EXTRACT_UI or EditorInfo.IME_ACTION_NONE
        return object : BaseInputConnection(this, true) {
            override fun commitText(text: CharSequence?, newCursorPosition: Int): Boolean {
                super.commitText(text, newCursorPosition)
                flush()
                return true
            }

            override fun finishComposingText(): Boolean {
                super.finishComposingText()
                flush()
                return true
            }

            override fun deleteSurroundingText(beforeLength: Int, afterLength: Int): Boolean {
                repeat(beforeLength.coerceAtLeast(1)) { onText("\u007f") }
                return true
            }

            override fun sendKeyEvent(event: KeyEvent): Boolean {
                if (event.action == KeyEvent.ACTION_DOWN) handleKey(event.keyCode, event)
                return true
            }

            private fun flush() {
                val e = editable ?: return
                val text = e.toString()
                e.clear()
                if (text.isNotEmpty()) onText(text.replace("\n", "\r"))
            }
        }
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean = handleKey(keyCode, event) || super.onKeyDown(keyCode, event)

    /** Keys from a hardware keyboard, or sent as events by the soft one. */
    private fun handleKey(keyCode: Int, event: KeyEvent): Boolean {
        val seq = when (keyCode) {
            KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER -> "\r"
            KeyEvent.KEYCODE_DEL -> "\u007f"
            KeyEvent.KEYCODE_FORWARD_DEL -> "\u001b[3~"
            KeyEvent.KEYCODE_TAB -> "\t"
            KeyEvent.KEYCODE_ESCAPE -> "\u001b"
            KeyEvent.KEYCODE_DPAD_UP -> "\u001b[A"
            KeyEvent.KEYCODE_DPAD_DOWN -> "\u001b[B"
            KeyEvent.KEYCODE_DPAD_RIGHT -> "\u001b[C"
            KeyEvent.KEYCODE_DPAD_LEFT -> "\u001b[D"
            KeyEvent.KEYCODE_MOVE_HOME -> "\u001b[H"
            KeyEvent.KEYCODE_MOVE_END -> "\u001b[F"
            KeyEvent.KEYCODE_PAGE_UP -> "\u001b[5~"
            KeyEvent.KEYCODE_PAGE_DOWN -> "\u001b[6~"
            KeyEvent.KEYCODE_BACK -> return false
            else -> {
                val ch = event.getUnicodeChar(event.metaState and KeyEvent.META_CTRL_MASK.inv())
                if (ch == 0) return false
                if (event.isCtrlPressed && ch.toChar().isLetter()) ((ch.toChar().lowercaseChar() - 'a') + 1).toChar().toString()
                else String(Character.toChars(ch))
            }
        }
        onText(if (event.isAltPressed && seq.length == 1) "\u001b$seq" else seq)
        return true
    }
}

private const val KEY_FONT = "terminal_font_sp"
private const val MIN_FONT = 6f
private const val MAX_FONT = 24f
/** The PC console's width at the least: wide enough that lines stay whole, panned sideways on the phone. */
private const val WIDE_COLS = 160
/** How long after a sideways pan before new output brings the cursor back into view. */
private const val FOLLOW_IDLE_MS = 1500L
