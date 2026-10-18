package app.linc.android.ui

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.animateContentSize
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialShapes
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.toShape
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
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.linc.android.service.Explanations
import app.linc.android.service.PcApp
import app.linc.android.service.PcHistory
import app.linc.android.service.PcInfoState
import app.linc.android.service.PcTools
import app.linc.android.ui.kit.ConnectedChoice
import app.linc.android.ui.kit.HelperText
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.LincScaffold
import app.linc.android.ui.kit.SectionHeader
import app.linc.android.ui.kit.pagePadding
import app.linc.android.ui.kit.segmentOf
import app.linc.android.ui.kit.segmentShape
import app.linc.android.ui.kit.rememberNotify
import kotlinx.coroutines.delay

/** What the performance card is showing, as Task Manager's Performance tab lists them. */
private enum class Meter(val label: String, val icon: ImageVector) {
    Cpu("CPU", LincSymbols.Chip), Memory("Memory", LincSymbols.Memory), Disk("Disk", LincSymbols.HardDrive),
    Network("Network", LincSymbols.Lan), Gpu("GPU", LincSymbols.Developer),
}

/**
 * Tools → PC (D-097): how the PC is doing, as Task Manager's Performance tab shows it — a live
 * graph for the processor, memory, disk, network and GPU, with the figures beside it — then what
 * it's running, its look (light or dark, accent, transparency, wallpaper), and a link opened on it.
 */
