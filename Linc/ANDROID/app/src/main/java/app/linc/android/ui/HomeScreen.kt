package app.linc.android.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.text.format.DateUtils
import android.text.format.Formatter
import android.webkit.MimeTypeMap
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Create
import androidx.compose.material.icons.filled.Share
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.FilledIconButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.IconButtonDefaults
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import app.linc.android.R
import app.linc.android.ui.theme.Dimens
import app.linc.android.service.ClipboardHistoryStore
import app.linc.android.service.CompanionStateHolder
import app.linc.android.service.CompanionStateHolder.ServiceState
import app.linc.android.service.PcMedia
import app.linc.android.service.PcMediaControl
import app.linc.android.service.PcMediaStore
import app.linc.android.service.ShareSender
import app.linc.android.service.ShareStore

@Composable
fun HomeScreen() {
    val context = LocalContext.current
    val pcMedia by PcMediaStore.state.collectAsState()
    val clips by ClipboardHistoryStore.items.collectAsState()
    val connection by CompanionStateHolder.state.collectAsState()

    LincScreen(title = stringResource(R.string.nav_home)) {
        ConnectionWidget(connection)
        PcMediaWidget(pcMedia)
        ClipboardWidget(clips.map { it.text to it.fromPhone }) { text ->
            context.getSystemService(ClipboardManager::class.java)
                .setPrimaryClip(ClipData.newPlainText("Linc", text))
        }
        MirrorPcWidget {
            context.startActivity(
                android.content.Intent(context, app.linc.android.MirrorActivity::class.java))
        }
        ToolsWidget {
            context.startActivity(
                android.content.Intent(context, app.linc.android.ToolsActivity::class.java))
        }
        ShareSection()
    }
}

