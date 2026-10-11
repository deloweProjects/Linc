@file:OptIn(androidx.compose.material3.ExperimentalMaterial3ExpressiveApi::class, androidx.compose.material3.ExperimentalMaterial3Api::class)

package app.linc.android.ui

import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.FilledIconButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialShapes
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.toShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import app.linc.android.service.ClipItem
import app.linc.android.service.ClipboardHistoryStore
import app.linc.android.service.CompanionStateHolder
import app.linc.android.service.CompanionStateHolder.ServiceState
import app.linc.android.service.ConnectionWatch
import app.linc.android.service.PcMedia
import app.linc.android.service.PcMediaControl
import app.linc.android.service.PcMediaStore
import app.linc.android.service.PcVoiceAccess
import app.linc.android.service.ProjectLinks
import app.linc.android.service.TransportStore
import app.linc.android.service.VoiceAccessState
import app.linc.android.ui.kit.BlockedState
import app.linc.android.ui.kit.HelperText
import app.linc.android.ui.kit.HeroCard
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.LincScaffold
import app.linc.android.ui.kit.Quality
import app.linc.android.ui.kit.SectionHeader
import app.linc.android.ui.kit.StatusChip
import app.linc.android.ui.kit.TileGrid
import app.linc.android.ui.kit.ToolTile
import app.linc.android.ui.kit.pagePadding
import app.linc.android.ui.theme.LincTheme

/** Where Home's tiles lead; the host switches tabs. */
enum class HomeDestination { Remote, Share, Clipboard }

/**
 * Home (1.0.0.11, D-082): the connection hero (tap for the Connection sheet), the tools as live
 * tiles, what the PC is playing, and Mirror PC as the extended FAB.
 */
@Composable
fun HomeScreen(
    onSettings: () -> Unit,
    onNavigate: (HomeDestination) -> Unit,
    onMirror: () -> Unit,
    onSetUp: () -> Unit,
) {
    val context = LocalContext.current
    val connection by CompanionStateHolder.state.collectAsState()
    val link by CompanionStateHolder.link.collectAsState()
    val media by PcMediaStore.state.collectAsState()
    val clips by ClipboardHistoryStore.items.collectAsState()
    val voice by PcVoiceAccess.state.collectAsState()
    val update by app.linc.android.service.UpdateCheck.offer.collectAsState()
    val updateProgress by app.linc.android.service.UpdateCheck.progress.collectAsState()
    val updateProblem by app.linc.android.service.UpdateCheck.problem.collectAsState()
    var exempt by remember { mutableStateOf(ConnectionWatch.isBatteryExempt(context)) }
    var paired by remember { mutableStateOf(TransportStore.hasAnyPc(context)) }
    LaunchedEffect(Unit) {
        while (true) {
            exempt = ConnectionWatch.isBatteryExempt(context)
            paired = TransportStore.hasAnyPc(context)
            kotlinx.coroutines.delay(2_000)
        }
    }
    HomeContent(
        connection = connection,
        transport = transportLabel(context, link),
        exempt = exempt,
        everPaired = paired || connection is ServiceState.Connected,
        media = media,
        lastClip = clips.firstOrNull(),
        voice = voice,
        update = update?.takeIf { !it.forced },
        updateProgress = updateProgress,
        updateProblem = updateProblem,
        onSettings = onSettings,
        onNavigate = onNavigate,
        onMirror = onMirror,
        onSetUp = onSetUp,
    )
}

