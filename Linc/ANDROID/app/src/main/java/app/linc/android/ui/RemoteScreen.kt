@file:OptIn(androidx.compose.material3.ExperimentalMaterial3ExpressiveApi::class, androidx.compose.material3.ExperimentalMaterial3Api::class)

package app.linc.android.ui

import android.Manifest
import android.content.Intent
import android.os.Bundle
import android.speech.RecognitionListener
import android.speech.RecognizerIntent
import android.speech.SpeechRecognizer
import android.view.HapticFeedbackConstants
import android.view.inputmethod.InputMethodManager
import android.widget.EditText
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.FilledIconButton
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.FloatingToolbarDefaults
import androidx.compose.material3.HorizontalFloatingToolbar
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.IconButtonDefaults
import androidx.compose.material3.IconToggleButton
import androidx.compose.material3.MaterialShapes
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Slider
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.ToggleButton
import androidx.compose.material3.rememberModalBottomSheetState
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
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import app.linc.android.service.CompanionStateHolder
import app.linc.android.service.CompanionStateHolder.ServiceState
import app.linc.android.service.Explanations
import app.linc.android.service.KeyboardMapping
import app.linc.android.service.KeyboardSink
import app.linc.android.service.MirrorControl
import app.linc.android.service.MirrorSettings
import app.linc.android.service.ModifierKeys
import app.linc.android.service.PcControl
import app.linc.android.service.PcMedia
import app.linc.android.service.PcMediaControl
import app.linc.android.service.PcMediaStore
import app.linc.android.service.PcVoiceAccess
import app.linc.android.service.PresentationControl
import app.linc.android.service.PresentationKeys
import app.linc.android.service.TrackpadGestures
import app.linc.android.ui.kit.BlockedState
import app.linc.android.ui.kit.ConnectedChoice
import app.linc.android.ui.kit.HelperText
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.LincScaffold
import app.linc.android.ui.kit.ProgressLine
import app.linc.android.ui.kit.SectionHeader
import app.linc.android.ui.kit.SettingsGroup
import app.linc.android.ui.kit.SettingsRow
import app.linc.android.ui.kit.TileGrid
import app.linc.android.ui.kit.ToolTile
import app.linc.android.ui.kit.page
import app.linc.android.ui.theme.LincTheme

/**
 * Remote (1.0.0.11, D-082; was Tools, M4a–M4c): the PC's trackpad fills the screen, with Left |
 * Right buttons under it and a floating toolbar of sheets — Keyboard, Media, Present, Voice,
 * Power — and Mirror PC. Works with no mirror session (D-080).
 *
 * **Disabled, never hidden.** Three gates keep their meaning: v14 for input (trackpad, keyboard,
 * presentation keys), v17 for quick controls (power, volume, brightness), v13 for media. A blocked
 * toolbar item stays, disabled, and its sheet (or the pad) says why.
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun RemoteScreen(
    onSettings: (() -> Unit)? = null,
    onBack: (() -> Unit)? = null,
    onMirror: () -> Unit,
) {
    val connection by CompanionStateHolder.state.collectAsState()
    val inputReason = inputReason(connection)
    val controlsReason = controlsReason(connection)
    val mediaReason = mediaReason(connection)
    val inputReady = inputReason == null
    var sheet by remember { mutableStateOf<RemoteSheet?>(null) }

    // PROTOCOL.md v17 has no unsolicited pc.state push: ask when Remote opens and on reconnect.
    LaunchedEffect(connection) { PcControl.requestState() }

    LincScaffold(title = "Tools", onSettings = onSettings, onBack = onBack) { inner ->
        Column(
            Modifier.page(inner)
                .then(if (onBack != null) Modifier.navigationBarsPadding() else Modifier)
                .padding(horizontal = Kit.gutter),
            verticalArrangement = Arrangement.spacedBy(Kit.groupGap),
        ) {
            ToolShortcuts()
            Trackpad(
                ready = inputReady,
                blockedReason = inputReason,
                modifier = Modifier.weight(1f).fillMaxWidth(),
            )
            ClickButtons(enabled = inputReady)
            // The pad explains a dead link; an older PC that has only some features gets one line.
            val partial = if (inputReady) controlsReason ?: mediaReason else null
            if (partial != null) {
                HelperText(partial, kind = Explanations.Kind.Blocked)
            }
            RemoteToolbar(
                inputReady = inputReady,
                mediaReady = mediaReason == null,
                controlsReady = controlsReason == null,
                voiceReady = PcVoiceAccess.ready() || PcVoiceAccess.micReady(),
                onOpen = { sheet = it },
                onMirror = onMirror,
                modifier = Modifier.align(Alignment.CenterHorizontally).padding(bottom = 12.dp),
            )
        }
    }

    sheet?.let { open ->
        RemoteSheetHost(open, onDismiss = { sheet = null }) {
            when (open) {
                RemoteSheet.Keyboard -> KeyboardSheet(inputReason)
                RemoteSheet.Media -> MediaSheet(mediaReason, controlsReason)
                RemoteSheet.Present -> PresentSheet(inputReason)
                RemoteSheet.Voice -> VoiceSheet()
                RemoteSheet.Power -> PowerSheet(controlsReason)
            }
        }
    }
}

enum class RemoteSheet { Keyboard, Media, Present, Voice, Power }

/** The full-page tools (D-097): a row of tonal chips above the pad, so the pad keeps its room. */
@Composable
private fun ToolShortcuts() {
    val context = androidx.compose.ui.platform.LocalContext.current
    Row(
        Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        for ((tool, label, icon) in listOf(
            Triple(app.linc.android.PcToolActivity.SOUND, "Sound", LincSymbols.Devices),
            Triple(app.linc.android.PcToolActivity.PC, "PC", LincSymbols.Computer),
            Triple(app.linc.android.PcToolActivity.FILES, "Files", LincSymbols.Folder),
            Triple(app.linc.android.PcToolActivity.TERMINAL, "Terminal", LincSymbols.TextSnippet),
        )) {
            androidx.compose.material3.FilledTonalButton(
                onClick = { app.linc.android.PcToolActivity.open(context, tool) },
                contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 16.dp, vertical = 8.dp),
            ) {
                Icon(icon, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(8.dp))
                Text(label)
            }
        }
    }
}

