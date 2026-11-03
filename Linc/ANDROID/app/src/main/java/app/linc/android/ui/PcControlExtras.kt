@file:OptIn(androidx.compose.material3.ExperimentalMaterial3ExpressiveApi::class)

package app.linc.android.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialShapes
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.toShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.linc.android.service.PcNote
import app.linc.android.service.PcQuick
import app.linc.android.service.PcQuickItem
import app.linc.android.service.PcTools
import app.linc.android.ui.kit.ConnectedChoice
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.SectionHeader
import app.linc.android.ui.kit.SettingsGroup
import app.linc.android.ui.kit.SettingsRow
import kotlinx.coroutines.delay

/*
 * Home › Control, reaching into the PC (2026-11-03). The owner: "on phone add a button under
 * Control in Home that gives settings like dark theme or light theme for PC; then below that a
 * section for notifications from the PC (not the ones from the phone), and a section for those
 * quick tiles from the PC and its quick settings too" — "make the user feel as if their phone
 * really is here". The PC sends the tiles and the notifications; see PcQuick.
 */

/**
 * While Home shows a connected PC: ask for its tiles (again every ten seconds, since Wi-Fi or
 * the hotspot also change at the PC) and watch its notifications if this phone's switch is on.
 * Stops watching when Home goes away, so the PC sends nothing nobody is looking at.
 */
@Composable
fun PcControlExtrasEffects(connectionKey: Any?) {
    val context = LocalContext.current
    if (connectionKey == null || !PcQuick.ready()) return
    LaunchedEffect(connectionKey) {
        PcQuick.reset()
        PcTools.refreshLook()
        if (PcQuick.notesEnabled(context)) PcQuick.watch(true)
        while (true) {
            PcQuick.refresh()
            delay(10_000)
        }
    }
    DisposableEffect(connectionKey) { onDispose { PcQuick.watch(false) } }
}

/** True once the PC has had five seconds to answer and hasn't: it's older than these messages. */
@Composable
private fun pcTooOld(connectionKey: Any?): Boolean {
    val items by PcQuick.items.collectAsState()
    var waited by remember(connectionKey) { mutableStateOf(false) }
    LaunchedEffect(connectionKey) { delay(5_000); waited = true }
    return waited && items == null
}

/** The button under Control: the PC's settings, in a sheet. */
@Composable
fun PcSettingsButton(onClick: () -> Unit) {
    if (!PcQuick.ready()) return
    Surface(
        onClick = onClick,
        shape = RoundedCornerShape(24.dp),
        color = MaterialTheme.colorScheme.secondaryContainer,
        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter),
    ) {
        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Box(Modifier.size(40.dp).clip(MaterialShapes.Cookie6Sided.toShape()).background(MaterialTheme.colorScheme.primary), contentAlignment = Alignment.Center) {
                Icon(LincSymbols.Tune, contentDescription = null, tint = MaterialTheme.colorScheme.onPrimary)
            }
            Column(Modifier.weight(1f)) {
                Text("PC settings", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                Text("Dark or light theme, night light, Wi-Fi, Bluetooth, Do not disturb, hotspot",
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSecondaryContainer.copy(alpha = 0.8f),
                    maxLines = 2, overflow = TextOverflow.Ellipsis)
            }
            Icon(LincSymbols.ChevronRight, contentDescription = null)
        }
    }
}