@Composable
private fun HomeContent(
    connection: ServiceState,
    transport: String?,
    exempt: Boolean,
    everPaired: Boolean,
    media: PcMedia?,
    lastClip: ClipItem?,
    voice: VoiceAccessState,
    update: app.linc.android.service.UpdateCheck.Offer? = null,
    updateProgress: Float? = null,
    updateProblem: String? = null,
    onSettings: () -> Unit,
    onNavigate: (HomeDestination) -> Unit,
    onMirror: () -> Unit,
    onSetUp: () -> Unit,
) {
    val context = LocalContext.current
    val list = rememberLazyListState()
    val fabExpanded by remember { derivedStateOf { list.firstVisibleItemIndex == 0 && list.firstVisibleItemScrollOffset < 40 } }
    var connectionSheet by remember { mutableStateOf(false) }
    var voiceSheet by remember { mutableStateOf(false) }
    var telegramDone by remember { mutableStateOf(ProjectLinks.telegramCardDone(context)) }
    val connected = connection as? ServiceState.Connected

    LincScaffold(
        title = "Linc",
        onSettings = onSettings,
        floatingActionButton = {
            ExtendedFloatingActionButton(
                text = { Text("Mirror PC") },
                icon = { Icon(LincSymbols.ScreenShare, contentDescription = null) },
                onClick = onMirror,
                expanded = fabExpanded,
            )
        },
    ) { inner ->
        LazyColumn(state = list, contentPadding = pagePadding(inner), verticalArrangement = Arrangement.spacedBy(Kit.groupGap)) {
            if (update != null) {
                item("update") {
                    UpdateAvailableCard(
                        version = update.version,
                        notes = update.notes,
                        problem = updateProblem,
                        progress = updateProgress,
                        onUpdate = { app.linc.android.service.UpdateCheck.install(context) },
                        onSkip = { app.linc.android.service.UpdateCheck.skip(context) },
                    )
                }
            }
            if (!exempt) {
                item("background") { BackgroundWarning() }
            }
            if (!everPaired) {
                item("setup") {
                    BlockedState(
                        title = "Connect to a PC",
                        reason = "Linc hasn't been connected to a PC yet. Setup takes a couple of minutes.",
                        icon = LincSymbols.Computer,
                        action = "Set up",
                        onAction = onSetUp,
                    )
                }
            }
            item("hero") {
                ConnectionHero(connection, transport, onClick = { connectionSheet = true })
            }
            // 1.0.0.15: once, after the first PC — setup's last page already offered it to new users.
            if (everPaired && !telegramDone) {
                item("telegram") {
                    TelegramCard(
                        onJoin = {
                            ProjectLinks.markTelegramCardDone(context); telegramDone = true
                            try {
                                context.startActivity(Intent(Intent.ACTION_VIEW, android.net.Uri.parse(ProjectLinks.TELEGRAM))
                                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                            } catch (_: android.content.ActivityNotFoundException) { /* no browser or Telegram */ }
                        },
                        onDismiss = { ProjectLinks.markTelegramCardDone(context); telegramDone = true },
                    )
                }
            }
            item("tools-h") { SectionHeader("Tools") }
            item("tools") {
                val ready = connected != null
                TileGrid(tiles = listOf(
                    { m -> ToolTile("Remote", LincSymbols.Remote, m, state = "Trackpad, keyboard, media",
                        shape = MaterialShapes.Cookie9Sided.toShape(), onClick = { onNavigate(HomeDestination.Remote) }) },
                    { m -> ToolTile("Send to PC", LincSymbols.Send, m, state = if (ready) "Files, photos, links" else "With Quick Share",
                        shape = MaterialShapes.Clover4Leaf.toShape(), onClick = { onNavigate(HomeDestination.Share) }) },
                    { m -> ToolTile("Clipboard", LincSymbols.Clipboard, m, state = lastClip?.text?.lineSequence()?.firstOrNull()?.take(60) ?: "Nothing copied yet",
                        shape = MaterialShapes.Sunny.toShape(), onClick = { onNavigate(HomeDestination.Clipboard) }) },
                    { m -> ToolTile("Voice Access", LincSymbols.VoiceAccess, m, active = voice.on,
                        state = when { !ready -> "Not connected"; voice.on -> "On"; else -> "Off" },
                        shape = MaterialShapes.SoftBurst.toShape(), enabled = ready, onClick = { voiceSheet = true }) },
                    { m -> ToolTile("Microphone", LincSymbols.Mic, m, active = voice.tool,
                        state = when { !ready -> "Not connected"; voice.tool -> "The PC hears this phone"; else -> "Off" },
                        shape = MaterialShapes.Pentagon.toShape(), enabled = ready, onClick = { voiceSheet = true }) },
                    { m -> ToolTile("Quick Share", LincSymbols.NearMe, m, state = "Any device nearby",
                        shape = MaterialShapes.Gem.toShape(), onClick = { onNavigate(HomeDestination.Share) }) },
                ))
            }
            if (media?.present == true) {
                item("media") { NowPlaying(media) }
            }
        }
    }

    if (connectionSheet) ConnectionSheet(onDismiss = { connectionSheet = false })
    if (voiceSheet) {
        ModalBottomSheet(onDismissRequest = { voiceSheet = false }) {
            Column(
                Modifier.fillMaxWidth().padding(bottom = 24.dp),
                verticalArrangement = Arrangement.spacedBy(Kit.groupGap),
            ) { VoiceSheet() }
        }
    }
}

@Composable
private fun ConnectionHero(connection: ServiceState, transport: String?, onClick: () -> Unit) {
    val connected = connection as? ServiceState.Connected
    val (label, quality) = when (connection) {
        is ServiceState.Connected -> "Connected" to Quality.Good
        is ServiceState.Listening -> "Looking for your PC" to Quality.Fair
        is ServiceState.Stopped -> "Linc isn't running" to Quality.Off
    }
    HeroCard(onClick = onClick) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(16.dp)) {
            Box(
                Modifier.size(56.dp).clip(MaterialShapes.Cookie7Sided.toShape()).background(MaterialTheme.colorScheme.onPrimaryContainer.copy(alpha = 0.12f)),
                contentAlignment = Alignment.Center,
            ) {
                Icon(
                    when {
                        transport?.startsWith("USB") == true -> LincSymbols.Usb
                        connected != null -> LincSymbols.Wifi
                        else -> LincSymbols.Computer
                    },
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.onPrimaryContainer,
                )
            }
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Text(
                    connected?.host ?: "Your PC",
                    style = MaterialTheme.typography.headlineSmall,
                    color = MaterialTheme.colorScheme.onPrimaryContainer,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                StatusChip(label, quality)
            }
            Icon(LincSymbols.ChevronRight, contentDescription = "Connection details", tint = MaterialTheme.colorScheme.onPrimaryContainer)
        }
        Text(
            when (connection) {
                is ServiceState.Connected -> transport?.let { "Over $it" } ?: "Connected"
                is ServiceState.Listening -> "It connects by itself when the PC is on the same Wi-Fi or the cable is in."
                is ServiceState.Stopped -> "Tap to start Linc."
            },
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onPrimaryContainer,
        )
    }
}