// ---- reasons (the three gates) --------------------------------------------------------------

/** Plain-language reason the trackpad/keyboard/presentation keys can't be used, or null. */
internal fun inputReason(state: ServiceState): String? = when (state) {
    is ServiceState.Stopped -> "Linc isn't running on this phone. Start it from Home."
    is ServiceState.Listening -> "Waiting for your PC to connect."
    is ServiceState.Connected ->
        if (state.protocolVersion >= 14) null
        else "Your PC's Linc app needs updating to use the trackpad and keyboard."
}

/** v17 quick controls: lock, sleep, restart, shut down, volume, brightness. */
internal fun controlsReason(state: ServiceState): String? = when (state) {
    is ServiceState.Stopped -> "Linc isn't running on this phone. Start it from Home."
    is ServiceState.Listening -> "Waiting for your PC to connect."
    is ServiceState.Connected ->
        if (state.protocolVersion >= 17) null
        else "Your PC's Linc app needs updating to use power, volume and brightness."
}

/** v13 media transport: a lower gate than the other two. */
internal fun mediaReason(state: ServiceState): String? = when (state) {
    is ServiceState.Stopped -> "Linc isn't running on this phone. Start it from Home."
    is ServiceState.Listening -> "Waiting for your PC to connect."
    is ServiceState.Connected ->
        if (state.protocolVersion >= 13) null
        else "Your PC's Linc app needs updating to control its media."
}

// ---- trackpad --------------------------------------------------------------------------------

@Composable
private fun Trackpad(ready: Boolean, blockedReason: String?, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val prefs = remember { context.getSharedPreferences("linc", android.content.Context.MODE_PRIVATE) }
    var gestures by remember { mutableIntStateOf(prefs.getInt(KEY_HINT_GESTURES, 0)) }
    val dot = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.14f)
    Surface(
        modifier = modifier.semantics { contentDescription = "Trackpad" },
        shape = RoundedCornerShape(Kit.heroRadius),
        color = MaterialTheme.colorScheme.surfaceContainerHigh,
    ) {
        Box(Modifier.fillMaxSize()) {
            Canvas(Modifier.fillMaxSize()) {
                val step = 24.dp.toPx()
                var y = step
                while (y < size.height) {
                    var x = step
                    while (x < size.width - step) {
                        drawCircle(dot, radius = 1.5.dp.toPx(), center = Offset(x, y))
                        x += step
                    }
                    y += step
                }
            }
            Box(
                Modifier.fillMaxSize().padding(end = 28.dp).trackpad(enabled = ready) {
                    if (gestures < HINT_GESTURES) {
                        gestures++
                        prefs.edit().putInt(KEY_HINT_GESTURES, gestures).apply()
                    }
                },
            )
            ScrollStrip(enabled = ready, modifier = Modifier.align(Alignment.CenterEnd))
            if (!ready) {
                BlockedState(
                    title = "Trackpad unavailable",
                    reason = blockedReason ?: "",
                    icon = LincSymbols.Mouse,
                    modifier = Modifier.align(Alignment.Center),
                )
            } else {
                AnimatedVisibility(
                    visible = gestures < HINT_GESTURES,
                    enter = fadeIn(MaterialTheme.motionScheme.defaultEffectsSpec()),
                    exit = fadeOut(MaterialTheme.motionScheme.slowEffectsSpec()),
                    modifier = Modifier.align(Alignment.TopCenter).padding(top = 16.dp),
                ) { HintChips() }
            }
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun HintChips() {
    FlowRow(
        horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.CenterHorizontally),
        verticalArrangement = Arrangement.spacedBy(8.dp),
        modifier = Modifier.padding(horizontal = 16.dp),
    ) {
        listOf("Tap to click", "Two fingers to scroll", "Two-finger tap: right click", "Tap, then drag").forEach {
            Text(
                it,
                style = MaterialTheme.typography.labelLarge,
                color = MaterialTheme.colorScheme.onSecondaryContainer,
                modifier = Modifier
                    .clip(CircleShape)
                    .background(MaterialTheme.colorScheme.secondaryContainer)
                    .padding(horizontal = 12.dp, vertical = 6.dp),
            )
        }
    }
}

/** A 24 dp strip on the right edge: drag up and down to scroll, as on a laptop's touchpad. */
@Composable
private fun ScrollStrip(enabled: Boolean, modifier: Modifier = Modifier) {
    val track = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.22f)
    var acc by remember { mutableFloatStateOf(0f) }
    Box(
        modifier
            .width(28.dp)
            .fillMaxHeight()
            .padding(vertical = 24.dp)
            .semantics { contentDescription = "Scroll strip" }
            .then(
                if (!enabled) Modifier else Modifier.pointerInput(Unit) {
                    detectVerticalDragGestures(onDragEnd = { acc = 0f }) { change, dy ->
                        change.consume()
                        acc += dy / density * TrackpadGestures.WHEEL_PER_DP
                        val unit = if (MirrorControl.v20()) 1 else 120
                        val q = acc.toInt() / unit * unit
                        if (q != 0) {
                            MirrorControl.scrollPrecise(0, q)
                            acc -= q
                        }
                    }
                },
            ),
        contentAlignment = Alignment.Center,
    ) {
        Box(Modifier.width(4.dp).fillMaxHeight().clip(CircleShape).background(track))
    }
}