/** Connection & health at a glance: is the PC linked, over what protocol, mirror-ready? */
@Composable
private fun ConnectionWidget(state: ServiceState) {
    val connected = state as? ServiceState.Connected
    val (dot, title, subtitle) = when (state) {
        is ServiceState.Connected -> Triple(
            Color(0xFF4CAF50),
            "Connected",
            "to ${state.desktopApp} · protocol v${state.protocolVersion}",
        )
        is ServiceState.Listening -> Triple(
            Color(0xFFFFB300),
            "Waiting for your PC",
            "Linc is running and ready to connect.",
        )
        is ServiceState.Stopped -> Triple(
            Color(0xFF9E9E9E),
            "Companion is off",
            "Open the Status tab to start it.",
        )
    }

    LincCard {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(Dimens.m),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(
                modifier = Modifier
                    .size(Dimens.m)
                    .clip(CircleShape)
                    .background(dot)
            )
            Column(modifier = Modifier.weight(1f)) {
                Text(title, style = MaterialTheme.typography.titleMedium)
                Text(
                    subtitle,
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            if (connected != null && connected.protocolVersion >= 14) {
                MirrorReadyChip()
            }
        }
    }
}

/** A small pill that signals the link is new enough to mirror the PC (protocol v14+). */
@Composable
private fun MirrorReadyChip() {
    Box(
        modifier = Modifier
            .clip(RoundedCornerShape(50))
            .background(MaterialTheme.colorScheme.secondaryContainer)
            .padding(horizontal = Dimens.s, vertical = Dimens.xs)
    ) {
        Text(
            "Mirror ready",
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSecondaryContainer,
        )
    }
}

/** Entry to the PC Remote (M05): fullscreen landscape view of the PC, touch as mouse. */
@Composable
private fun MirrorPcWidget(onOpen: () -> Unit) {
    LincCard(
        title = "Mirror PC",
        modifier = Modifier
            .heightIn(min = Dimens.touchTarget)
            .clickable(onClick = onOpen),
    ) {
        Text(
            "See and control your PC from here.",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

/** Entry to the Tools page (M4a): full-screen remote keyboard + trackpad. */
@Composable
private fun ToolsWidget(onOpen: () -> Unit) {
    LincCard(
        title = "Tools",
        modifier = Modifier
            .heightIn(min = Dimens.touchTarget)
            .clickable(onClick = onOpen),
    ) {
        Text(
            "Keyboard and trackpad for your PC.",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

@Composable
private fun PcMediaWidget(media: PcMedia?) {
    val playing = media?.present == true && media.playing

    // The desktop republishes every few seconds, so without local ticking the progress bar
    // would jump in steps. Advance it here between pushes and re-sync whenever one lands.
    var elapsedMs by remember(media?.title, media?.positionMs) {
        mutableLongStateOf(media?.positionMs ?: 0L)
    }
    LaunchedEffect(media?.title, media?.positionMs, playing) {
        while (playing) {
            delay(500)
            elapsedMs += 500
        }
    }

    val duration = media?.durationMs ?: 0L
    val progress = if (duration > 0) (elapsedMs.toFloat() / duration).coerceIn(0f, 1f) else 0f
    val animatedProgress by animateFloatAsState(progress, tween(400), label = "mediaProgress")

    // B1: the one card treatment — tonal surface, no elevation. The playing/paused distinction
    // stays, but as a step on the same container ramp rather than a second colour system.
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(Dimens.radiusCard),
        color = if (playing) MaterialTheme.colorScheme.surfaceContainerHighest
        else MaterialTheme.colorScheme.surfaceContainer,
    ) {
        Column(
            modifier = Modifier.padding(Dimens.l),
            verticalArrangement = Arrangement.spacedBy(Dimens.m),
        ) {
            Row(
                horizontalArrangement = Arrangement.spacedBy(Dimens.s),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    "On your PC",
                    style = MaterialTheme.typography.labelLarge,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                if (playing) {
                    EqualizerBars()
                }
            }

            if (media == null || !media.present) {
                Text(
                    "Nothing is playing on the PC.",
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            } else {
                Text(
                    media.title ?: "Unknown title",
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.SemiBold,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
                media.artist?.takeIf { it.isNotBlank() }?.let {
                    Text(
                        it,
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }

                if (duration > 0) {
                    Column(verticalArrangement = Arrangement.spacedBy(Dimens.xs)) {
                        // Display only — the protocol has no seek for PC media (v13).
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(4.dp)
                                .clip(CircleShape)
                                .background(MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.25f))
                        ) {
                            Box(
                                modifier = Modifier
                                    .fillMaxWidth(animatedProgress)
                                    .height(4.dp)
                                    .clip(CircleShape)
                                    .background(MaterialTheme.colorScheme.primary)
                            )
                        }
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                        ) {
                            Text(
                                formatTime(elapsedMs),
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            Text(
                                formatTime(duration),
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                }

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(Dimens.m),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Spacer(Modifier.weight(1f))
                    IconButton(
                        onClick = { PcMediaControl.send("prev") },
                        colors = IconButtonDefaults.iconButtonColors(
                            contentColor = MaterialTheme.colorScheme.onSurface
                        ),
                    ) {
                        TransportGlyph(Glyph.Previous)
                    }
                    FilledIconButton(
                        onClick = { PcMediaControl.send(if (media.playing) "pause" else "play") },
                        modifier = Modifier.size(56.dp),
                    ) {
                        TransportGlyph(if (media.playing) Glyph.Pause else Glyph.Play)
                    }
                    IconButton(
                        onClick = { PcMediaControl.send("next") },
                        colors = IconButtonDefaults.iconButtonColors(
                            contentColor = MaterialTheme.colorScheme.onSurface
                        ),
                    ) {
                        TransportGlyph(Glyph.Next)
                    }
                    Spacer(Modifier.weight(1f))
                }

                media.app?.takeIf { it.isNotBlank() }?.let {
                    Text(
                        appLabel(it),
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.fillMaxWidth(),
                    )
                }
            }
        }
    }
}

private enum class Glyph { Play, Pause, Next, Previous }

/**
 * Transport icons drawn directly. The app deliberately doesn't pull in
 * `material-icons-extended` (it is large, for a handful of glyphs), and the core icon set has
 * no Pause / SkipNext / SkipPrevious — the widget used to show a Play arrow even while playing.
 */
@Composable
private fun TransportGlyph(glyph: Glyph, modifier: Modifier = Modifier) {
    val tint = androidx.compose.material3.LocalContentColor.current
    Canvas(modifier = modifier.size(24.dp)) {
        val w = size.width
        val h = size.height
        when (glyph) {
            Glyph.Play -> drawPath(
                androidx.compose.ui.graphics.Path().apply {
                    moveTo(w * 0.26f, h * 0.16f)
                    lineTo(w * 0.82f, h * 0.5f)
                    lineTo(w * 0.26f, h * 0.84f)
                    close()
                },
                color = tint,
            )

            Glyph.Pause -> {
                val barW = w * 0.16f
                drawRoundRect(
                    color = tint,
                    topLeft = Offset(w * 0.28f, h * 0.18f),
                    size = Size(barW, h * 0.64f),
                    cornerRadius = androidx.compose.ui.geometry.CornerRadius(barW / 3),
                )
                drawRoundRect(
                    color = tint,
                    topLeft = Offset(w * 0.56f, h * 0.18f),
                    size = Size(barW, h * 0.64f),
                    cornerRadius = androidx.compose.ui.geometry.CornerRadius(barW / 3),
                )
            }

            Glyph.Next, Glyph.Previous -> {
                val flip = glyph == Glyph.Previous
                fun x(v: Float) = if (flip) w - v else v
                drawPath(
                    androidx.compose.ui.graphics.Path().apply {
                        moveTo(x(w * 0.20f), h * 0.20f)
                        lineTo(x(w * 0.62f), h * 0.5f)
                        lineTo(x(w * 0.20f), h * 0.80f)
                        close()
                    },
                    color = tint,
                )
                drawRoundRect(
                    color = tint,
                    topLeft = Offset(if (flip) w * 0.16f else w * 0.68f, h * 0.20f),
                    size = Size(w * 0.14f, h * 0.60f),
                    cornerRadius = androidx.compose.ui.geometry.CornerRadius(w * 0.05f),
                )
            }
        }
    }
}

/** Three bars that bounce while the PC is playing — a glanceable "this is live" cue. */
@Composable
private fun EqualizerBars() {
    val transition = rememberInfiniteTransition(label = "eq")
    val bar0 by transition.animateFloat(
        0.35f, 1f, infiniteRepeatable(tween(420), RepeatMode.Reverse), label = "eq0")
    val bar1 by transition.animateFloat(
        0.35f, 1f, infiniteRepeatable(tween(550), RepeatMode.Reverse), label = "eq1")
    val bar2 by transition.animateFloat(
        0.35f, 1f, infiniteRepeatable(tween(680), RepeatMode.Reverse), label = "eq2")

    Row(
        horizontalArrangement = Arrangement.spacedBy(2.dp),
        verticalAlignment = Alignment.Bottom,
        modifier = Modifier.height(12.dp),
    ) {
        listOf(bar0, bar1, bar2).forEach { fraction ->
            Box(
                modifier = Modifier
                    .size(width = 3.dp, height = (12 * fraction).dp)
                    .clip(RoundedCornerShape(2.dp))
                    .background(MaterialTheme.colorScheme.primary)
            )
        }
    }
}

private fun formatTime(ms: Long): String {
    val totalSeconds = (ms / 1000).coerceAtLeast(0)
    val minutes = totalSeconds / 60
    val seconds = totalSeconds % 60
    return if (minutes >= 60) {
        "%d:%02d:%02d".format(minutes / 60, minutes % 60, seconds)
    } else {
        "%d:%02d".format(minutes, seconds)
    }
}

/** Turns the raw source id ("PotPlayerMini64.exe", "winamp-api") into something readable. */
private fun appLabel(app: String): String = when {
    app == "winamp-api" -> "Playing on your PC"
    app.endsWith(".exe", ignoreCase = true) -> app.dropLast(4)
    app.contains('!') -> app.substringAfter('!').ifBlank { app }
    else -> app
}

@Composable
private fun ClipboardWidget(clips: List<Pair<String, Boolean>>, onCopy: (String) -> Unit) {
    LincCard(title = "Clipboard") {
        if (clips.isEmpty()) {
            LincEmpty(
                icon = {
                    Icon(
                        Icons.Filled.Create,
                        contentDescription = null,
                        modifier = Modifier.size(Dimens.xl),
                        tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                },
                text = "Text copied on either device appears here.",
            )
        } else {
                clips.forEach { (text, fromPhone) ->
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .heightIn(min = Dimens.touchTarget)
                            .padding(vertical = Dimens.xs),
                    ) {
                        Text(
                            text,
                            style = MaterialTheme.typography.bodyMedium,
                            maxLines = 2,
                            overflow = TextOverflow.Ellipsis,
                            modifier = Modifier
                                .fillMaxWidth()
                                .clickable { onCopy(text) },
                        )
                        Text(
                            if (fromPhone) "from this phone · tap to copy" else "from PC · tap to copy",
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
        }
    }
}

// ---- Share (M10 Part B): moved here from the standalone ShareScreen — the bottom-nav tab is
// gone (B2.7), this is now Home's own section, still built on ShareStore/ShareSender exactly
// as before. ----

@Composable
private fun ShareSection() {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val received by ShareStore.received.collectAsState()
    val sent by ShareStore.sent.collectAsState()
    val sending by ShareStore.sending.collectAsState()
    val sendStatus by ShareStore.sendStatus.collectAsState()
    val highlight by ShareStore.lastReceived.collectAsState()

    // Let a newly arrived file glow briefly, then settle.
    LaunchedEffect(highlight) {
        if (highlight != null) {
            delay(2500)
            ShareStore.clearHighlight()
        }
    }

    val picker = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri ->
        if (uri != null) {
            val name = ShareSender.nameFor(context, uri)
            ShareStore.beginSend(name)
            scope.launch {
                // Copying into the outbox can be slow for a big file — keep it off the UI thread
                // so the progress bar actually animates instead of freezing.
                val ok = withContext(Dispatchers.IO) { ShareSender.sendFile(context, uri) }
                ShareStore.endSend(name, ok)
            }
        }
    }

    LincSectionLabel("Share")

    // ---- Send to PC ----
    LincCard(title = "Send to PC") {
        run {
            Button(
                onClick = { picker.launch("*/*") },
                enabled = sending == null,
                modifier = Modifier.fillMaxWidth().heightIn(min = Dimens.touchTarget),
            ) {
                Text(if (sending == null) "Pick a file to send" else "Sending…")
            }

            AnimatedVisibility(
                visible = sending != null,
                enter = fadeIn() + expandVertically(),
                exit = fadeOut() + shrinkVertically(),
            ) {
                Column(verticalArrangement = Arrangement.spacedBy(Dimens.s)) {
                    Row(
                        horizontalArrangement = Arrangement.spacedBy(Dimens.m),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        PulsingDot()
                        Text(
                            sending ?: "",
                            style = MaterialTheme.typography.bodyMedium,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                    // Indeterminate: the PC pulls the file, so the phone can't know a percentage.
                    LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
                    Text(
                        "Waiting for the PC to pick it up…",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }

            if (sendStatus.isNotEmpty()) {
                Text(
                    sendStatus,
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.error,
                )
            }

            sent.take(3).forEach { item ->
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(Dimens.s),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        if (item.ok) "✓" else "!",
                        color = if (item.ok) MaterialTheme.colorScheme.primary
                        else MaterialTheme.colorScheme.error,
                        style = MaterialTheme.typography.bodyMedium,
                    )
                    Text(
                        item.name,
                        style = MaterialTheme.typography.bodySmall,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f),
                    )
                    Text(
                        relativeTime(item.at),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        }
    }

    // ---- Received from PC ----
    LincCard {
        run {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text("Received from PC", style = MaterialTheme.typography.titleMedium)
                if (received.isNotEmpty()) {
                    Text(
                        "${received.size}",
                        style = MaterialTheme.typography.labelMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }

            if (received.isEmpty()) {
                LincEmpty(
                    icon = {
                        Icon(
                            Icons.Filled.Share,
                            contentDescription = null,
                            modifier = Modifier.size(Dimens.xl),
                            tint = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    },
                    text = "Files the PC sends land here, saved in Download/Linc.",
                )
            } else {
                received.forEach { item ->
                    ReceivedRow(
                        name = item.name,
                        path = item.path,
                        at = item.at,
                        isNew = item.name == highlight,
                        onOpen = { openFile(context, item.path, item.name) },
                    )
                }
            }
        }
    }
}

@Composable
private fun ReceivedRow(
    name: String,
    path: String,
    at: Long,
    isNew: Boolean,
    onOpen: () -> Unit,
) {
    val background by animateColorAsState(
        targetValue = if (isNew) MaterialTheme.colorScheme.primaryContainer else Color.Transparent,
        animationSpec = tween(durationMillis = 600),
        label = "newFileGlow",
    )
    val size = remember(path) { runCatching { File(path).length() }.getOrDefault(0L) }
    val context = LocalContext.current

    Card(
        colors = CardDefaults.cardColors(containerColor = background),
        shape = RoundedCornerShape(Dimens.radiusControl),
        modifier = Modifier.fillMaxWidth().heightIn(min = Dimens.touchTarget),
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = Dimens.m, vertical = Dimens.s),
            horizontalArrangement = Arrangement.spacedBy(Dimens.m),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(
                Icons.Filled.Share,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.primary,
                modifier = Modifier.size(20.dp),
            )
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    name,
                    style = MaterialTheme.typography.bodyMedium,
                    fontWeight = if (isNew) FontWeight.Bold else FontWeight.Normal,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    buildString {
                        if (size > 0) append(Formatter.formatShortFileSize(context, size)).append(" · ")
                        append(relativeTime(at))
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            TextButton(onClick = onOpen) { Text("Open") }
        }
    }
}

/** A slow pulse, so an indeterminate send still reads as "something is happening". */
@Composable
private fun PulsingDot() {
    val transition = rememberInfiniteTransition(label = "sendPulse")
    val alpha by transition.animateFloat(
        initialValue = 0.3f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(tween(700), RepeatMode.Reverse),
        label = "sendPulseAlpha",
    )
    Box(
        modifier = Modifier
            .size(10.dp)
            .alpha(alpha)
            .background(MaterialTheme.colorScheme.primary, CircleShape)
    )
}

private fun relativeTime(at: Long): String =
    DateUtils.getRelativeTimeSpanString(at, System.currentTimeMillis(), DateUtils.MINUTE_IN_MILLIS).toString()

private fun openFile(context: android.content.Context, path: String, name: String) {
    runCatching {
        val uri = FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", File(path))
        val ext = name.substringAfterLast('.', "").lowercase()
        val mime = MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext) ?: "*/*"
        context.startActivity(
            Intent(Intent.ACTION_VIEW)
                .setDataAndType(uri, mime)
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        )
    }
}