@OptIn(ExperimentalLayoutApi::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun PcScreen(onBack: (() -> Unit)? = null, onSettings: (() -> Unit)? = null) {
    val context = LocalContext.current
    val look by PcTools.look.collectAsState()
    val info by PcTools.info.collectAsState()
    val history by PcTools.history.collectAsState()
    val apps by PcTools.apps.collectAsState()
    val ready = PcTools.ready()
    val notify = rememberNotify()
    val phoneAccent = MaterialTheme.colorScheme.primary
    val phoneDark = androidx.compose.foundation.isSystemInDarkTheme()
    var fit by rememberSaveable { mutableStateOf("fill") }
    var link by rememberSaveable { mutableStateOf("") }
    var ending by remember { mutableStateOf<PcApp?>(null) }
    var meter by rememberSaveable { mutableStateOf(Meter.Cpu) }
    var showAllApps by rememberSaveable { mutableStateOf(false) }
    val photo = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if (uri != null) notify(if (PcTools.setWallpaper(context, uri, fit)) "Sending the picture to the PC…" else "Couldn't read that picture")
    }

    LaunchedEffect(ready) {
        if (!ready) return@LaunchedEffect
        PcTools.refreshLook(); PcTools.refreshApps()
        var n = 0
        while (true) {
            PcTools.refreshInfo()
            if (++n % 10 == 0) PcTools.refreshApps()
            delay(1_000)
        }
    }

    LincScaffold(title = info.name.ifEmpty { "PC" }, subtitle = info.os.ifEmpty { null }, onBack = onBack, onSettings = onSettings) { inner ->
        LazyColumn(contentPadding = pagePadding(inner), verticalArrangement = Arrangement.spacedBy(Kit.groupGap)) {
            if (!ready) item {
                HelperText("Connect the PC, and update Linc on it, to use these.", kind = Explanations.Kind.Blocked,
                    modifier = Modifier.padding(horizontal = Kit.gutter))
            }

            // ---- performance ------------------------------------------------------------------
            item { SectionHeader("Performance") }
            item {
                Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = Kit.gutter),
                    horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    for (m in Meter.entries) {
                        if (m == Meter.Gpu && info.gpuPct < 0) continue
                        MeterTile(m, m == meter, summary(m, info), series(m, history).firstOrNull() ?: emptyList(),
                            meterColor(m)) { meter = m }
                    }
                }
            }
            item { PerformanceCard(meter, info, history) }

            // ---- apps -------------------------------------------------------------------------
            item {
                SectionHeader("Running apps") {
                    TextButton(onClick = { PcTools.refreshApps() }, enabled = ready) { Text("Refresh") }
                }
            }
            val list = apps
            if (list == null) item { HelperText("Loading…", modifier = Modifier.padding(horizontal = Kit.gutter)) }
            else {
                val shown = if (showAllApps) list else list.take(6)
                for ((index, app) in shown.withIndex()) item(app.pid) {
                    AppRow(app, index, shown.size, onEnd = { ending = app })
                }
                if (list.size > 6) item {
                    TextButton(onClick = { showAllApps = !showAllApps }, modifier = Modifier.padding(horizontal = Kit.gutter)) {
                        Text(if (showAllApps) "Show fewer" else "Show all ${list.size}")
                    }
                }
            }

            // ---- look -------------------------------------------------------------------------
            item { SectionHeader("Personalise") }
            look.error?.let { e -> item { HelperText(e, kind = Explanations.Kind.Error, modifier = Modifier.padding(horizontal = Kit.gutter)) } }
            item {
                Card {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Icon(LincSymbols.Contrast, contentDescription = null)
                        Spacer(Modifier.width(12.dp))
                        Text("Light or dark", style = MaterialTheme.typography.titleSmall, modifier = Modifier.weight(1f))
                        TextButton(onClick = { PcTools.setLook(appsDark = phoneDark, systemDark = phoneDark) }, enabled = ready) {
                            Text("Match phone")
                        }
                    }
                    Text("Windows", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    ConnectedChoice(listOf(false to "Light", true to "Dark"), look.systemDark, { PcTools.setLook(systemDark = it) }, enabled = ready)
                    Text("Apps", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    ConnectedChoice(listOf(false to "Light", true to "Dark"), look.appsDark, { PcTools.setLook(appsDark = it) }, enabled = ready)
                }
            }
            item {
                Card {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Icon(LincSymbols.Palette, contentDescription = null)
                        Spacer(Modifier.width(12.dp))
                        Text("Accent colour", style = MaterialTheme.typography.titleSmall)
                    }
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
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Icon(LincSymbols.Wallpaper, contentDescription = null)
                        Spacer(Modifier.width(12.dp))
                        Column(Modifier.weight(1f)) {
                            Text("Wallpaper", style = MaterialTheme.typography.titleSmall)
                            look.wallpaper?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                                maxLines = 1, overflow = TextOverflow.Ellipsis) }
                        }
                    }
                    ConnectedChoice(listOf("fill" to "Fill", "fit" to "Fit", "stretch" to "Stretch", "center" to "Centre"), fit, { fit = it }, enabled = ready)
                    Button(onClick = { photo.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) }, enabled = ready,
                        modifier = Modifier.fillMaxWidth()) {
                        Icon(LincSymbols.PhotoLibrary, contentDescription = null)
                        Spacer(Modifier.width(8.dp))
                        Text("Choose a photo")
                    }
                }
            }

            // ---- open a link ------------------------------------------------------------------
            item { SectionHeader("Open on the PC") }
            item {
                Row(Modifier.padding(horizontal = Kit.gutter), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedTextField(value = link, onValueChange = { link = it }, label = { Text("A link") }, singleLine = true, modifier = Modifier.weight(1f),
                        shape = RoundedCornerShape(16.dp))
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

// ---- performance -------------------------------------------------------------------------------

@Composable
private fun meterColor(m: Meter): Color = when (m) {
    Meter.Cpu -> MaterialTheme.colorScheme.primary
    Meter.Memory -> MaterialTheme.colorScheme.tertiary
    Meter.Disk -> MaterialTheme.colorScheme.secondary
    Meter.Network -> MaterialTheme.colorScheme.error
    Meter.Gpu -> MaterialTheme.colorScheme.primary
}

/** Each meter's lines; network has two (receive, then send). */
private fun series(m: Meter, h: PcHistory): List<List<Float>> = when (m) {
    Meter.Cpu -> listOf(h.cpu)
    Meter.Memory -> listOf(h.memory)
    Meter.Disk -> listOf(h.disk)
    Meter.Network -> listOf(h.netRecv, h.netSend)
    Meter.Gpu -> listOf(h.gpu)
}

private fun summary(m: Meter, i: PcInfoState): String = when (m) {
    Meter.Cpu -> if (i.cpuPct >= 0) "${i.cpuPct}%" else "—"
    Meter.Memory -> if (i.memTotalMb > 0) "%.1f/%.0f GB".format(i.memUsedMb / 1024.0, i.memTotalMb / 1024.0) else "—"
    Meter.Disk -> if (i.diskPct >= 0) "${i.diskPct}%" else i.disks.firstOrNull()?.let { (_, free, _) -> "%.0f GB free".format(free) } ?: "—"
    Meter.Network -> if (i.netRecvKbps >= 0) "↓ ${rate(i.netRecvKbps)}" else "—"
    Meter.Gpu -> if (i.gpuPct >= 0) "${i.gpuPct}%" else "—"
}

private fun rate(kbps: Long): String = when {
    kbps < 1000 -> "$kbps Kbps"
    kbps < 1_000_000 -> "%.1f Mbps".format(kbps / 1000.0)
    else -> "%.2f Gbps".format(kbps / 1_000_000.0)
}

@Composable
private fun MeterTile(m: Meter, selected: Boolean, value: String, points: List<Float>, color: Color, onClick: () -> Unit) {
    Surface(
        onClick = onClick,
        shape = RoundedCornerShape(20.dp),
        color = if (selected) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surfaceContainer,
        modifier = Modifier.width(128.dp).semantics { contentDescription = "${m.label}, $value" },
    ) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(m.icon, contentDescription = null, modifier = Modifier.size(18.dp), tint = color)
                Spacer(Modifier.width(6.dp))
                Text(m.label, style = MaterialTheme.typography.labelLarge, fontWeight = FontWeight.SemiBold)
            }
            Text(value, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1)
            Graph(listOf(points), listOf(color), max = if (m == Meter.Network) null else 100f, grid = false,
                modifier = Modifier.fillMaxWidth().height(32.dp))
        }
    }
}