/** Left | Right: connected buttons that press and release with the finger (hold = hold). */
@Composable
private fun ClickButtons(enabled: Boolean) {
    val view = LocalView.current
    Row(Modifier.fillMaxWidth().height(56.dp), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
        listOf("left" to "Left", "right" to "Right").forEachIndexed { index, (button, label) ->
            val shape = if (index == 0) {
                RoundedCornerShape(topStart = 28.dp, bottomStart = 28.dp, topEnd = 6.dp, bottomEnd = 6.dp)
            } else {
                RoundedCornerShape(topStart = 6.dp, bottomStart = 6.dp, topEnd = 28.dp, bottomEnd = 28.dp)
            }
            var pressed by remember { mutableStateOf(false) }
            Surface(
                shape = shape,
                color = if (pressed) MaterialTheme.colorScheme.secondary else MaterialTheme.colorScheme.secondaryContainer,
                contentColor = if (pressed) MaterialTheme.colorScheme.onSecondary else MaterialTheme.colorScheme.onSecondaryContainer,
                modifier = Modifier
                    .weight(1f)
                    .fillMaxHeight()
                    .alpha(if (enabled) 1f else 0.38f)
                    .semantics { contentDescription = "$label click" }
                    .then(
                        if (!enabled) Modifier else Modifier.pointerInput(button) {
                            detectTapGestures(onPress = {
                                pressed = true
                                view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                                MirrorControl.trackpadButton(button, true)
                                tryAwaitRelease()
                                MirrorControl.trackpadButton(button, false)
                                pressed = false
                            })
                        },
                    ),
            ) {
                Box(contentAlignment = Alignment.Center) {
                    Text(label, style = MaterialTheme.typography.labelLarge, fontWeight = FontWeight.SemiBold)
                }
            }
        }
    }
}

// ---- toolbar ---------------------------------------------------------------------------------

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun RemoteToolbar(
    inputReady: Boolean,
    mediaReady: Boolean,
    controlsReady: Boolean,
    voiceReady: Boolean,
    onOpen: (RemoteSheet) -> Unit,
    onMirror: () -> Unit,
    modifier: Modifier = Modifier,
) {
    HorizontalFloatingToolbar(
        expanded = true,
        modifier = modifier,
        floatingActionButton = {
            FloatingToolbarDefaults.VibrantFloatingActionButton(onClick = onMirror) {
                Icon(LincSymbols.ScreenShare, contentDescription = "Mirror PC")
            }
        },
    ) {
        // Blocked items are disabled, not removed (M4b's rule); the pad or the line above the
        // toolbar says why.
        ToolbarItem(LincSymbols.Keyboard, "Keyboard", inputReady) { onOpen(RemoteSheet.Keyboard) }
        ToolbarItem(LincSymbols.MusicNote, "Media", mediaReady) { onOpen(RemoteSheet.Media) }
        ToolbarItem(LincSymbols.Slideshow, "Present", inputReady) { onOpen(RemoteSheet.Present) }
        ToolbarItem(LincSymbols.KeyboardVoice, "Voice", voiceReady) { onOpen(RemoteSheet.Voice) }
        ToolbarItem(LincSymbols.PowerOff, "Power", controlsReady) { onOpen(RemoteSheet.Power) }
    }
}

