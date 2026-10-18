package app.linc.android.ui

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleIn
import androidx.compose.animation.scaleOut
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.ButtonGroupDefaults
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.material3.MaterialShapes
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.ToggleButton
import androidx.compose.material3.ToggleButtonDefaults
import androidx.compose.material3.TonalToggleButton
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.toShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import app.linc.android.service.LinkStats
import app.linc.android.ui.theme.SeedDarkColorScheme
import app.linc.android.ui.theme.dynamicColorAvailable
import kotlin.math.roundToInt

/**
 * Everything PC Remote shows on screen besides the PC itself (D-069): MirrorActivity writes
 * into it, the overlay draws it. Plain Compose state, so the overlay recomposes on change.
 */
class RemoteUiState {
    var visible by mutableStateOf(false)
    var panelOpen by mutableStateOf(false)
    var quality by mutableStateOf(LinkStats.Quality.GOOD)
    var stalled by mutableStateOf(false)
    var pingMs by mutableIntStateOf(-1)
    var fps by mutableIntStateOf(0)
    var mbps by mutableFloatStateOf(0f)
    var picture by mutableStateOf("")
    var mouseMode by mutableStateOf(false)
    var floatingKeyboard by mutableStateOf(false)
    var phoneKeyboard by mutableStateOf(false)
    var autoKeyboard by mutableStateOf(true)
    var soundAvailable by mutableStateOf(false)
    var soundOn by mutableStateOf(true)
    var voiceAccessAvailable by mutableStateOf(false)
    var voiceAccessOn by mutableStateOf(false)
    var tabletAvailable by mutableStateOf(false)
    /** Which microphone the PC hears in PC Remote: this phone's, or its own (D-075). */
    var phoneMic by mutableStateOf(true)
    var micLive by mutableStateOf(false)
    var tabletOn by mutableStateOf(false)
    var displays by mutableIntStateOf(1)
    var zoomed by mutableStateOf(false)
    /** Bubble position: which edge, and how far down (0..1). */
    var bubbleRight by mutableStateOf(true)
    var bubbleY by mutableFloatStateOf(0.1f)
}

interface RemoteActions {
    fun setMouseMode(mouse: Boolean)
    fun floatingKeyboard()
    fun phoneKeyboard()
    fun setAutoKeyboard(on: Boolean)
    fun sound()
    fun voiceAccess()
    fun tabletMode()
    fun setPhoneMic(phone: Boolean)
    fun display()
    fun quality()
    fun fit()
    fun taskView()
    fun showDesktop()
    fun end()
    fun bubbleMoved(right: Boolean, y: Float)
}

private val Good = Color(0xFF7BDA8F)
private val Fair = Color(0xFFF5C451)
private val Poor = Color(0xFFFF8A80)