/** The selected meter: a big graph, as Task Manager draws it, with its figures under it. */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun PerformanceCard(m: Meter, i: PcInfoState, h: PcHistory) {
    val color = meterColor(m)
    val lines = series(m, h)
    Surface(shape = RoundedCornerShape(28.dp), color = MaterialTheme.colorScheme.surfaceContainer,
        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter).animateContentSize()) {
        Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.size(40.dp).clip(MaterialShapes.Cookie9Sided.toShape()).background(color.copy(alpha = 0.16f)),
                    contentAlignment = Alignment.Center) { Icon(m.icon, contentDescription = null, tint = color) }
                Spacer(Modifier.width(12.dp))
                Column(Modifier.weight(1f)) {
                    Text(m.label, style = MaterialTheme.typography.titleLarge)
                    val subtitle = when (m) {
                        Meter.Cpu -> i.cpuName
                        Meter.Gpu -> i.gpuName
                        Meter.Network -> i.netName
                        Meter.Disk -> i.disks.joinToString("  ") { it.first }.ifEmpty { null }
                        Meter.Memory -> if (i.memTotalMb > 0) "%.0f GB".format(i.memTotalMb / 1024.0) else null
                    }
                    subtitle?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1, overflow = TextOverflow.Ellipsis) }
                }
                Text(summary(m, i), style = MaterialTheme.typography.titleMedium, color = color, fontWeight = FontWeight.SemiBold)
            }
            Column {
                Row {
                    Text(when (m) { Meter.Network -> "Throughput"; Meter.Memory -> "Memory usage"; Meter.Disk -> "Active time"; else -> "% Utilisation" },
                        style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.weight(1f))
                    Text(if (m == Meter.Network) rate(niceMax(lines.flatten()).toLong()) else "100%",
                        style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                Graph(lines, if (m == Meter.Network) listOf(color, MaterialTheme.colorScheme.tertiary) else listOf(color),
                    max = if (m == Meter.Network) null else 100f, grid = true,
                    modifier = Modifier.fillMaxWidth().height(160.dp).padding(vertical = 4.dp))
                Row {
                    Text("2 minutes", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.weight(1f))
                    Text("now", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            val figures: List<Pair<String, String>> = when (m) {
                Meter.Cpu -> listOfNotNull(
                    "Utilisation" to (if (i.cpuPct >= 0) "${i.cpuPct}%" else "—"),
                    if (i.cpuMhz > 0) "Speed" to "%.2f GHz".format(i.cpuMhz / 1000.0) else null,
                    if (i.processes > 0) "Processes" to "${i.processes}" else null,
                    if (i.handles > 0) "Handles" to "%,d".format(i.handles) else null,
                    if (i.cores > 0) "Cores" to "${i.cores}" else null,
                    if (i.threads > 0) "Logical processors" to "${i.threads}" else null,
                    "Up time" to uptime(i.uptimeMin),
                )
                Meter.Memory -> listOfNotNull(
                    "In use" to "%.1f GB".format(i.memUsedMb / 1024.0),
                    "Available" to "%.1f GB".format((i.memTotalMb - i.memUsedMb) / 1024.0),
                    if (i.memCommitLimitMb > 0) "Committed" to "%.1f/%.1f GB".format(i.memCommittedMb / 1024.0, i.memCommitLimitMb / 1024.0) else null,
                )
                Meter.Disk -> listOfNotNull(
                    if (i.diskPct >= 0) "Active time" to "${i.diskPct}%" else null,
                    if (i.diskReadKBps >= 0) "Read speed" to speed(i.diskReadKBps) else null,
                    if (i.diskWriteKBps >= 0) "Write speed" to speed(i.diskWriteKBps) else null,
                ) + i.disks.map { (name, free, total) -> "Free on $name" to "%.0f of %.0f GB".format(free, total) }
                Meter.Network -> listOf(
                    "Receive" to (if (i.netRecvKbps >= 0) rate(i.netRecvKbps) else "—"),
                    "Send" to (if (i.netSendKbps >= 0) rate(i.netSendKbps) else "—"),
                )
                Meter.Gpu -> listOf("Utilisation" to (if (i.gpuPct >= 0) "${i.gpuPct}%" else "—"))
            }
            FlowRow(horizontalArrangement = Arrangement.spacedBy(20.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                for ((label, value) in figures) Column {
                    Text(label, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    Text(value, style = MaterialTheme.typography.titleMedium)
                }
            }
            if (m == Meter.Network) Row(horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                Legend(color, "Receive"); Legend(MaterialTheme.colorScheme.tertiary, "Send")
            }
            i.battery?.let { (pct, charging) ->
                if (m == Meter.Cpu) Text("Battery $pct%" + if (charging) " · charging" else "", style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}

@Composable
private fun Legend(color: Color, label: String) = Row(verticalAlignment = Alignment.CenterVertically) {
    Box(Modifier.size(10.dp).clip(CircleShape).background(color))
    Spacer(Modifier.width(6.dp))
    Text(label, style = MaterialTheme.typography.labelMedium)
}

private fun speed(kBps: Long): String = when {
    kBps < 1024 -> "$kBps KB/s"
    else -> "%.1f MB/s".format(kBps / 1024.0)
}

/** A rounded top for a graph whose values have no fixed ceiling (network). */
private fun niceMax(values: List<Float>): Float {
    val top = (values.maxOrNull() ?: 0f).coerceAtLeast(100f)
    var step = 100f
    while (step < top) step *= if (step.toString().startsWith("1")) 5f else 2f
    return step
}

/**
 * A Task Manager-style graph: the newest point on the right edge, a filled area under a line,
 * and a light grid. [max] null scales to the data (rounded up).
 */
@Composable
private fun Graph(lines: List<List<Float>>, colors: List<Color>, max: Float?, grid: Boolean, modifier: Modifier = Modifier) {
    val gridColor = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.6f)
    val frame = MaterialTheme.colorScheme.outlineVariant
    Canvas(modifier.clip(RoundedCornerShape(if (grid) 12.dp else 6.dp))) {
        val top = max ?: niceMax(lines.flatten())
        if (grid) {
            for (k in 1 until 4) {
                val y = size.height * k / 4
                drawLine(gridColor, Offset(0f, y), Offset(size.width, y), strokeWidth = 1.dp.toPx())
            }
            for (k in 1 until 6) {
                val x = size.width * k / 6
                drawLine(gridColor, Offset(x, 0f), Offset(x, size.height), strokeWidth = 1.dp.toPx(),
                    pathEffect = PathEffect.dashPathEffect(floatArrayOf(4.dp.toPx(), 4.dp.toPx())))
            }
        }
        val step = size.width / (PcTools.HISTORY_POINTS - 1)
        lines.forEachIndexed { index, points ->
            if (points.size < 2) return@forEachIndexed
            val color = colors.getOrElse(index) { colors.first() }
            val line = Path()
            val start = size.width - (points.size - 1) * step
            points.forEachIndexed { k, v ->
                val x = start + k * step
                val y = size.height - (v / top).coerceIn(0f, 1f) * size.height
                if (k == 0) line.moveTo(x, y) else line.lineTo(x, y)
            }
            if (index == 0) {
                val area = Path().apply {
                    addPath(line)
                    lineTo(size.width, size.height)
                    lineTo(start, size.height)
                    close()
                }
                drawPath(area, Brush.verticalGradient(listOf(color.copy(alpha = 0.35f), color.copy(alpha = 0.04f))))
            }
            drawPath(line, color, style = Stroke(width = (if (grid) 2.dp else 1.5.dp).toPx(), cap = StrokeCap.Round, join = StrokeJoin.Round,
                pathEffect = if (index > 0) PathEffect.dashPathEffect(floatArrayOf(6.dp.toPx(), 4.dp.toPx())) else null))
        }
        if (grid) drawRoundRect(frame, style = Stroke(1.dp.toPx()), cornerRadius = androidx.compose.ui.geometry.CornerRadius(12.dp.toPx()))
    }
}

// ---- apps --------------------------------------------------------------------------------------

@Composable
private fun AppRow(app: PcApp, index: Int, count: Int, onEnd: () -> Unit) {
    Surface(shape = segmentShape(segmentOf(index, count)),
        color = MaterialTheme.colorScheme.surfaceContainer, modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter)) {
        Row(Modifier.padding(start = 16.dp, end = 8.dp, top = 10.dp, bottom = 10.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(36.dp).clip(RoundedCornerShape(10.dp)).background(MaterialTheme.colorScheme.secondaryContainer),
                contentAlignment = Alignment.Center) {
                Text(app.name.take(1).uppercase(), style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.onSecondaryContainer)
            }
            Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                Text(app.title, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text("${app.name} · ${app.memMb} MB", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            TextButton(onClick = onEnd) { Text("End") }
        }
    }
}

// ---- look --------------------------------------------------------------------------------------

/** Windows' own accent choices. */
private val ACCENTS = listOf("#0078D7", "#0063B1", "#E81123", "#FF8C00", "#FFB900", "#10893E", "#00B7C3", "#8E8CD8", "#744DA9", "#E3008C", "#767676")

private fun hex(c: Color): String = "#%06X".format(c.toArgb() and 0xFFFFFF)

@Composable
private fun Card(content: @Composable () -> Unit) {
    Surface(shape = RoundedCornerShape(24.dp), color = MaterialTheme.colorScheme.surfaceContainer,
        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter)) {
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

private fun uptime(minutes: Long): String = when {
    minutes <= 0 -> "—"
    minutes < 60 -> "$minutes min"
    minutes < 60 * 24 -> "${minutes / 60} h ${minutes % 60} min"
    else -> "${minutes / (60 * 24)} d ${minutes / 60 % 24} h"
}