@Composable
private fun ToolbarItem(icon: ImageVector, label: String, enabled: Boolean, onClick: () -> Unit) {
    IconButton(onClick = onClick, enabled = enabled, modifier = Modifier.size(48.dp)) {
        Icon(icon, contentDescription = label)
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun RemoteSheetHost(sheet: RemoteSheet, onDismiss: () -> Unit, content: @Composable () -> Unit) {
    val state = rememberModalBottomSheetState(skipPartiallyExpanded = sheet != RemoteSheet.Keyboard)
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = state) {
        Column(
            Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(bottom = 24.dp),
            verticalArrangement = Arrangement.spacedBy(Kit.groupGap),
        ) { content() }
    }
}

@Composable
private fun SheetTitle(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.headlineSmall,
        modifier = Modifier.padding(horizontal = Kit.gutter),
    )
}

/** The reason a sheet's controls are disabled, in an error-free tone (it isn't an error). */
@Composable
private fun SheetBlocked(reason: String?) {
    if (reason == null) return
    HelperText(reason, kind = Explanations.Kind.Blocked, modifier = Modifier.padding(horizontal = Kit.gutter))
}

// ---- Keyboard sheet --------------------------------------------------------------------------

@OptIn(ExperimentalMaterial3ExpressiveApi::class, ExperimentalLayoutApi::class)
@Composable
private fun KeyboardSheet(blockedReason: String?) {
    val ready = blockedReason == null
    val context = LocalContext.current
    val view = LocalView.current
    val latches by ModifierKeys.latches.collectAsState()
    var sink by remember { mutableStateOf<EditText?>(null) }
    val dictation = rememberDictation()

    SheetTitle("Keyboard")
    SheetBlocked(blockedReason)
    HelperText(
        "Type with your phone's keyboard. Ctrl, Alt, Shift and Win apply to the next key; tap twice to keep them on.",
        modifier = Modifier.padding(horizontal = Kit.gutter),
    )
    // The invisible IME sink, wired exactly like Mirror PC's — one keyboard path, not two.
    AndroidView(
        modifier = Modifier.size(1.dp).alpha(0f),
        factory = { ctx ->
            EditText(ctx).apply {
                isFocusable = true
                isFocusableInTouchMode = true
                KeyboardSink.wire(this)
                sink = this
            }
        },
    )
    LaunchedEffect(sink, ready) {
        val target = sink ?: return@LaunchedEffect
        if (!ready) return@LaunchedEffect
        kotlinx.coroutines.delay(250) // let the sheet settle before raising the IME
        target.requestFocus()
        context.getSystemService(InputMethodManager::class.java).showSoftInput(target, InputMethodManager.SHOW_IMPLICIT)
    }
    DisposableEffect(Unit) { onDispose { ModifierKeys.clear() } }

    Row(Modifier.fillMaxWidth().padding(horizontal = Kit.gutter), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        listOf(ModifierKeys.VK_CONTROL to "Ctrl", ModifierKeys.VK_MENU to "Alt", ModifierKeys.VK_SHIFT to "Shift", ModifierKeys.VK_LWIN to "Win")
            .forEach { (vk, label) ->
                val latch = latches[vk] ?: ModifierKeys.Latch.OFF
                ToggleButton(
                    checked = latch != ModifierKeys.Latch.OFF,
                    onCheckedChange = {
                        view.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                        ModifierKeys.cycle(vk)
                    },
                    enabled = ready,
                    modifier = Modifier.weight(1f).heightIn(min = Kit.touch)
                        .semantics { contentDescription = "$label, ${latch.name.lowercase()}" },
                ) { Text(if (latch == ModifierKeys.Latch.LOCKED) "$label•" else label, maxLines = 1) }
            }
    }
    FlowRow(
        Modifier.fillMaxWidth().padding(horizontal = Kit.gutter),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        KeyChip("Esc", null, ready) { ModifierKeys.chord(0x1B, MirrorControl::key) }
        KeyChip("Tab", LincSymbols.TabKey, ready) { ModifierKeys.chord(0x09, MirrorControl::key) }
        KeyChip("Left", LincSymbols.ArrowLeft, ready) { ModifierKeys.chord(0x25, MirrorControl::key) }
        KeyChip("Up", LincSymbols.ArrowUp, ready) { ModifierKeys.chord(0x26, MirrorControl::key) }
        KeyChip("Down", LincSymbols.ArrowDown, ready) { ModifierKeys.chord(0x28, MirrorControl::key) }
        KeyChip("Right", LincSymbols.ArrowRight, ready) { ModifierKeys.chord(0x27, MirrorControl::key) }
        KeyChip("Backspace", LincSymbols.Backspace, ready) { ModifierKeys.chord(KeyboardMapping.VK_BACK, MirrorControl::key) }
        KeyChip("Enter", LincSymbols.Enter, ready) { ModifierKeys.chord(0x0D, MirrorControl::key) }
    }
    FilledTonalButton(
        onClick = { dictation.toggle() },
        enabled = ready,
        shapes = ButtonDefaults.shapes(),
        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter).heightIn(min = 56.dp),
    ) {
        Icon(LincSymbols.KeyboardVoice, contentDescription = null)
        Spacer(Modifier.width(8.dp))
        Text(if (dictation.listening) (dictation.partial.ifBlank { "Listening… tap to stop" }) else "Dictate — speak, and it's typed on the PC",
            maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
    dictation.error?.let { HelperText(it, kind = Explanations.Kind.Error, modifier = Modifier.padding(horizontal = Kit.gutter)) }
}

@Composable
private fun KeyChip(label: String, icon: ImageVector?, enabled: Boolean, onClick: () -> Unit) {
    val view = LocalView.current
    OutlinedButton(
        onClick = { view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP); onClick() },
        enabled = enabled,
        modifier = Modifier.heightIn(min = Kit.touch).semantics { contentDescription = label },
    ) {
        if (icon != null) Icon(icon, contentDescription = null) else Text(label)
    }
}