/**
 * The PC Remote status bubble and its panel, in Material 3 Expressive. The bubble is a
 * nine-sided cookie (an Expressive shape) whose icon is the connection — full, half or broken
 * Wi-Fi in green, amber or red — and it turns as it opens. Drag it anywhere; it springs to the
 * nearer edge. The panel opens beside it: link stats, then connected button groups for the
 * pointer and keyboard, then tonal toggle buttons for everything else.
 *
 * The overlay covers the screen but only its bubble and panel take touches, so everything
 * else falls through to the PC picture underneath.
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun PcRemoteOverlay(state: RemoteUiState, actions: RemoteActions) {
    // Always dark — it floats over the PC picture — but in the phone's own Material You colours,
    // like the rest of the app (the plain darkColorScheme() it used ignored the wallpaper).
    val context = LocalContext.current
    val scheme = remember(context) {
        if (dynamicColorAvailable) runCatching { dynamicDarkColorScheme(context) }.getOrDefault(SeedDarkColorScheme)
        else SeedDarkColorScheme
    }
    MaterialExpressiveTheme(colorScheme = scheme) {
        if (!state.visible) return@MaterialExpressiveTheme
        BoxWithConstraints(Modifier.fillMaxSize()) {
            val density = LocalDensity.current
            val bubble = 56.dp
            val margin = 12.dp
            val maxXpx = with(density) { (maxWidth - bubble - margin).toPx() }
            val minXpx = with(density) { margin.toPx() }
            val maxYpx = with(density) { (maxHeight - bubble).toPx() }.coerceAtLeast(1f)

            var dragging by remember { mutableStateOf(false) }
            var dragX by remember { mutableFloatStateOf(0f) }
            var dragY by remember { mutableFloatStateOf(0f) }
            val restX = if (state.bubbleRight) maxXpx else minXpx
            val restY = state.bubbleY * maxYpx
            val x by animateFloatAsState(if (dragging) dragX else restX,
                if (dragging) tween(0) else MaterialTheme.motionScheme.defaultSpatialSpec(), label = "bubbleX")
            val y by animateFloatAsState(if (dragging) dragY else restY,
                if (dragging) tween(0) else MaterialTheme.motionScheme.defaultSpatialSpec(), label = "bubbleY")
            val spin by animateFloatAsState(if (state.panelOpen) 90f else 0f,
                MaterialTheme.motionScheme.defaultSpatialSpec(), label = "bubbleSpin")

            val (icon, tint) = when {
                state.stalled || state.quality == LinkStats.Quality.POOR -> LincSymbols.WifiBad to Poor
                state.quality == LinkStats.Quality.FAIR -> LincSymbols.WifiFair to Fair
                else -> LincSymbols.WifiGood to Good
            }

            // ---- the panel -------------------------------------------------------------------
            var panelHeight by remember { mutableIntStateOf(0) }
            val panelWidth = 372.dp
            val panelX = with(density) {
                if (state.bubbleRight) restX - (panelWidth + 10.dp).toPx() else restX + (bubble + 10.dp).toPx()
            }.coerceAtLeast(0f)
            val panelY = restY.coerceIn(0f, (with(density) { maxHeight.toPx() } - panelHeight).coerceAtLeast(0f))
            AnimatedVisibility(
                visible = state.panelOpen,
                enter = scaleIn(MaterialTheme.motionScheme.fastSpatialSpec(), 0.85f,
                    TransformOrigin(if (state.bubbleRight) 1f else 0f, 0f)) + fadeIn(),
                exit = scaleOut(targetScale = 0.9f) + fadeOut(),
                modifier = Modifier
                    .offset { IntOffset(panelX.roundToInt(), panelY.roundToInt()) }
                    .width(panelWidth)
                    .heightIn(max = maxHeight)
                    .onSizeChanged { panelHeight = it.height },
            ) {
                Panel(state, actions, icon, tint)
            }

            // ---- the bubble ------------------------------------------------------------------
            // The shadow is a circle under the cookie, never the cookie itself: an elevation
            // shadow on a concave outline sends Skia down its concave tessellator, which on a
            // Pixel 7 spent whole seconds per frame on the render thread — the main thread
            // blocked behind it, input timed out, and Android killed Linc as "not responding"
            // the moment the bubble was tapped (and it was the steady lag before that too).
            val cookie = MaterialShapes.Cookie9Sided.toShape()
            Surface(
                shape = cookie,
                color = MaterialTheme.colorScheme.surfaceContainerHighest,
                modifier = Modifier
                    .offset { IntOffset(x.roundToInt(), y.roundToInt()) }
                    .size(bubble)
                    .shadow(8.dp, CircleShape)
                    .rotate(spin)
                    .pointerInput(Unit) {
                        detectDragGestures(
                            onDragStart = { dragging = true; dragX = x; dragY = y },
                            onDragEnd = {
                                dragging = false
                                val right = dragX + size.width / 2f > (maxXpx + minXpx + size.width) / 2f
                                actions.bubbleMoved(right, (dragY / maxYpx).coerceIn(0f, 1f))
                            },
                            onDragCancel = { dragging = false },
                        ) { change, delta ->
                            change.consume()
                            dragX = (dragX + delta.x).coerceIn(0f, maxXpx + margin.toPx())
                            dragY = (dragY + delta.y).coerceIn(0f, maxYpx)
                        }
                    }
                    .clickable { state.panelOpen = !state.panelOpen },
            ) {
                Box(contentAlignment = Alignment.Center) {
                    Icon(
                        if (state.panelOpen) LincSymbols.Close else icon,
                        contentDescription = if (state.panelOpen) "Close Tools menu" else "Tools menu",
                        tint = if (state.panelOpen) MaterialTheme.colorScheme.onSurface else tint,
                        modifier = Modifier.size(26.dp).rotate(-spin),
                    )
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class, ExperimentalLayoutApi::class)
@Composable
private fun Panel(state: RemoteUiState, actions: RemoteActions, icon: ImageVector, tint: Color) {
    Surface(
        shape = RoundedCornerShape(32.dp),
        color = MaterialTheme.colorScheme.surfaceContainer,
        tonalElevation = 6.dp,
        shadowElevation = 12.dp,
    ) {
        Column(
            Modifier.verticalScroll(rememberScrollState()).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            // Header: what the link is like, in words and colour.
            Row(verticalAlignment = Alignment.CenterVertically) {
                Surface(shape = MaterialShapes.Cookie12Sided.toShape(), color = tint.copy(alpha = 0.18f),
                    modifier = Modifier.size(40.dp)) {
                    Box(contentAlignment = Alignment.Center) { Icon(icon, null, tint = tint, modifier = Modifier.size(22.dp)) }
                }
                Spacer(Modifier.width(12.dp))
                Column(Modifier.weight(1f)) {
                    Text("Tools", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                    Text(
                        if (state.stalled) "Picture stalled — recovering" else "${state.quality.label} connection",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                IconButton(onClick = { state.panelOpen = false }) { Icon(LincSymbols.Close, "Close") }
            }

            // Stats.
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Stat(if (state.pingMs >= 0) "${state.pingMs}" else "—", "ms ping", Modifier.weight(1f))
                Stat("${state.fps}", "fps", Modifier.weight(1f))
                Stat(String.format("%.1f", state.mbps), "Mb/s", Modifier.weight(1f))
                Stat(state.picture, "picture", Modifier.weight(1f))
            }

            // Pointer: a connected button group, one choice.
            Section("Pointer")
            Row(horizontalArrangement = Arrangement.spacedBy(ButtonGroupDefaults.ConnectedSpaceBetween)) {
                ToggleButton(
                    checked = !state.mouseMode, onCheckedChange = { actions.setMouseMode(false) },
                    shapes = ButtonGroupDefaults.connectedLeadingButtonShapes(), modifier = Modifier.weight(1f),
                ) { IconLabel(LincSymbols.TouchApp, "Touch") }
                ToggleButton(
                    checked = state.mouseMode, onCheckedChange = { actions.setMouseMode(true) },
                    shapes = ButtonGroupDefaults.connectedTrailingButtonShapes(), modifier = Modifier.weight(1f),
                ) { IconLabel(LincSymbols.Mouse, "Mouse") }
            }

            // Keyboard: which one, and whether it pops up by itself.
            Section("Keyboard")
            Row(horizontalArrangement = Arrangement.spacedBy(ButtonGroupDefaults.ConnectedSpaceBetween)) {
                ToggleButton(
                    checked = state.floatingKeyboard, onCheckedChange = { actions.floatingKeyboard() },
                    shapes = ButtonGroupDefaults.connectedLeadingButtonShapes(), modifier = Modifier.weight(1f),
                ) { IconLabel(LincSymbols.KeyboardAlt, "Floating") }
                ToggleButton(
                    checked = state.phoneKeyboard, onCheckedChange = { actions.phoneKeyboard() },
                    shapes = ButtonGroupDefaults.connectedTrailingButtonShapes(), modifier = Modifier.weight(1f),
                ) { IconLabel(LincSymbols.Smartphone, "Phone") }
            }
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("Pop up on text fields", style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f))
                Switch(checked = state.autoKeyboard, onCheckedChange = actions::setAutoKeyboard)
            }

            // Microphone: which one the PC hears — Voice Access, calls, dictation — while in here.
            Section(if (state.micLive) "Microphone · phone is live" else "Microphone")
            Row(horizontalArrangement = Arrangement.spacedBy(ButtonGroupDefaults.ConnectedSpaceBetween)) {
                ToggleButton(
                    checked = state.phoneMic, onCheckedChange = { actions.setPhoneMic(true) },
                    shapes = ButtonGroupDefaults.connectedLeadingButtonShapes(), modifier = Modifier.weight(1f),
                ) { IconLabel(LincSymbols.Mic, "Phone") }
                ToggleButton(
                    checked = !state.phoneMic, onCheckedChange = { actions.setPhoneMic(false) },
                    shapes = ButtonGroupDefaults.connectedTrailingButtonShapes(), modifier = Modifier.weight(1f),
                ) { IconLabel(LincSymbols.Display, "PC") }
            }

            // Everything else.
            Section("PC")
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                // Tablet mode first and every button keyed, so nothing moves under a finger when
                // another one comes or goes.
                key("tablet") { if (state.tabletAvailable) Toggle(state.tabletOn, LincSymbols.Tablet, "Tablet mode") { actions.tabletMode() } }
                key("sound") {
                    if (state.soundAvailable) Toggle(state.soundOn, if (state.soundOn) LincSymbols.VolumeUp else LincSymbols.VolumeOff,
                        if (state.soundOn) "Sound" else "Muted") { actions.sound() }
                }
                key("voice") { if (state.voiceAccessAvailable) Toggle(state.voiceAccessOn, LincSymbols.VoiceAccess, "Voice Access") { actions.voiceAccess() } }
                if (state.displays > 1) Toggle(false, LincSymbols.Display, "Display") { actions.display() }
                Toggle(false, LincSymbols.Tune, state.picture.ifEmpty { "Quality" }) { actions.quality() }
                if (state.zoomed) Toggle(false, LincSymbols.FitScreen, "Fit") { actions.fit() }
                Toggle(false, LincSymbols.TaskView, "Task view") { actions.taskView() }
                Toggle(false, LincSymbols.Home, "Desktop") { actions.showDesktop() }
            }

            FilledTonalButton(
                onClick = actions::end,
                modifier = Modifier.fillMaxWidth().height(48.dp),
                colors = ButtonDefaults.filledTonalButtonColors(
                    containerColor = MaterialTheme.colorScheme.errorContainer,
                    contentColor = MaterialTheme.colorScheme.onErrorContainer,
                ),
            ) { IconLabel(LincSymbols.Power, "End session") }
        }
    }
}

@Composable
private fun Stat(value: String, caption: String, modifier: Modifier) {
    Surface(shape = RoundedCornerShape(18.dp), color = MaterialTheme.colorScheme.surfaceContainerHighest, modifier = modifier) {
        Column(Modifier.padding(vertical = 10.dp), horizontalAlignment = Alignment.CenterHorizontally) {
            Text(value, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold, maxLines = 1)
            Text(caption, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
private fun Section(title: String) = Text(
    title, style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary,
)

@Composable
private fun IconLabel(icon: ImageVector, label: String) {
    Icon(icon, null, Modifier.size(ButtonDefaults.IconSize))
    Spacer(Modifier.size(ButtonDefaults.IconSpacing))
    Text(label, maxLines = 1)
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun Toggle(checked: Boolean, icon: ImageVector, label: String, onClick: () -> Unit) {
    TonalToggleButton(checked = checked, onCheckedChange = { onClick() }, shapes = ToggleButtonDefaults.shapes()) {
        IconLabel(icon, label)
    }
}
