package app.linc.android.ui

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
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
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.linc.android.service.Explanations
import app.linc.android.service.PcApp
import app.linc.android.service.PcTools
import app.linc.android.ui.kit.ConnectedChoice
import app.linc.android.ui.kit.HelperText
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.LincScaffold
import app.linc.android.ui.kit.SectionHeader
import app.linc.android.ui.kit.pagePadding
import app.linc.android.ui.kit.rememberNotify
import kotlinx.coroutines.delay

/**
 * Tools → PC (D-097): the PC's look and feel from the phone — light or dark, accent colour,
 * transparency, a wallpaper from your photos — plus how it's doing, what it's running, and a link
 * opened on it.
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun PcScreen(onBack: () -> Unit) {
    val context = LocalContext.current
    val look by PcTools.look.collectAsState()
    val info by PcTools.info.collectAsState()
    val apps by PcTools.apps.collectAsState()
    val ready = PcTools.ready()
    val notify = rememberNotify()
    val phoneAccent = MaterialTheme.colorScheme.primary
    val phoneDark = androidx.compose.foundation.isSystemInDarkTheme()
    var fit by rememberSaveable { mutableStateOf("fill") }
    var link by rememberSaveable { mutableStateOf("") }
    var ending by remember { mutableStateOf<PcApp?>(null) }
    val photo = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if (uri != null) notify(if (PcTools.setWallpaper(context, uri, fit)) "Sending the picture to the PC…" else "Couldn't read that picture")
    }

    LaunchedEffect(ready) {
        if (!ready) return@LaunchedEffect
        PcTools.refreshLook(); PcTools.refreshApps()
        while (true) { PcTools.refreshInfo(); delay(3_000) }
    }

    LincScaffold(title = "PC", subtitle = info.name.ifEmpty { null }, onBack = onBack) { inner ->
        LazyColumn(contentPadding = pagePadding(inner), verticalArrangement = Arrangement.spacedBy(Kit.groupGap)) {
            if (!ready) item {
                HelperText("Connect the PC, and update Linc on it, to use these.", kind = Explanations.Kind.Blocked,
                    modifier = Modifier.padding(horizontal = Kit.gutter))
            }
            look.error?.let { e -> item { HelperText(e, kind = Explanations.Kind.Error, modifier = Modifier.padding(horizontal = Kit.gutter)) } }

            // ---- look -------------------------------------------------------------------------
            item { SectionHeader("Look") }
            item {
                Card {
                    Text("Windows", style = MaterialTheme.typography.titleSmall)
                    ConnectedChoice(listOf(false to "Light", true to "Dark"), look.systemDark, { PcTools.setLook(systemDark = it) }, enabled = ready)
                    Text("Apps", style = MaterialTheme.typography.titleSmall)
                    ConnectedChoice(listOf(false to "Light", true to "Dark"), look.appsDark, { PcTools.setLook(appsDark = it) }, enabled = ready)
                    TextButton(onClick = { PcTools.setLook(appsDark = phoneDark, systemDark = phoneDark) }, enabled = ready) {
                        Text("Match this phone (${if (phoneDark) "dark" else "light"})")
                    }
                }
            }
            item {
                Card {
                    Text("Accent colour", style = MaterialTheme.typography.titleSmall)
                    val phoneHex = hex(phoneAccent)
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(10.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        Swatch(phoneHex, "This phone's colour", look.accent.equals(phoneHex, true) && !look.autoAccent, ready) {
                            PcTools.setLook(accent = phoneHex, autoAccent = false)
                        }
                        for (c in ACCENTS) Swatch(c, c, look.accent.equals(c, true) && !look.autoAccent, ready) {
                            PcTools.setLook(accent = c, autoAccent = false)
                        }
                    }
                    SwitchRow("Pick it from the wallpaper", look.autoAccent, ready) { PcTools.setLook(autoAccent = it) }
                    SwitchRow("Transparency effects", look.transparency, ready) { PcTools.setLook(transparency = it) }
                }
            }
            item {
                Card {
                    Text("Wallpaper", style = MaterialTheme.typography.titleSmall)
                    look.wallpaper?.let { Text("Now: $it", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                    ConnectedChoice(listOf("fill" to "Fill", "fit" to "Fit", "stretch" to "Stretch", "center" to "Centre"), fit, { fit = it }, enabled = ready)
                    Button(onClick = { photo.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) }, enabled = ready) {
                        Text("Choose a photo")
                    }
                }
            }

            // ---- status -----------------------------------------------------------------------
            item { SectionHeader("How it's doing") }
            item {
                Card {
                    if (info.os.isNotEmpty()) Text(info.os, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    if (info.cpuPct >= 0) Meter("Processor", "${info.cpuPct}%", info.cpuPct / 100f)
                    if (info.memTotalMb > 0) Meter("Memory", "%.1f of %.1f GB".format(info.memUsedMb / 1024.0, info.memTotalMb / 1024.0),
                        info.memUsedMb.toFloat() / info.memTotalMb)
                    for ((name, free, total) in info.disks) if (total > 0) Meter("Disk $name", "%.0f GB free of %.0f".format(free, total), ((total - free) / total).toFloat())
                    info.battery?.let { (pct, charging) -> Meter("Battery", "$pct%" + if (charging) " · charging" else "", pct / 100f) }
                    if (info.uptimeMin > 0) Text("On for ${uptime(info.uptimeMin)}", style = MaterialTheme.typography.bodySmall)
                }
            }

            // ---- apps -------------------------------------------------------------------------
            item {
                SectionHeader("Running apps") {
                    TextButton(onClick = { PcTools.refreshApps() }, enabled = ready) { Text("Refresh") }
                }
            }
            val list = apps
            if (list == null) item { HelperText("Loading…", modifier = Modifier.padding(horizontal = Kit.gutter)) }
            else for (app in list) item(app.pid) {
                Surface(shape = RoundedCornerShape(16.dp), color = MaterialTheme.colorScheme.surfaceContainer, modifier = Modifier.fillMaxWidth()) {
                    Row(Modifier.padding(start = 16.dp, end = 8.dp, top = 8.dp, bottom = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) {
                            Text(app.title, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text("${app.name} · ${app.memMb} MB", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                        TextButton(onClick = { ending = app }) { Text("End") }
                    }
                }
            }

            // ---- open a link ------------------------------------------------------------------
            item { SectionHeader("Open on the PC") }
            item {
                Row(Modifier.padding(horizontal = Kit.gutter), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedTextField(value = link, onValueChange = { link = it }, label = { Text("A link") }, singleLine = true, modifier = Modifier.weight(1f))
                    FilledTonalButton(onClick = {
                        val url = link.trim().let { if (it.startsWith("http://") || it.startsWith("https://")) it else "https://$it" }
                        PcTools.openOnPc(url); notify("Opened on the PC"); link = ""
                    }, enabled = ready && link.isNotBlank()) { Text("Open") }
                }
            }
        }
    }

    ending?.let { app ->
        AlertDialog(
            onDismissRequest = { ending = null },
            title = { Text("End ${app.name}?") },
            text = { Text("Linc asks it to close first, as its window's X would; if it doesn't within 3 seconds it is ended, and anything unsaved in it is lost.") },
            confirmButton = { TextButton(onClick = { PcTools.endApp(app.pid); ending = null }) { Text("End") } },
            dismissButton = { TextButton(onClick = { ending = null }) { Text("Cancel") } },
        )
    }
}

/** Windows' own accent choices. */
private val ACCENTS = listOf("#0078D7", "#0063B1", "#E81123", "#FF8C00", "#FFB900", "#10893E", "#00B7C3", "#8E8CD8", "#744DA9", "#E3008C", "#767676")