/** The phone's speech recogniser typing on the PC, as Mirror PC's keyboard 🎤 key does. */
private class Dictation(val toggle: () -> Unit, val listening: Boolean, val partial: String, val error: String?)

@Composable
private fun rememberDictation(): Dictation {
    val context = LocalContext.current
    var recognizer by remember { mutableStateOf<SpeechRecognizer?>(null) }
    var partial by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    fun stop() {
        recognizer?.let { runCatching { it.cancel() }; it.destroy() }
        recognizer = null
        partial = ""
    }
    fun start() {
        error = null
        if (!SpeechRecognizer.isRecognitionAvailable(context)) { error = "This phone has no speech recognition."; return }
        val sr = SpeechRecognizer.createSpeechRecognizer(context)
        recognizer = sr
        sr.setRecognitionListener(object : RecognitionListener {
            override fun onPartialResults(p: Bundle?) {
                partial = p?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull().orEmpty()
            }
            override fun onResults(r: Bundle?) {
                r?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull()
                    ?.takeIf { it.isNotBlank() }?.let { MirrorControl.text("$it ") }
                stop()
            }
            override fun onError(e: Int) {
                if (e != SpeechRecognizer.ERROR_NO_MATCH && e != SpeechRecognizer.ERROR_SPEECH_TIMEOUT) error = "Dictation stopped."
                stop()
            }
            override fun onReadyForSpeech(params: Bundle?) = Unit
            override fun onBeginningOfSpeech() = Unit
            override fun onRmsChanged(rmsdB: Float) = Unit
            override fun onBufferReceived(buffer: ByteArray?) = Unit
            override fun onEndOfSpeech() = Unit
            override fun onEvent(eventType: Int, params: Bundle?) = Unit
        })
        sr.startListening(Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).apply {
            putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
            putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true)
        })
    }
    val askMic = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) start() else error = "Dictation needs the microphone."
    }
    DisposableEffect(Unit) { onDispose { stop() } }
    return Dictation(
        toggle = {
            when {
                recognizer != null -> stop()
                !PcVoiceAccess.hasMicPermission(context) -> askMic.launch(Manifest.permission.RECORD_AUDIO)
                else -> start()
            }
        },
        listening = recognizer != null,
        partial = partial,
        error = error,
    )
}

