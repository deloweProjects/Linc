package app.linc.android.ui

import android.view.inputmethod.InputMethodManager
import android.widget.EditText
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.wrapContentWidth
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.input.pointer.PointerInputScope
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import app.linc.android.ui.theme.Dimens
import app.linc.android.service.CompanionStateHolder
import app.linc.android.service.CompanionStateHolder.ServiceState
import app.linc.android.service.KeyboardSink
import app.linc.android.service.MirrorControl
import app.linc.android.service.PcControl
import app.linc.android.service.PcMedia
import app.linc.android.service.PcMediaControl
import app.linc.android.service.PcMediaStore
import app.linc.android.service.PresentationControl
import app.linc.android.service.PresentationKeys
import app.linc.android.service.TrackpadGestures

/**
 * The phone's Tools page (M4a): a full-screen remote keyboard + trackpad, reached from a Home
 * card rather than the bottom nav (M10's Share precedent — a destination that doesn't fit the
 * nav bar lives in a card instead). Visible but inert with no v14+ desktop connected; see
 * [connectionReason] for the plain-language reason shown in that state.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ToolsScreen(onBack: () -> Unit) {
    val connection by CompanionStateHolder.state.collectAsState()
    val reason = connectionReason(connection)
    val ready = reason == null

    // v17 quick controls (M4b): a separate gate from keyboard/trackpad's v14 one — a v14-only
    // desktop still gets a working keyboard/trackpad with the controls merely disabled.
    val pcState by PcControl.state.collectAsState()
    val controlsBlockedReason = controlsReason(connection)
    val controlsReady = controlsBlockedReason == null
    var confirmAction by remember { mutableStateOf<String?>(null) } // "shutdown" | "restart" | null

    // M4c: the PC's media state the Home widget already receives (v13 `pc.media.state`), read here
    // too. It is pushed unsolicited by the desktop on connect and on every change, so the Tools
    // page costs nothing to show it — no fetch was added.
    val pcMedia by PcMediaStore.state.collectAsState()
    val mediaBlockedReason = mediaReason(connection)
    val mediaReady = mediaBlockedReason == null

    // Request pc.state when Tools opens, and again on every connection change (covers a
    // reconnect that completes while this screen is already open) — PROTOCOL.md v17 has
    // deliberately no unsolicited push, so this is the only time the phone asks.
    LaunchedEffect(connection) { PcControl.requestState() }

    var keyboardTarget by remember { mutableStateOf<EditText?>(null) }
    var keyboardUp by remember { mutableStateOf(false) }

    val toolsScrollBehavior = TopAppBarDefaults.pinnedScrollBehavior()
    Scaffold(
        modifier = Modifier.fillMaxSize().nestedScroll(toolsScrollBehavior.nestedScrollConnection),
        containerColor = MaterialTheme.colorScheme.surface,
        topBar = {
            TopAppBar(
                title = { Text("Tools", style = MaterialTheme.typography.headlineSmall) },
                navigationIcon = {
                    IconButton(onClick = onBack, modifier = Modifier.size(Dimens.touchTarget)) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back")
                    }
                },
                actions = {
                    Button(
                        enabled = ready,
                        modifier = Modifier
                            .padding(end = Dimens.s)
                            .heightIn(min = Dimens.touchTarget),
                        onClick = {
                            val target = keyboardTarget ?: return@Button
                            val manager = target.context
                                .getSystemService(InputMethodManager::class.java)
                            keyboardUp = !keyboardUp
                            if (keyboardUp) {
                                target.requestFocus()
                                manager.showSoftInput(target, InputMethodManager.SHOW_IMPLICIT)
                            } else {
                                manager.hideSoftInputFromWindow(target.windowToken, 0)
                            }
                        },
                    ) { Text(if (keyboardUp) "Hide keyboard" else "Keyboard") }
                },
                scrollBehavior = toolsScrollBehavior,
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = MaterialTheme.colorScheme.surface,
                    // M18 A4: matches LincScreen — the title keeps its own surface once content
                    // scrolls beneath it, instead of the content being sliced under the bar.
                    scrolledContainerColor = MaterialTheme.colorScheme.surfaceContainer,
                ),
            )
        },
    ) { innerPadding ->
        // M16 Part B: a scroll host, the M5a rule. Before this the page was a fixed Column and
        // the trackpad was the only child with weight(1f), so every card M4c added came straight
        // out of the trackpad's height. Now the cards push the page taller and scroll.
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(innerPadding)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = Dimens.l),
        ) {
            Spacer(Modifier.height(Dimens.s))

            if (!ready) {
                Text(
                    reason ?: "",
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(bottom = Dimens.l),
                )
            }

            QuickControlsCard(
                ready = controlsReady,
                blockedReason = controlsBlockedReason,
                state = pcState,
                onLock = { PcControl.lock() },
                onSleep = { PcControl.sleep() },
                onShutdown = { confirmAction = "shutdown" },
                onRestart = { confirmAction = "restart" },
                onDimmer = { level -> PcControl.brightnessSet((level - 10).coerceAtLeast(0)) },
                onBrighter = { level -> PcControl.brightnessSet((level + 10).coerceAtMost(100)) },
            )

            MediaCard(
                transportReady = mediaReady,
                transportBlockedReason = mediaBlockedReason,
                volumeReady = controlsReady,
                volumeBlockedReason = controlsBlockedReason,
                media = pcMedia,
                state = pcState,
                onPrevious = { PcMediaControl.send("prev") },
                onPlayPause = { PcMediaControl.send(if (pcMedia?.playing == true) "pause" else "play") },
                onNext = { PcMediaControl.send("next") },
                onVolumeDown = { PcControl.volumeDown() },
                onVolumeUp = { PcControl.volumeUp() },
                onMuteToggle = { PcControl.volumeMute() },
            )

            PresentationCard(
                ready = ready,
                blockedReason = reason,
                onAction = { action -> PresentationControl.send(action) },
            )

            if (confirmAction != null) {
                val restart = confirmAction == "restart"
                AlertDialog(
                    onDismissRequest = { confirmAction = null },
                    title = { Text(if (restart) "Restart this PC?" else "Shut down this PC?") },
                    text = {
                        Text(
                            "Save your work on the PC first — this will " +
                                (if (restart) "restart it" else "shut it down") + " now.",
                        )
                    },
                    confirmButton = {
                        TextButton(onClick = {
                            if (restart) PcControl.restart() else PcControl.shutdown()
                            confirmAction = null
                        }) { Text(if (restart) "Restart" else "Shut down") }
                    },
                    dismissButton = {
                        TextButton(onClick = { confirmAction = null }) { Text("Cancel") }
                    },
                )
            }

            // The trackpad gets a floor instead of a weight: weight(1f) is illegal inside a
            // scrolling Column (its height is unbounded), and a floor is what the surface
            // actually needs. 280.dp is roughly a third of a normal phone's content height and
            // leaves room for a full-length drag and a two-finger scroll without the finger
            // running off the pad — well clear of the 48.dp minimum touch target, which sizes a
            // button, not a gesture surface.
            // NOTE (M17a B1): the floor stays the literal `280.dp` here rather than
            // `Dimens.trackpadMin`, because ToolsScreenSourceTest pins the literal. Pinning the
            // constant instead would let the value be edited to anything without the guard
            // noticing, which is a weaker check, so the literal wins and Dimens.trackpadMin
            // records the same number for anyone reaching for the scale.
            Surface(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(min = 280.dp)
                    .padding(bottom = Dimens.xl)
                    .alpha(if (ready) 1f else 0.4f),
                shape = RoundedCornerShape(Dimens.radiusCard),
                color = MaterialTheme.colorScheme.surfaceContainer,
            ) {
                Box(
                    modifier = Modifier
                        .fillMaxSize()
                        .then(if (ready) Modifier.pointerInput(Unit) { trackpadGestures() } else Modifier),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(
                        "Drag to move the cursor · Tap to click · Two fingers to scroll",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(Dimens.xl),
                    )
                }
            }

            // Invisible IME sink that carries keystrokes to the PC, wired exactly like
            // MirrorActivity's — same KeyboardSink, so the mapping never diverges.
            AndroidView(
                modifier = Modifier.size(1.dp).alpha(0f),
                factory = { ctx ->
                    EditText(ctx).apply {
                        isFocusable = true
                        isFocusableInTouchMode = true
                        KeyboardSink.wire(this)
                        keyboardTarget = this
                    }
                },
            )
        }
    }
}

/** Plain-language reason the Tools surface is inert, or null when it's ready to use. */
private fun connectionReason(state: ServiceState): String? = when (state) {
    is ServiceState.Stopped -> "Linc's companion service is off. Open the Status tab to start it."
    is ServiceState.Listening -> "Waiting for your PC to connect."
    is ServiceState.Connected ->
        if (state.protocolVersion >= 14) null
        else "Your PC's Linc app needs updating to use the keyboard and trackpad."
}