/** The sheet behind [PcSettingsButton]: the PC's theme, then its quick settings as switches. */
@Composable
fun PcSettingsSheet(connectionKey: Any?) {
    val look by PcTools.look.collectAsState()
    val items by PcQuick.items.collectAsState()
    val error by PcQuick.error.collectAsState()
    val tooOld = pcTooOld(connectionKey)
    LaunchedEffect(Unit) { PcTools.refreshLook(); PcQuick.refresh() }

    Text("Your PC's settings", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold,
        modifier = Modifier.padding(horizontal = Kit.gutter))
    SectionHeader("Theme")
    Column(Modifier.padding(horizontal = Kit.gutter)) {
        // Apps and Windows together: "dark theme or light theme for PC" means the whole PC.
        ConnectedChoice(
            options = listOf(false to "Light", true to "Dark"),
            selected = look.appsDark,
            onSelect = { dark -> PcTools.setLook(appsDark = dark, systemDark = dark); PcQuick.refresh() },
            enabled = look.known,
        )
    }
    val settings = items.orEmpty().filter { it.group == "setting" && it.id != "dark" }
    if (settings.isNotEmpty()) {
        SectionHeader("Quick settings")
        SettingsGroup(rows = settings.map { item ->
            { s ->
                SettingsRow(item.label, s, iconFor(item.id),
                    supporting = item.note ?: if (!item.available) "Not available on this PC" else null,
                    enabled = item.available,
                    trailing = { Switch(item.on == true, { PcQuick.set(item.id, it) }, enabled = item.available) })
            }
        })
    } else if (tooOld) {
        Text("Update Linc on the PC for night light, Wi-Fi, Bluetooth and the rest.",
            style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(horizontal = Kit.gutter))
    }
    error?.let { ErrorLine(it) }
}

/** "From your PC": the PC's own notifications, newest first, each with a dismiss. */
@Composable
fun PcNotesSection(connectionKey: Any?) {
    val context = LocalContext.current
    if (!PcQuick.ready() || !PcQuick.notesEnabled(context)) return
    val notes by PcQuick.notes.collectAsState()
    val tooOld = pcTooOld(connectionKey)
    if (!notes.known && !tooOld) return
    var expanded by remember { mutableStateOf(false) }

    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        SectionHeader("From your PC") {
            if (notes.items.size > 1) TextButton(onClick = { PcQuick.dismissAll() }) { Text("Clear all") }
        }
        when {
            !notes.known -> Hint("Update Linc on the PC to see its notifications here.")
            !notes.enabled -> Hint("Turned off on the PC: Linc › Settings › \"Send this PC's notifications to the phone\".")
            notes.access == "ask" -> Hint("Windows needs your OK first.", action = "Ask on the PC") { PcQuick.watch(true, ask = true) }
            notes.access == "denied" -> Hint("Windows said no. On the PC: Settings › Privacy & security › Notifications, allow Linc, then try again.",
                action = "Try again") { PcQuick.watch(true, ask = true) }
            notes.access != "allowed" -> Hint(notes.error ?: "This PC can't share its notifications.")
            notes.items.isEmpty() -> Hint("No notifications on the PC.")
            else -> {
                val shown = if (expanded) notes.items else notes.items.take(3)
                shown.forEach { NoteCard(it) }
                if (notes.items.size > 3) {
                    TextButton(onClick = { expanded = !expanded }, modifier = Modifier.padding(horizontal = Kit.gutter)) {
                        Text(if (expanded) "Show fewer" else "Show all ${notes.items.size}")
                    }
                }
            }
        }
    }
}

@Composable
private fun NoteCard(note: PcNote) {
    Surface(
        shape = RoundedCornerShape(20.dp),
        color = MaterialTheme.colorScheme.surfaceContainer,
        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter),
    ) {
        Row(Modifier.padding(start = 16.dp, top = 12.dp, bottom = 12.dp, end = 4.dp), verticalAlignment = Alignment.Top) {
            Icon(LincSymbols.DesktopWindows, contentDescription = null, tint = MaterialTheme.colorScheme.primary,
                modifier = Modifier.padding(top = 2.dp).size(20.dp))
            Column(Modifier.weight(1f).padding(start = 12.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(listOfNotNull(note.app.ifBlank { null }, ago(note.time)).joinToString(" · "),
                    style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1)
                if (note.title.isNotBlank()) Text(note.title, style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.SemiBold,
                    maxLines = 2, overflow = TextOverflow.Ellipsis)
                if (note.text.isNotBlank()) Text(note.text, style = MaterialTheme.typography.bodyMedium, maxLines = 4, overflow = TextOverflow.Ellipsis)
            }
            IconButton(onClick = { PcQuick.dismiss(note.id) }) {
                Icon(LincSymbols.Close, contentDescription = "Dismiss on the PC")
            }
        }
    }
}