// ---- Media sheet -----------------------------------------------------------------------------

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun MediaSheet(transportReason: String?, volumeReason: String?) {
    val media by PcMediaStore.state.collectAsState()
    val pcState by PcControl.state.collectAsState()
    // pc.media.state with nothing present: the PC has no player to route a press to, so the
    // transport is disabled with a reason rather than silently doing nothing (M4c).
    val playing = media?.present == true
    val canTransport = transportReason == null && playing
    val volumeReady = volumeReason == null

    SheetTitle("Media")
    // Now playing: one card, the PC's track and its controls together.
    androidx.compose.material3.Surface(
        shape = RoundedCornerShape(28.dp),
        color = MaterialTheme.colorScheme.surfaceContainerHigh,
        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter),
    ) {
        Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                Box(
                    Modifier.size(72.dp).clip(MaterialShapes.Cookie12Sided.toShape()).background(MaterialTheme.colorScheme.tertiaryContainer),
                    contentAlignment = Alignment.Center,
                ) { Icon(LincSymbols.MusicNote, contentDescription = null, tint = MaterialTheme.colorScheme.onTertiaryContainer, modifier = Modifier.size(32.dp)) }
                Column(Modifier.weight(1f)) {
                    Text(nowPlayingTitle(media), style = MaterialTheme.typography.titleLarge, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    val sub = listOfNotNull(media?.artist?.takeIf { it.isNotBlank() && playing }, media?.app?.takeIf { it.isNotBlank() && playing })
                    if (sub.isNotEmpty()) Text(sub.joinToString(" · "), style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            }
            val m = media
            if (playing && m != null && m.durationMs > 0) {
                androidx.compose.material3.LinearWavyProgressIndicator(
                    progress = { (m.positionMs.toFloat() / m.durationMs).coerceIn(0f, 1f) },
                    modifier = Modifier.fillMaxWidth(),
                )
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceEvenly, verticalAlignment = Alignment.CenterVertically) {
                IconButton(onClick = { PcMediaControl.send("prev") }, enabled = canTransport, modifier = Modifier.size(56.dp)) {
                    Icon(LincSymbols.SkipPrevious, contentDescription = "Previous", modifier = Modifier.size(32.dp))
                }
                FilledIconButton(
                    onClick = { PcMediaControl.send(if (media?.playing == true) "pause" else "play") },
                    enabled = canTransport,
                    modifier = Modifier.size(72.dp),
                    shape = MaterialShapes.Cookie9Sided.toShape(),
                ) {
                    Icon(if (media?.playing == true) LincSymbols.Pause else LincSymbols.Play,
                        contentDescription = if (media?.playing == true) "Pause" else "Play", modifier = Modifier.size(32.dp))
                }
                IconButton(onClick = { PcMediaControl.send("next") }, enabled = canTransport, modifier = Modifier.size(56.dp)) {
                    Icon(LincSymbols.SkipNext, contentDescription = "Next", modifier = Modifier.size(32.dp))
                }
            }
        }
    }
    SheetBlocked(transportReason)

    // The PC's volume. With Play everywhere on it is the master for every speaker at once.
    SheetBlocked(volumeReason)
    var volume by remember(pcState?.volume) { mutableFloatStateOf((pcState?.volume ?: 50).toFloat()) }
    Row(Modifier.fillMaxWidth().padding(horizontal = Kit.gutter), verticalAlignment = Alignment.CenterVertically) {
        IconToggleButton(
            checked = pcState?.muted == true,
            onCheckedChange = { PcControl.volumeMute() },
            enabled = volumeReady,
        ) {
            Icon(if (pcState?.muted == true) LincSymbols.VolumeOff else LincSymbols.VolumeUp,
                contentDescription = if (pcState?.muted == true) "Unmute the PC" else "Mute the PC")
        }
        Slider(
            value = volume,
            onValueChange = { volume = it },
            onValueChangeFinished = { PcControl.volumeSet(volume.toInt()) },
            valueRange = 0f..100f,
            enabled = volumeReady,
            modifier = Modifier.weight(1f).semantics { contentDescription = "PC volume" },
        )
        Text("${volume.toInt()}", style = MaterialTheme.typography.labelLarge, modifier = Modifier.width(36.dp))
    }
    PlayEverywhereSummary()
}

private fun nowPlayingTitle(media: PcMedia?): String = when {
    media == null -> "Waiting to hear what's playing"
    !media.present -> "Nothing is playing on the PC"
    else -> media.title?.takeIf { it.isNotBlank() } ?: "Something is playing"
}