/** Plain-language reason the quick controls are disabled (v17, M4b) — separate from the
 * keyboard/trackpad gate above, since a v14-only desktop still has a working keyboard/trackpad. */
private fun controlsReason(state: ServiceState): String? = when (state) {
    is ServiceState.Stopped -> "Linc's companion service is off. Open the Status tab to start it."
    is ServiceState.Listening -> "Waiting for your PC to connect."
    is ServiceState.Connected ->
        if (state.protocolVersion >= 17) null
        else "Your PC's Linc app needs updating to use quick controls."
}

/** Plain-language reason the PC media transport is disabled (v13 `pc.media.control`, M4c) — a
 * third gate, lower than the other two: a v13-to-v16 desktop can still be driven as a media
 * remote even though it has neither quick controls nor a keyboard. */
private fun mediaReason(state: ServiceState): String? = when (state) {
    is ServiceState.Stopped -> "Linc's companion service is off. Open the Status tab to start it."
    is ServiceState.Listening -> "Waiting for your PC to connect."
    is ServiceState.Connected ->
        if (state.protocolVersion >= 13) null
        else "Your PC's Linc app needs updating to control its media."
}

/**
 * Lock/sleep/shutdown/restart and PC brightness (v17, M4b, D-042). Disabled, never
 * hidden, when [ready] is false or a per-control capability flag in [state] says no — each with
 * its own plain-language reason so the surface never offers a control that silently does nothing.
 *
 * M4c moved this card's volume row into [MediaCard]. It is the same `pc.control` volume on the
 * same page — leaving it here as well would have put two identical volume controls one card apart.
 */
