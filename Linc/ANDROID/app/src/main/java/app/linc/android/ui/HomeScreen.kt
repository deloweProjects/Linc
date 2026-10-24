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
import androidx.compose.material3.FilledIconButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialShapes
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.toShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.animation.core.animateFloat
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
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
import app.linc.android.ui.kit.LincBottomSheet
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

/** Where Home's tiles lead; the host switches tabs (and, for a Tools page, picks it). */
enum class HomeDestination { Remote, Sound, Pc, Files, Sync, Terminal, Share, Clipboard }

/**
 * Home (1.0.0.11, D-082): the connection hero (tap for the Connection sheet), the tools as live
 * tiles and what the PC is playing. Mirror PC sits in the Your PC card.
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
    val pcLink by CompanionStateHolder.pcLink.collectAsState()
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
        transport = transportLabel(context, link, pcLink),
        adb = pcLink?.adb,
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
    /** The PC's ADB state (v32 link.state), or null when the PC hasn't said (older PCs). */
    adb: Boolean? = null,
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
    var connectionSheet by remember { mutableStateOf(false) }
    var voiceSheet by remember { mutableStateOf(false) }
    var powerSheet by remember { mutableStateOf(false) }
    var telegramDone by remember { mutableStateOf(ProjectLinks.telegramCardDone(context)) }
    val connected = connection as? ServiceState.Connected

    LincScaffold(
        title = "Linc",
        onSettings = onSettings,
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
                ConnectionHero(connection, transport, adb, onClick = { connectionSheet = true }, onMirror = onMirror)
            }
            if (connected != null) {
                item("pc-stats") { PcStatsRow(onOpen = { onNavigate(HomeDestination.Pc) }) }
                item("pc-quick") { PcQuickControls(connected) }
            }
            if (media?.present == true) {
                item("media") { NowPlaying(media) }
            }
            item("tools-h") { SectionHeader("Control") }
            item("tools") {
                val ready = connected != null
                ToolGrid(listOf(
                    HomeTool("Remote", LincSymbols.Mouse, MaterialShapes.Cookie9Sided) { onNavigate(HomeDestination.Remote) },
                    HomeTool("Sound", LincSymbols.GraphicEq, MaterialShapes.Clover4Leaf) { onNavigate(HomeDestination.Sound) },
                    HomeTool("PC", LincSymbols.DesktopWindows, MaterialShapes.Square) { onNavigate(HomeDestination.Pc) },
                    HomeTool("Files", LincSymbols.Folder, MaterialShapes.Sunny) { onNavigate(HomeDestination.Files) },
                    HomeTool("Sync", LincSymbols.Sync, MaterialShapes.Cookie4Sided) { onNavigate(HomeDestination.Sync) },
                    HomeTool("Terminal", LincSymbols.Terminal, MaterialShapes.Pill) { onNavigate(HomeDestination.Terminal) },
                    HomeTool("Send", LincSymbols.Send, MaterialShapes.Gem) { onNavigate(HomeDestination.Share) },
                    HomeTool("Clipboard", LincSymbols.Clipboard, MaterialShapes.SoftBurst) { onNavigate(HomeDestination.Clipboard) },
                    HomeTool("Voice", LincSymbols.VoiceAccess, MaterialShapes.Pentagon, active = voice.on || voice.tool, enabled = ready) { voiceSheet = true },
                    HomeTool("Quick Share", LincSymbols.NearMe, MaterialShapes.Cookie7Sided) { onNavigate(HomeDestination.Share) },
                    HomeTool("Power", LincSymbols.PowerOff, MaterialShapes.Sunny, enabled = ready) { powerSheet = true },
                ))
            }
            lastClip?.let { clip ->
                item("clip") {
                    Surface(
                        onClick = { onNavigate(HomeDestination.Clipboard) },
                        shape = RoundedCornerShape(20.dp),
                        color = MaterialTheme.colorScheme.surfaceContainer,
                        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter),
                    ) {
                        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                            Icon(LincSymbols.Copy, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                            Column(Modifier.weight(1f)) {
                                Text("Last copied", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
                                Text(clip.text.lineSequence().firstOrNull().orEmpty().take(120), style = MaterialTheme.typography.bodyMedium,
                                    maxLines = 1, overflow = TextOverflow.Ellipsis)
                            }
                            Icon(LincSymbols.ChevronRight, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
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
        }
    }

    if (connectionSheet) ConnectionSheet(onDismiss = { connectionSheet = false })
    if (powerSheet) {
        // The PC's power, from Home: lock, sleep, restart, shut down, and its brightness.
        LincBottomSheet(onDismissRequest = { powerSheet = false }) {
            Column(
                Modifier.fillMaxWidth().padding(bottom = 24.dp),
                verticalArrangement = Arrangement.spacedBy(Kit.groupGap),
            ) { PowerSheet(controlsReason(connection)) }
        }
    }
    if (voiceSheet) {
        LincBottomSheet(onDismissRequest = { voiceSheet = false }) {
            Column(
                Modifier.fillMaxWidth().padding(bottom = 24.dp),
                verticalArrangement = Arrangement.spacedBy(Kit.groupGap),
            ) { VoiceSheet() }
        }
    }
}

/**
 * The connection, as Home's hero: the PC's name large, a chip for how it's linked (USB cable or
 * Wi-Fi) and one for its state, on the theme's primary container. Tap for the Connection sheet.
 */
@OptIn(androidx.compose.material3.ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun ConnectionHero(connection: ServiceState, transport: String?, adb: Boolean?, onClick: () -> Unit, onMirror: () -> Unit) {
    val connected = connection as? ServiceState.Connected
    val (label, quality) = when (connection) {
        is ServiceState.Connected -> "Connected" to Quality.Good
        is ServiceState.Listening -> "Looking for your PC" to Quality.Fair
        is ServiceState.Stopped -> "Linc isn't running" to Quality.Off
    }
    val usb = transport?.startsWith("USB") == true
    // The shape turns slowly while Linc looks for the PC, and rests once it is connected.
    val spin = if (connection is ServiceState.Listening) {
        val t = androidx.compose.animation.core.rememberInfiniteTransition(label = "heroSpin")
        t.animateFloat(0f, 360f, androidx.compose.animation.core.infiniteRepeatable(
            androidx.compose.animation.core.tween(12_000, easing = androidx.compose.animation.core.LinearEasing)), label = "heroSpinAngle").value
    } else 0f
    HeroCard(onClick = onClick) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(16.dp)) {
            Box(Modifier.size(72.dp), contentAlignment = Alignment.Center) {
                Box(
                    Modifier.size(72.dp).rotate(spin).clip(MaterialShapes.Cookie9Sided.toShape())
                        .background(MaterialTheme.colorScheme.primary),
                )
                Icon(
                    when {
                        usb -> LincSymbols.Usb
                        connected != null -> LincSymbols.Wifi
                        else -> LincSymbols.Computer
                    },
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.onPrimary,
                    modifier = Modifier.size(32.dp),
                )
            }
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(
                    connected?.host ?: "Your PC",
                    style = MaterialTheme.typography.headlineMedium,
                    fontWeight = FontWeight.SemiBold,
                    color = MaterialTheme.colorScheme.onPrimaryContainer,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    StatusChip(label, quality)
                    if (connected != null && transport != null) StatusChip(if (usb) "USB" else transport.removePrefix("Wi-Fi ").let { if (it.startsWith("(")) "Wi-Fi" else it }, Quality.Good)
                    // The same ADB chip as the PC's, from what the PC says (link.state).
                    if (connected != null && adb != null) StatusChip("ADB", if (adb) Quality.Good else Quality.Off)
                }
            }
        }
        Text(
            when (connection) {
                is ServiceState.Connected -> transport?.let { "Over $it · tap for details" } ?: "Tap for details"
                is ServiceState.Listening -> "It connects by itself when the PC is on the same Wi-Fi or the cable is in."
                is ServiceState.Stopped -> "Tap to start Linc."
            },
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onPrimaryContainer,
        )
        // Mirror PC lives with the PC it mirrors (it was the page's floating button).
        if (connected != null) {
            androidx.compose.material3.Button(
                onClick = onMirror,
                modifier = Modifier.fillMaxWidth().heightIn(min = Kit.touch),
            ) {
                Icon(LincSymbols.ScreenShare, contentDescription = null, modifier = Modifier.size(20.dp))
                Text("Mirror PC", modifier = Modifier.padding(start = 8.dp))
            }
        }
    }
}