/** "PC quick tiles": Windows' quick settings, then Linc's PC shortcuts; tap runs it on the PC. */
@Composable
fun PcTilesSection() {
    val items by PcQuick.items.collectAsState()
    val error by PcQuick.error.collectAsState()
    val tiles = items ?: return
    if (tiles.isEmpty()) return
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        SectionHeader("PC quick tiles")
        Column(Modifier.padding(horizontal = Kit.gutter), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            tiles.sortedBy { if (it.group == "setting") 0 else 1 }.chunked(3).forEach { row ->
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    row.forEach { QuickTile(it, Modifier.weight(1f)) }
                    repeat(3 - row.size) { Spacer(Modifier.weight(1f)) }
                }
            }
        }
        error?.let { ErrorLine(it) }
    }
}

@Composable
private fun QuickTile(item: PcQuickItem, modifier: Modifier) {
    val on = item.kind == "toggle" && item.on == true
    Surface(
        onClick = { PcQuick.set(item.id) },
        enabled = item.available,
        shape = RoundedCornerShape(24.dp),
        color = if (on) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceContainer,
        modifier = modifier,
    ) {
        Column(
            Modifier.padding(vertical = 14.dp, horizontal = 6.dp).alpha(if (item.available) 1f else 0.4f),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            Box(
                Modifier.size(44.dp).clip(if (item.group == "setting") MaterialShapes.Circle.toShape() else MaterialShapes.Cookie4Sided.toShape())
                    .background(if (on) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.secondaryContainer),
                contentAlignment = Alignment.Center,
            ) {
                Icon(iconFor(item.id), contentDescription = null,
                    tint = if (on) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSecondaryContainer)
            }
            Text(item.label, style = MaterialTheme.typography.labelLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
            val state = when {
                !item.available -> item.note ?: "Not here"
                item.kind == "toggle" -> item.note?.takeIf { on } ?: if (on) "On" else "Off"
                else -> null
            }
            if (state != null) Text(state, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }
}

@Composable
private fun Hint(text: String, action: String? = null, onAction: () -> Unit = {}) {
    Surface(
        shape = RoundedCornerShape(20.dp),
        color = MaterialTheme.colorScheme.surfaceContainer,
        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter),
    ) {
        Row(Modifier.padding(horizontal = 16.dp, vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(text, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.weight(1f))
            if (action != null) TextButton(onClick = onAction) { Text(action) }
        }
    }
}

@Composable
private fun ErrorLine(code: String) {
    val text = when (code) {
        "denied" -> "Windows refused that."
        "unavailable" -> "This PC can't do that."
        "unknown" -> "The PC didn't know that one."
        else -> code
    }
    Text(text, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error,
        modifier = Modifier.padding(horizontal = Kit.gutter + 4.dp))
}

private fun iconFor(id: String): ImageVector = when (id) {
    "dark" -> LincSymbols.DarkMode
    "nightlight" -> LincSymbols.Bedtime
    "wifi" -> LincSymbols.Wifi
    "bluetooth" -> LincSymbols.Headphones
    "dnd" -> LincSymbols.Notifications
    "hotspot" -> LincSymbols.Hotspot
    "lock" -> LincSymbols.Lock
    "desktop" -> LincSymbols.DesktopWindows
    "snip" -> LincSymbols.Image
    "taskview" -> LincSymbols.TaskView
    "clipboard" -> LincSymbols.Clipboard
    "openlinc" -> LincSymbols.OpenInNew
    else -> LincSymbols.Tune
}

private fun ago(time: Long): String? {
    if (time <= 0) return null
    val minutes = (System.currentTimeMillis() - time) / 60_000
    return when {
        minutes < 1 -> "now"
        minutes < 60 -> "${minutes} min"
        minutes < 24 * 60 -> "${minutes / 60} h"
        else -> "${minutes / (24 * 60)} d"
    }
}