/**
 * Shown while Linc isn't allowed to run in the background — the reason a phone "connects and
 * drops" over Wi-Fi (D-077). Goes away once allowed.
 */
@Composable
private fun BackgroundWarning() {
    val context = LocalContext.current
    Surface(
        color = MaterialTheme.colorScheme.errorContainer,
        contentColor = MaterialTheme.colorScheme.onErrorContainer,
        shape = RoundedCornerShape(Kit.heroRadius),
        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter),
    ) {
        Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Icon(LincSymbols.BatteryAlert, contentDescription = null)
                Text("Linc may lose your PC in the background", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
            }
            Text(
                "Android can stop Linc when the screen is off, which drops the Wi-Fi link." +
                    if (ConnectionWatch.hasMakerBackgroundManager) " This phone's maker adds its own manager too — turn Linc on in its auto-start list." else "",
                style = MaterialTheme.typography.bodyMedium,
            )
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(onClick = { ConnectionWatch.requestBatteryExemption(context) }, shapes = ButtonDefaults.shapes()) { Text("Allow") }
                if (ConnectionWatch.hasMakerBackgroundManager) {
                    OutlinedButton(onClick = { ConnectionWatch.openMakerBackgroundSettings(context) }, shapes = ButtonDefaults.shapes()) { Text("Auto-start") }
                }
            }
        }
    }
}