@Composable
@OptIn(ExperimentalLayoutApi::class)
private fun QuickControlsCard(
    ready: Boolean,
    blockedReason: String?,
    state: PcControl.State?,
    onLock: () -> Unit,
    onSleep: () -> Unit,
    onShutdown: () -> Unit,
    onRestart: () -> Unit,
    onDimmer: (currentLevel: Int) -> Unit,
    onBrighter: (currentLevel: Int) -> Unit,
) {
    // Capability flags only arrive once a pc.state reply lands; until then, don't disable a
    // control on a guess — assume available so the button isn't stuck dim while `ready` is
    // already true (the reply typically lands within one round trip of Tools opening).
    val canSleep = state?.canSleep ?: true
    val canShutdown = state?.canShutdown ?: true
    val canBrightness = state?.canBrightness ?: true

    LincCard(
        title = "Quick controls",
        modifier = Modifier.padding(bottom = Dimens.l).alpha(if (ready) 1f else 0.4f),
    ) {
        run {
            // M18 A4: observed clipped on the Pixel 7 — four buttons do not fit one row at
            // 1080 px, so "Restart" was pushed off the right edge as an unreachable sliver and
            // stretched the row's height. A FlowRow wraps instead of overflowing. Presentation
            // only: the same four controls with the same actions and the same enabled rules.
            FlowRow(
                horizontalArrangement = Arrangement.spacedBy(Dimens.s),
                verticalArrangement = Arrangement.spacedBy(Dimens.s),
            ) {
                OutlinedButton(onClick = onLock, enabled = ready) { Text("Lock") }
                OutlinedButton(onClick = onSleep, enabled = ready && canSleep) { Text("Sleep") }
                OutlinedButton(onClick = onShutdown, enabled = ready && canShutdown) { Text("Shut down") }
                OutlinedButton(onClick = onRestart, enabled = ready && canShutdown) { Text("Restart") }
            }
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(Dimens.s)) {
                Text("Brightness", modifier = Modifier.wrapContentWidth())
                val level = state?.brightness ?: 50
                OutlinedButton(onClick = { onDimmer(level) }, enabled = ready && canBrightness) { Text("−") }
                Text(state?.brightness?.let { "$it%" } ?: "—", modifier = Modifier.wrapContentWidth())
                OutlinedButton(onClick = { onBrighter(level) }, enabled = ready && canBrightness) { Text("+") }
            }
            if (ready && !canBrightness) {
                Text(
                    "This PC's screen brightness can't be controlled remotely.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            if (blockedReason != null) {
                Text(
                    blockedReason,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

/**
 * The PC as a media remote (M4c Part A): transport for whatever is playing on the PC, plus the
 * PC's volume.
 *
 * **Nothing new goes on the wire.** The transport buttons send the same v13 `pc.media.control`
 * the Home media widget has sent since M19/D-028, through the same [PcMediaControl]; volume sends
 * the v17 `pc.control` M4b shipped, through the same [PcControl]. The two ride different protocol
 * versions, so they are gated separately — a v13-to-v16 desktop is still a working media remote
 * with the volume row disabled.
 *
 * Disabled, never hidden (M4b's rule): when `pc.media.state` reports nothing present, the desktop
 * has no player to route a control to — `PcMediaService.OnCompanionMessage` reaches neither its
 * SMTC branch nor the Winamp-API fallback and the press is dropped — so the transport buttons are
 * disabled with a reason rather than silently doing nothing.
 */
@Composable
private fun MediaCard(
    transportReady: Boolean,
    transportBlockedReason: String?,
    volumeReady: Boolean,
    volumeBlockedReason: String?,
    media: PcMedia?,
    state: PcControl.State?,
    onPrevious: () -> Unit,
    onPlayPause: () -> Unit,
    onNext: () -> Unit,
    onVolumeDown: () -> Unit,
    onVolumeUp: () -> Unit,
    onMuteToggle: () -> Unit,
) {
    // Free of charge: pc.media.state is pushed unsolicited on connect and on every change, so the
    // title is already in hand. Nothing here fetches anything.
    val playing = media?.present == true
    val canTransport = transportReady && playing

    LincCard(title = "Media", modifier = Modifier.padding(bottom = Dimens.l)) {
        run {

            Text(
                nowPlayingText(media),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 2,
            )

            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(Dimens.s)) {
                OutlinedButton(onClick = onPrevious, enabled = canTransport) { Text("Previous") }
                Button(onClick = onPlayPause, enabled = canTransport) {
                    Text(if (media?.playing == true) "Pause" else "Play")
                }
                OutlinedButton(onClick = onNext, enabled = canTransport) { Text("Next") }
            }

            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(Dimens.s)) {
                Text("Volume", modifier = Modifier.wrapContentWidth())
                OutlinedButton(onClick = onVolumeDown, enabled = volumeReady) { Text("−") }
                Text(state?.let { "${it.volume}%" } ?: "—", modifier = Modifier.wrapContentWidth())
                OutlinedButton(onClick = onVolumeUp, enabled = volumeReady) { Text("+") }
                OutlinedButton(onClick = onMuteToggle, enabled = volumeReady) {
                    Text(if (state?.muted == true) "Unmute" else "Mute")
                }
            }

            if (transportBlockedReason != null) {
                Text(
                    transportBlockedReason,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            if (volumeBlockedReason != null && volumeBlockedReason != transportBlockedReason) {
                Text(
                    volumeBlockedReason,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

/** What the PC is playing, or the plain-language reason there is nothing to show. */
private fun nowPlayingText(media: PcMedia?): String = when {
    media == null -> "Waiting to hear what's playing on the PC."
    !media.present -> "Nothing is playing on the PC right now."
    else -> listOfNotNull(
        media.title?.takeIf { it.isNotBlank() },
        media.artist?.takeIf { it.isNotBlank() },
    ).joinToString(" — ").ifEmpty { "Something is playing on the PC." }
}

/**
 * The slideshow clicker (M4c Part B). Reuses the v14 `pc.input` message M4a already sends from
 * this page's keyboard and trackpad — no mirror session is required, and no new message type
 * exists for this. The key mapping is the pure [PresentationKeys]; this card only lays out the
 * buttons and says out loud what they actually are.
 */
@Composable
private fun PresentationCard(
    ready: Boolean,
    blockedReason: String?,
    onAction: (PresentationKeys.Action) -> Unit,
) {
    LincCard(title = "Presentation", modifier = Modifier.padding(bottom = Dimens.l)) {
        run {

            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(Dimens.s)) {
                OutlinedButton(
                    onClick = { onAction(PresentationKeys.Action.Previous) },
                    enabled = ready,
                ) { Text("Previous") }
                Button(
                    onClick = { onAction(PresentationKeys.Action.Next) },
                    enabled = ready,
                ) { Text("Next") }
            }
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(Dimens.s)) {
                OutlinedButton(
                    onClick = { onAction(PresentationKeys.Action.Start) },
                    enabled = ready,
                ) { Text("Start") }
                OutlinedButton(
                    onClick = { onAction(PresentationKeys.Action.End) },
                    enabled = ready,
                ) { Text("End") }
                OutlinedButton(
                    onClick = { onAction(PresentationKeys.Action.Black) },
                    enabled = ready,
                ) { Text("Black") }
            }

            // Said out loud so nobody expects this to know what a slide is.
            Text(
                "These send key presses to whatever is in focus on the PC — there's no slideshow app detection.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            if (blockedReason != null) {
                Text(
                    blockedReason,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

/**
 * One-finger drag moves the cursor, a tap (no movement) clicks, two fingers drag to scroll.
 * The deltas-in/payload-out mapping is [TrackpadGestures]; this loop only does gesture
 * recognition (pointer count, tap-vs-drag) and forwards results to [MirrorControl].
 */
private suspend fun PointerInputScope.trackpadGestures() {
    awaitEachGesture {
        val first = awaitFirstDown(requireUnconsumed = false)
        var lastX = first.position.x
        var lastY = first.position.y
        var moved = false

        while (true) {
            val event = awaitPointerEvent()
            val pressed = event.changes.filter { it.pressed }
            if (pressed.isEmpty()) break

            val primary = pressed.first()
            val dx = primary.position.x - lastX
            val dy = primary.position.y - lastY
            lastX = primary.position.x
            lastY = primary.position.y

            if (dx != 0f || dy != 0f) {
                moved = true
                if (pressed.size >= 2) {
                    val scroll = TrackpadGestures.twoFingerDrag(dx, dy)
                    if (scroll.dx != 0 || scroll.dy != 0) MirrorControl.scroll(scroll.dx, scroll.dy)
                } else {
                    val move = TrackpadGestures.drag(dx, dy)
                    MirrorControl.pointerRelative("move", move.dx, move.dy)
                }
            }
            event.changes.forEach { it.consume() }
        }

        if (!moved) {
            MirrorControl.click(true)
            MirrorControl.click(false)
        }
    }
}