/** One of Home's tool buttons: an icon in an Expressive shape over its name. */
private class HomeTool(
    val label: String,
    val icon: androidx.compose.ui.graphics.vector.ImageVector,
    val shape: androidx.graphics.shapes.RoundedPolygon,
    val active: Boolean = false,
    val enabled: Boolean = true,
    val onClick: () -> Unit,
)

/** Three across: every tool, one tap away, in the shapes Tools uses. */
@OptIn(androidx.compose.material3.ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun ToolGrid(tools: List<HomeTool>) {
    Column(Modifier.padding(horizontal = Kit.gutter), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        tools.chunked(3).forEach { row ->
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                for (t in row) {
                    Surface(
                        onClick = t.onClick,
                        enabled = t.enabled,
                        shape = RoundedCornerShape(24.dp),
                        color = if (t.active) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceContainer,
                        modifier = Modifier.weight(1f),
                    ) {
                        Column(
                            Modifier.padding(vertical = 16.dp).alphaIf(!t.enabled),
                            horizontalAlignment = Alignment.CenterHorizontally,
                            verticalArrangement = Arrangement.spacedBy(8.dp),
                        ) {
                            Box(
                                Modifier.size(48.dp).clip(t.shape.toShape())
                                    .background(if (t.active) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.secondaryContainer),
                                contentAlignment = Alignment.Center,
                            ) {
                                Icon(t.icon, contentDescription = null,
                                    tint = if (t.active) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSecondaryContainer)
                            }
                            Text(t.label, style = MaterialTheme.typography.labelLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                    }
                }
                repeat(3 - row.size) { androidx.compose.foundation.layout.Spacer(Modifier.weight(1f)) }
            }
        }
    }
}

private fun Modifier.alphaIf(dim: Boolean) = if (dim) this.alpha(0.4f) else this

/**
 * The PC at a glance, from Tools → PC's figures: processor, memory and battery (or disk) as
 * rings, refreshed every few seconds while Home shows. Tap for the full Performance page.
 */
@Composable
private fun PcStatsRow(onOpen: () -> Unit) {
    if (!app.linc.android.service.PcTools.ready()) return
    val info by app.linc.android.service.PcTools.info.collectAsState()
    LaunchedEffect(Unit) {
        while (true) {
            app.linc.android.service.PcTools.refreshInfo()
            kotlinx.coroutines.delay(3_000)
        }
    }
    if (info.cpuPct < 0 && info.memTotalMb <= 0) return
    Row(Modifier.fillMaxWidth().padding(horizontal = Kit.gutter), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        StatRing("CPU", info.cpuPct.coerceAtLeast(0) / 100f, "${info.cpuPct.coerceAtLeast(0)}%",
            MaterialTheme.colorScheme.primary, Modifier.weight(1f), onOpen)
        val mem = if (info.memTotalMb > 0) info.memUsedMb.toFloat() / info.memTotalMb else 0f
        StatRing("Memory", mem, "%.1f GB".format(info.memUsedMb / 1024.0), MaterialTheme.colorScheme.tertiary, Modifier.weight(1f), onOpen)
        val battery = info.battery
        if (battery != null) {
            StatRing(if (battery.second) "Charging" else "Battery", battery.first / 100f, "${battery.first}%",
                MaterialTheme.colorScheme.secondary, Modifier.weight(1f), onOpen)
        } else {
            val disk = info.disks.firstOrNull()
            val used = disk?.let { (_, free, total) -> if (total > 0) ((total - free) / total).toFloat() else 0f } ?: 0f
            StatRing(disk?.first?.let { "Disk $it" } ?: "Disk", used, disk?.let { "%.0f GB free".format(it.second) } ?: "—",
                MaterialTheme.colorScheme.secondary, Modifier.weight(1f), onOpen)
        }
    }
}

@Composable
private fun StatRing(label: String, fraction: Float, value: String, color: androidx.compose.ui.graphics.Color, modifier: Modifier, onClick: () -> Unit) {
    val shown by androidx.compose.animation.core.animateFloatAsState(fraction.coerceIn(0f, 1f), label = "ring")
    Surface(onClick = onClick, shape = RoundedCornerShape(24.dp), color = MaterialTheme.colorScheme.surfaceContainer, modifier = modifier) {
        Column(Modifier.padding(vertical = 14.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Box(contentAlignment = Alignment.Center) {
                androidx.compose.material3.CircularProgressIndicator(
                    progress = { shown }, modifier = Modifier.size(56.dp), color = color, strokeWidth = 6.dp,
                    trackColor = color.copy(alpha = 0.16f),
                )
                Text("${(shown * 100).toInt()}", style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold)
            }
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                Text(label, style = MaterialTheme.typography.labelLarge, maxLines = 1)
                Text(value, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1)
            }
        }
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

/**
 * "USB cable" / "Wi-Fi" for the live link, or null when not connected. What the PC says
 * ([pcLink], v32 link.state) wins: the PC owns how the two are connected, so both screens agree.
 * Only an older PC leaves this phone to judge from its own link ("Wi-Fi (direct)" then).
 */
internal fun transportLabel(
    context: Context,
    link: CompanionStateHolder.Link?,
    pcLink: CompanionStateHolder.PcLink? = CompanionStateHolder.pcLink.value,
): String? {
    link ?: return null
    pcLink?.let { return it.label }
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