@Composable
private fun NowPlaying(media: PcMedia) {
    Surface(
        color = MaterialTheme.colorScheme.tertiaryContainer,
        contentColor = MaterialTheme.colorScheme.onTertiaryContainer,
        shape = RoundedCornerShape(Kit.heroRadius),
        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter),
    ) {
        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(16.dp)) {
            Box(
                Modifier.size(48.dp).clip(MaterialShapes.Cookie12Sided.toShape()).background(MaterialTheme.colorScheme.onTertiaryContainer.copy(alpha = 0.12f)),
                contentAlignment = Alignment.Center,
            ) { Icon(LincSymbols.MusicNote, contentDescription = null) }
            Column(Modifier.weight(1f)) {
                Text("On your PC", style = MaterialTheme.typography.labelLarge)
                Text(media.title ?: "Something is playing", style = MaterialTheme.typography.titleMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                media.artist?.takeIf { it.isNotBlank() }?.let {
                    Text(it, style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            }
            FilledIconButton(onClick = { PcMediaControl.send(if (media.playing) "pause" else "play") }, modifier = Modifier.size(52.dp)) {
                Icon(if (media.playing) LincSymbols.Pause else LincSymbols.Play, contentDescription = if (media.playing) "Pause" else "Play")
            }
        }
    }
}

/** "USB cable" / "Wi-Fi" / "Wi-Fi (direct)" for the live link, or null when not connected. */
internal fun transportLabel(context: Context, link: CompanionStateHolder.Link?): String? {
    link ?: return null
    return when (link.route) {
        CompanionStateHolder.Route.Direct -> if (link.address == "127.0.0.1") "USB cable" else "Wi-Fi (direct)"
        CompanionStateHolder.Route.Adb -> if (usbConnected(context)) "USB cable" else "Wi-Fi"
    }
}

/** The platform's sticky USB state: whether a cable to a computer is in right now. */
private fun usbConnected(context: Context): Boolean = runCatching {
    context.registerReceiver(null, IntentFilter("android.hardware.usb.action.USB_STATE"))
        ?.getBooleanExtra("connected", false) == true
}.getOrDefault(false)

// ---- previews --------------------------------------------------------------------------------

@Composable
private fun HomePreview(state: ServiceState, exempt: Boolean = true, everPaired: Boolean = true) = HomeContent(
    connection = state,
    transport = if (state is ServiceState.Connected) "Wi-Fi (direct)" else null,
    exempt = exempt,
    everPaired = everPaired,
    media = PcMedia(present = true, playing = true, title = "Midnight City", artist = "M83", app = null, positionMs = 0, durationMs = 0),
    lastClip = ClipItem(1, "Meeting moved to 3 pm", true, 0),
    voice = VoiceAccessState(available = true, on = true),
    onSettings = {}, onNavigate = {}, onMirror = {}, onSetUp = {},
)

@Preview(name = "Home — light", widthDp = 360, heightDp = 1100)
@Composable
private fun HomePreviewLight() = LincTheme(darkTheme = false) { HomePreview(ServiceState.Connected("linc-desktop", "1.0.0.11", 24, "DESKTOP-PPPT5UX")) }

@Preview(name = "Home — dark, warning", widthDp = 411, heightDp = 1100)
@Composable
private fun HomePreviewDark() = LincTheme(darkTheme = true) { HomePreview(ServiceState.Listening, exempt = false) }

@Preview(name = "Home — never set up", widthDp = 360, heightDp = 1100, fontScale = 2f)
@Composable
private fun HomePreviewNew() = LincTheme { HomePreview(ServiceState.Stopped, everPaired = false) }

/** Home's one-time invitation to the Linc Telegram channel. Either button retires it for good. */
@Composable
private fun TelegramCard(onJoin: () -> Unit, onDismiss: () -> Unit) {
    Surface(
        color = MaterialTheme.colorScheme.tertiaryContainer,
        contentColor = MaterialTheme.colorScheme.onTertiaryContainer,
        shape = RoundedCornerShape(Kit.heroRadius),
        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter),
    ) {
        Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Icon(LincSymbols.Send, contentDescription = null)
                Text("Join the Linc channel", style = MaterialTheme.typography.titleMedium)
            }
            Text("News, new versions and help from the developer, on Telegram.", style = MaterialTheme.typography.bodyMedium)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(onClick = onJoin, shapes = ButtonDefaults.shapes(), modifier = Modifier.heightIn(min = Kit.touch)) { Text("Join") }
                androidx.compose.material3.TextButton(onClick = onDismiss, modifier = Modifier.heightIn(min = Kit.touch)) { Text("Not now") }
            }
        }
    }
}