// ---- Present sheet ---------------------------------------------------------------------------

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun PresentSheet(blockedReason: String?) {
    val ready = blockedReason == null
    val view = LocalView.current
    fun send(action: PresentationKeys.Action) {
        view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
        PresentationControl.send(action)
    }
    SheetTitle("Present")
    SheetBlocked(blockedReason)
    HelperText(
        "These press keys on whatever has focus on the PC — there's no slideshow app detection.",
        modifier = Modifier.padding(horizontal = Kit.gutter),
    )
    Row(Modifier.fillMaxWidth().padding(horizontal = Kit.gutter).height(96.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        OutlinedButton(onClick = { send(PresentationKeys.Action.Previous) }, enabled = ready, shapes = ButtonDefaults.shapes(),
            modifier = Modifier.weight(1f).fillMaxHeight()) { Text("Previous") }
        Button(onClick = { send(PresentationKeys.Action.Next) }, enabled = ready, shapes = ButtonDefaults.shapes(),
            modifier = Modifier.weight(2f).fillMaxHeight()) { Text("Next", style = MaterialTheme.typography.titleLarge) }
    }
    Row(Modifier.fillMaxWidth().padding(horizontal = Kit.gutter), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        FilledTonalButton(onClick = { send(PresentationKeys.Action.Start) }, enabled = ready, shapes = ButtonDefaults.shapes(),
            modifier = Modifier.weight(1f).heightIn(min = 56.dp)) { Text("Start") }
        FilledTonalButton(onClick = { send(PresentationKeys.Action.End) }, enabled = ready, shapes = ButtonDefaults.shapes(),
            modifier = Modifier.weight(1f).heightIn(min = 56.dp)) { Text("End") }
        FilledTonalButton(onClick = { send(PresentationKeys.Action.Black) }, enabled = ready, shapes = ButtonDefaults.shapes(),
            modifier = Modifier.weight(1f).heightIn(min = 56.dp)) { Text("Black") }
    }
}

// ---- Voice sheet -----------------------------------------------------------------------------

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
internal fun VoiceSheet() {
    val context = LocalContext.current
    val connection by CompanionStateHolder.state.collectAsState()
    val state by PcVoiceAccess.state.collectAsState()
    val micLive by PcVoiceAccess.micLive.collectAsState()
    var phoneMic by remember { mutableStateOf(MirrorSettings.voicePhoneMic(context)) }
    /** What to do once the user has agreed to (or declined) setting the PC's microphone up. */
    var pendingSetup by remember { mutableStateOf<String?>(null) } // "voice" | "tool"
    LaunchedEffect(connection) { PcVoiceAccess.refresh(context) }

    fun startVoice() {
        if (phoneMic && PcVoiceAccess.state.value.canSetUp) pendingSetup = "voice"
        else PcVoiceAccess.set(context, true, phoneMic)
    }
    fun startTool() {
        if (PcVoiceAccess.state.value.canSetUp) pendingSetup = "tool" else PcVoiceAccess.setTool(context, true)
    }
    var afterMic by remember { mutableStateOf<(() -> Unit)?>(null) }
    val askMic = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) afterMic?.invoke()
        afterMic = null
    }
    fun withMic(action: () -> Unit) {
        if (PcVoiceAccess.hasMicPermission(context)) action() else { afterMic = action; askMic.launch(Manifest.permission.RECORD_AUDIO) }
    }

    val voiceReady = PcVoiceAccess.ready()
    val micReady = PcVoiceAccess.micReady()
    SheetTitle("Voice")
    if (!voiceReady) {
        SheetBlocked("Update Linc on the PC to use Voice Access and the microphone from here.")
    } else if (!state.available) {
        SheetBlocked("This PC doesn't have Windows Voice Access (Windows 11 22H2 or later). The microphone below still works.")
    }

    val p = state.progress
    if (state.settingUp || (p != null && p < 100)) {
        if (p != null && p < 100) ProgressLine(p / 100f, "Downloading the microphone for the PC", Modifier.padding(horizontal = Kit.gutter))
        else HelperText(state.setupText, kind = Explanations.Kind.Warning, modifier = Modifier.padding(horizontal = Kit.gutter))
    }

    pendingSetup?.let { which ->
        Surface(
            color = MaterialTheme.colorScheme.secondaryContainer,
            shape = RoundedCornerShape(Kit.groupOuter),
            modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter),
        ) {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("Set up the microphone on the PC?", style = MaterialTheme.typography.titleMedium)
                Text(PcVoiceAccess.SETUP_EXPLANATION, style = MaterialTheme.typography.bodyMedium)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(onClick = {
                        pendingSetup = null
                        if (which == "voice") PcVoiceAccess.set(context, true, phoneMic = true, installCable = true)
                        else PcVoiceAccess.setTool(context, true, installCable = true)
                    }, shapes = ButtonDefaults.shapes()) { Text("Set up") }
                    TextButton(onClick = {
                        pendingSetup = null
                        if (which == "voice") PcVoiceAccess.set(context, true, phoneMic = false)
                    }) { Text(if (which == "voice") "Use the PC's mic" else "Not now") }
                }
            }
        }
    }

    SettingsGroup(rows = listOf(
        { seg ->
            SettingsRow(
                title = "Voice Access",
                segment = seg,
                icon = LincSymbols.VoiceAccess,
                supporting = when {
                    state.on && micLive -> "On — the PC is listening through this phone. Try \"open Edge\" or \"show numbers\"."
                    state.on -> "On — the PC is listening through its own microphone."
                    else -> "Control the PC by voice: \"click Start\", \"scroll down\", \"type hello\"."
                },
                enabled = voiceReady && state.available && !state.settingUp,
                trailing = {
                    Switch(
                        checked = state.on,
                        enabled = voiceReady && state.available && !state.settingUp,
                        onCheckedChange = { on ->
                            if (!on) PcVoiceAccess.set(context, false, phoneMic)
                            else if (phoneMic) withMic { startVoice() } else startVoice()
                        },
                    )
                },
            )
        },
        { seg ->
            SettingsRow(
                title = "Use this phone as the PC's microphone",
                segment = seg,
                icon = LincSymbols.Mic,
                supporting = if (state.tool && micLive) "On — every app on the PC hears this phone: calls, dictation (Win+H), Voice Access."
                else "For any app on the PC, not just Voice Access.",
                enabled = micReady && !state.settingUp,
                trailing = {
                    Switch(
                        checked = state.tool,
                        enabled = micReady && !state.settingUp,
                        onCheckedChange = { on -> if (on) withMic { startTool() } else PcVoiceAccess.setTool(context, false) },
                    )
                },
            )
        },
    ))
    SectionHeader("Which microphone the PC hears")
    ConnectedChoice(
        options = listOf(true to "Phone", false to "PC"),
        selected = phoneMic,
        onSelect = { usePhone ->
            phoneMic = usePhone
            MirrorSettings.setVoicePhoneMic(context, usePhone)
            // Running already: switch its microphone over now (D-075).
            if (state.on) {
                if (usePhone) withMic { startVoice() } else PcVoiceAccess.set(context, true, phoneMic = false)
            }
        },
        modifier = Modifier.padding(horizontal = Kit.gutter),
    )
    HelperText(
        "With Phone chosen, Voice Access hears this phone, and in Mirror PC any app that starts listening hears it too.",
        modifier = Modifier.padding(horizontal = Kit.gutter),
    )
    state.error?.let { HelperText(it, kind = Explanations.Kind.Error, modifier = Modifier.padding(horizontal = Kit.gutter)) }
}