private fun hex(c: Color): String = "#%06X".format(c.toArgb() and 0xFFFFFF)

@Composable
private fun Card(content: @Composable () -> Unit) {
    Surface(shape = RoundedCornerShape(24.dp), color = MaterialTheme.colorScheme.surfaceContainer, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) { content() }
    }
}

@Composable
private fun Swatch(hex: String, label: String, selected: Boolean, enabled: Boolean, onClick: () -> Unit) {
    val color = runCatching { Color(android.graphics.Color.parseColor(hex)) }.getOrDefault(Color.Gray)
    Box(
        Modifier.size(40.dp).clip(CircleShape).background(color)
            .border(if (selected) 3.dp else 0.dp, MaterialTheme.colorScheme.onSurface, CircleShape)
            .clickable(enabled = enabled, onClick = onClick)
            .semantics { contentDescription = label + if (selected) ", chosen" else "" },
        contentAlignment = Alignment.Center,
    ) { if (selected) Icon(LincSymbols.Check, contentDescription = null, tint = Color.White) }
}

@Composable
private fun SwitchRow(label: String, checked: Boolean, enabled: Boolean, onChange: (Boolean) -> Unit) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text(label, style = MaterialTheme.typography.bodyLarge, modifier = Modifier.weight(1f))
        Switch(checked = checked, enabled = enabled, onCheckedChange = onChange)
    }
}

@Composable
private fun Meter(label: String, value: String, fraction: Float) {
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Row {
            Text(label, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f))
            Text(value, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        LinearProgressIndicator(progress = { fraction.coerceIn(0f, 1f) }, modifier = Modifier.fillMaxWidth())
    }
}

private fun uptime(minutes: Long): String = when {
    minutes < 60 -> "$minutes min"
    minutes < 60 * 24 -> "${minutes / 60} h ${minutes % 60} min"
    else -> "${minutes / (60 * 24)} days ${minutes / 60 % 24} h"
}
