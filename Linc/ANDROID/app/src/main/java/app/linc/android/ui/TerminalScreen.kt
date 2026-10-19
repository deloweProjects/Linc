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
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectTransformGestures
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
import androidx.compose.material3.MaterialShapes
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.toShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.graphics.nativeCanvas
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
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
                HelperText("Runs on the PC as you, with your files and programs — the same as opening it there. Pinch to change the text size.",
                    modifier = Modifier.padding(horizontal = Kit.gutter))
            }
        }
    }
}

// ---- the session -------------------------------------------------------------------------------

/**
 * One shell, full screen: the console drawn in this phone's colours, the phone's keyboard typing
 * straight into it, and Termux's two rows of extra keys (Esc, Tab, Ctrl, Alt, arrows, Home, End,
 * Page Up/Down) in Material's shapes. Pinch to zoom, drag to scroll back.
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
    var fontSp by remember { mutableFloatStateOf(prefs.getFloat(KEY_FONT, 13f)) }
    var input by remember { mutableStateOf<TerminalInputView?>(null) }
    var ctrl by remember { mutableStateOf(false) }
    var alt by remember { mutableStateOf(false) }
    var scrollBack by remember { mutableIntStateOf(0) }

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

    Column(Modifier.fillMaxSize().background(colors.surface).statusBarsPadding().navigationBarsPadding().imePadding()) {
        // ---- top bar: the shell's title, keyboard, close --------------------------------------
        Row(Modifier.fillMaxWidth().height(56.dp).padding(start = 4.dp, end = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onClose) { Icon(LincSymbols.ArrowBack, contentDescription = "Leave it running") }
            Column(Modifier.weight(1f)) {
                Text(shell?.let { shellLook(it).title } ?: "Terminal", style = MaterialTheme.typography.titleMedium, maxLines = 1)
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
            val cols = ((constraints.maxWidth - 2 * pad) / cellW).toInt().coerceIn(20, 300)
            val rows = ((constraints.maxHeight - 2 * pad) / cellH).toInt().coerceIn(5, 200)

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
                } else if (open) PcTools.resize(cols, rows)
            }
            LaunchedEffect(input) {
                kotlinx.coroutines.delay(300)
                showKeyboard()
            }

            val fg = colors.onSurface
            val cursor = colors.primary
            Canvas(
                Modifier.fillMaxSize()
                    .pointerInput(Unit) { detectTapGestures(onTap = { showKeyboard() }) }
                    .pointerInput(Unit) {
                        detectTransformGestures { _, pan, zoom, _ ->
                            if (zoom != 1f) {
                                fontSp = (fontSp * zoom).coerceIn(7f, 28f)
                                prefs.edit { putFloat(KEY_FONT, fontSp) }
                            }
                            if (pan.y != 0f) {
                                val max = synchronized(PcTools.screen) { PcTools.screen.scrollbackSize }
                                scrollBack = (scrollBack + (pan.y / cellH).toInt()).coerceIn(0, max)
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
                for (r in 0 until rows) {
                    val line = lines.getOrNull(first + r) ?: break
                    val y = pad + r * cellH
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
                        val run = line.text.substring(i, j)
                        if (run.isNotBlank()) {
                            paint.color = f.toArgb()
                            paint.isFakeBoldText = attr and TerminalEmulator.BOLD != 0L
                            paint.isUnderlineText = attr and TerminalEmulator.UNDERLINE != 0L
                            canvas.drawText(run, x, y - metrics.ascent, paint)
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
            }
            if (!ready) {
                HelperText("Connect the PC, and update Linc on it, to open a terminal.", kind = Explanations.Kind.Blocked,
                    modifier = Modifier.align(Alignment.Center).padding(24.dp))
            } else if (started && !open && shell == null) {
                Button(onClick = onClose, modifier = Modifier.align(Alignment.Center)) { Text("The shell has closed") }
            }
            if (scrollBack > 0) {
                Surface(onClick = { scrollBack = 0 }, shape = RoundedCornerShape(16.dp), color = colors.secondaryContainer,
                    modifier = Modifier.align(Alignment.BottomEnd).padding(12.dp)) {
                    Row(Modifier.padding(horizontal = 12.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                        Icon(LincSymbols.ArrowDown, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text("Back to the bottom", style = MaterialTheme.typography.labelLarge)
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

    DisposableEffect(Unit) { onDispose { input = null } }
}

private data class Snapshot(val text: String, val attrs: LongArray)
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