// ---- Power sheet -----------------------------------------------------------------------------

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun PowerSheet(blockedReason: String?) {
    val ready = blockedReason == null
    val state by PcControl.state.collectAsState()
    var confirm by remember { mutableStateOf<String?>(null) } // "shutdown" | "restart"
    // Capability flags arrive with the pc.state reply; until then don't disable on a guess.
    val canSleep = state?.canSleep ?: true
    val canShutdown = state?.canShutdown ?: true
    val canBrightness = state?.canBrightness ?: true

    SheetTitle("Power")
    SheetBlocked(blockedReason)
    TileGrid(tiles = listOf(
        { m -> ToolTile("Lock", LincSymbols.Lock, m, enabled = ready, onClick = { PcControl.lock() }) },
        { m -> ToolTile("Sleep", LincSymbols.Bedtime, m, enabled = ready && canSleep,
            state = if (!canSleep) "This PC can't sleep" else null, onClick = { PcControl.sleep() }) },
        { m -> ToolTile("Restart", LincSymbols.Restart, m, enabled = ready && canShutdown, onClick = { confirm = "restart" }) },
        { m -> ToolTile("Shut down", LincSymbols.PowerOff, m, enabled = ready && canShutdown, onClick = { confirm = "shutdown" }) },
    ))
    SectionHeader("Screen brightness")
    var level by remember(state?.brightness) { mutableFloatStateOf((state?.brightness ?: 50).toFloat()) }
    Row(Modifier.fillMaxWidth().padding(horizontal = Kit.gutter), verticalAlignment = Alignment.CenterVertically) {
        Icon(LincSymbols.Brightness, contentDescription = null, modifier = Modifier.padding(end = 12.dp))
        Slider(
            value = level,
            onValueChange = { level = it },
            onValueChangeFinished = { PcControl.brightnessSet(level.toInt()) },
            valueRange = 0f..100f,
            enabled = ready && canBrightness,
            modifier = Modifier.weight(1f).semantics { contentDescription = "PC screen brightness" },
        )
        Text(state?.brightness?.let { "${level.toInt()}" } ?: "—", style = MaterialTheme.typography.labelLarge, modifier = Modifier.width(36.dp))
    }
    if (ready && !canBrightness) {
        HelperText("This PC's screen brightness can't be controlled remotely.", kind = Explanations.Kind.Blocked,
            modifier = Modifier.padding(horizontal = Kit.gutter))
    }

    confirm?.let { action ->
        val restart = action == "restart"
        AlertDialog(
            onDismissRequest = { confirm = null },
            icon = { Icon(if (restart) LincSymbols.Restart else LincSymbols.PowerOff, contentDescription = null) },
            title = { Text(if (restart) "Restart the PC?" else "Shut down the PC?") },
            text = { Text("Save your work on the PC first — it will ${if (restart) "restart" else "shut down"} now.") },
            confirmButton = {
                TextButton(onClick = {
                    if (restart) PcControl.restart() else PcControl.shutdown()
                    confirm = null
                }) { Text(if (restart) "Restart" else "Shut down") }
            },
            dismissButton = { TextButton(onClick = { confirm = null }) { Text("Cancel") } },
        )
    }
}

private const val KEY_HINT_GESTURES = "remote_hint_gestures"
private const val HINT_GESTURES = 3

// ---- previews --------------------------------------------------------------------------------

@Preview(name = "Remote — light", widthDp = 360, heightDp = 760)
@Composable
private fun RemotePreviewLight() = LincTheme(darkTheme = false) { RemoteScreen(onSettings = {}, onMirror = {}) }

@Preview(name = "Remote — dark", widthDp = 411, heightDp = 860)
@Composable
private fun RemotePreviewDark() = LincTheme(darkTheme = true) { RemoteScreen(onSettings = {}, onMirror = {}) }

@Preview(name = "Remote — large font", widthDp = 360, heightDp = 760, fontScale = 2f)
@Composable
private fun RemotePreviewLargeFont() = LincTheme { RemoteScreen(onSettings = {}, onMirror = {}) }

@Preview(name = "Remote — blocked", widthDp = 360, heightDp = 760)
@Composable
private fun RemotePreviewBlocked() = LincTheme {
    // Previews have no companion service, so the screen shows its blocked state.
    BlockedState(title = "Trackpad unavailable", reason = inputReason(ServiceState.Listening) ?: "", icon = LincSymbols.Mouse)
}
