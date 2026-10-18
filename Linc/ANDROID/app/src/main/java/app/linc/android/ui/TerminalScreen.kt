package app.linc.android.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Button
import androidx.compose.material3.FilledIconButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.linc.android.service.Explanations
import app.linc.android.service.PcTools
import app.linc.android.service.TerminalEmulator
import app.linc.android.ui.kit.ConnectedChoice
import app.linc.android.ui.kit.HelperText
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.LincScaffold

/**
 * Tools → Terminal (D-097): PowerShell, Command Prompt or WSL on the PC, in a real Windows
 * console — the same prompts, colours and programs as at the PC. Type a line and send it; the
 * keys row has what a phone keyboard lacks (Tab, Ctrl+C, Esc, arrows).
 */
@Composable
fun TerminalScreen(onBack: () -> Unit) {
    val shells by PcTools.shells.collectAsState()
    val open by PcTools.termOpen.collectAsState()
    val error by PcTools.termError.collectAsState()
    val version by PcTools.screenVersion.collectAsState()
    val ready = PcTools.ready()
    var shell by rememberSaveable { mutableStateOf("powershell") }
    var line by rememberSaveable { mutableStateOf("") }
    LaunchedEffect(ready) { if (ready) PcTools.refreshShells() }

    LincScaffold(title = "Terminal", subtitle = if (open) PcTools.screen.title.ifEmpty { label(shell) } else null, onBack = {
        if (open) PcTools.closeTerminal()
        onBack()
    }) { inner ->
        Column(Modifier.fillMaxSize().padding(inner).imePadding().padding(horizontal = Kit.gutter, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp)) {
            if (!ready) HelperText("Connect the PC, and update Linc on it, to open a terminal.", kind = Explanations.Kind.Blocked)
            error?.let { HelperText(it, kind = Explanations.Kind.Error) }
            if (!open) {
                ConnectedChoice(shells.map { it to label(it) }, shell, { shell = it }, enabled = ready)
                HelperText("Runs on the PC as you, with your files and programs — the same as opening ${label(shell)} there.")
            }
            BoxWithConstraints(Modifier.weight(1f).fillMaxWidth().clip(RoundedCornerShape(16.dp)).background(Color(0xFF0C0C0C))) {
                val measurer = rememberTextMeasurer()
                val style = TextStyle(fontFamily = FontFamily.Monospace, fontSize = 12.sp, color = Color(0xFFCCCCCC))
                val density = LocalDensity.current
                val cell = remember(style) { measurer.measure("M", style).size }
                val cols = with(density) { ((maxWidth.toPx() - 16.dp.toPx()) / cell.width).toInt().coerceIn(20, 300) }
                val rows = with(density) { ((maxHeight.toPx() - 16.dp.toPx()) / cell.height).toInt().coerceIn(5, 200) }
                LaunchedEffect(cols, rows, open) { if (open) PcTools.resize(cols, rows) }
                if (!open) {
                    Button(onClick = { PcTools.openTerminal(shell, cols, rows) }, enabled = ready, modifier = Modifier.align(Alignment.Center)) {
                        Text("Open ${label(shell)}")
                    }
                } else {
                    TerminalView(version, style)
                }
            }
            if (open) {
                Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    for ((name, keys) in KEYS) AssistChip(onClick = { PcTools.type(keys) }, label = { Text(name) })
                }
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    fun send() { PcTools.type(line + "\r"); line = "" }
                    OutlinedTextField(
                        value = line, onValueChange = { line = it }, singleLine = true, modifier = Modifier.weight(1f),
                        placeholder = { Text("Type a command") },
                        textStyle = TextStyle(fontFamily = FontFamily.Monospace),
                        keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send, autoCorrectEnabled = false),
                        keyboardActions = KeyboardActions(onSend = { send() }),
                    )
                    FilledIconButton(onClick = { send() }) { Icon(LincSymbols.Send, contentDescription = "Send") }
                }
                TextButton(onClick = { PcTools.closeTerminal() }) { Text("Close the terminal") }
            }
        }
    }
}

private fun label(shell: String) = when (shell) {
    "cmd" -> "Command Prompt"
    "wsl" -> "WSL"
    else -> "PowerShell"
}

/** The keys a phone keyboard lacks, as the console wants them. */
private val KEYS = listOf(
    "Tab" to "\t", "Ctrl+C" to "\u0003", "Esc" to "\u001b", "↑" to "\u001b[A", "↓" to "\u001b[B",
    "←" to "\u001b[D", "→" to "\u001b[C", "Ctrl+Z" to "\u001a", "Ctrl+L" to "\u000c", "Enter" to "\r",
)

@Composable
private fun TerminalView(version: Long, style: TextStyle) {
    val screen = PcTools.screen
    val lines = remember(version) { synchronized(screen) { screen.lines().map { render(it) } } }
    val list = rememberLazyListState()
    LaunchedEffect(version) { if (lines.isNotEmpty()) list.scrollToItem(lines.lastIndex) }
    LazyColumn(state = list, modifier = Modifier.fillMaxSize().padding(8.dp)) {
        items(lines.size) { i -> Text(lines[i], style = style, softWrap = false, maxLines = 1) }
    }
}

/** One terminal line as styled text: runs of the same look become one span. */
private fun render(line: TerminalEmulator.Line): AnnotatedString = buildAnnotatedString {
    val text = String(line.chars).trimEnd()
    var i = 0
    while (i < text.length) {
        val attr = line.attrs[i]
        var j = i
        while (j < text.length && line.attrs[j] == attr) j++
        val inverse = attr and TerminalEmulator.INVERSE != 0L
        val fg = TerminalEmulator.fg(attr).let { if (it < 0) null else Color(0xFF000000 or it.toLong()) }
        val bg = TerminalEmulator.bg(attr).let { if (it < 0) null else Color(0xFF000000 or it.toLong()) }
        pushStyle(SpanStyle(
            color = (if (inverse) bg ?: Color(0xFF0C0C0C) else fg) ?: Color.Unspecified,
            background = (if (inverse) fg ?: Color(0xFFCCCCCC) else bg) ?: Color.Unspecified,
            fontWeight = if (attr and TerminalEmulator.BOLD != 0L) FontWeight.Bold else null,
            textDecoration = if (attr and TerminalEmulator.UNDERLINE != 0L) TextDecoration.Underline else null,
        ))
        append(text.substring(i, j))
        pop()
        i = j
    }
}
