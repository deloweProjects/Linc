@file:OptIn(androidx.compose.material3.ExperimentalMaterial3ExpressiveApi::class, androidx.compose.material3.ExperimentalMaterial3Api::class)

package app.linc.android.ui

import android.text.format.DateFormat
import androidx.compose.animation.animateContentSize
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import app.linc.android.service.ClipHistory
import app.linc.android.service.ClipItem
import app.linc.android.service.ClipboardBridge
import app.linc.android.service.ClipboardHistoryStore
import app.linc.android.service.ClipboardSettings
import app.linc.android.service.CompanionStateHolder
import app.linc.android.service.CompanionStateHolder.ServiceState
import app.linc.android.service.Explanations
import app.linc.android.ui.kit.EmptyState
import app.linc.android.ui.kit.HelperText
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.LincScaffold
import app.linc.android.ui.kit.SectionHeader
import app.linc.android.ui.kit.pagePadding
import app.linc.android.ui.kit.rememberNotify
import app.linc.android.ui.kit.segmentColor
import app.linc.android.ui.kit.segmentOf
import app.linc.android.ui.kit.segmentShape
import app.linc.android.ui.theme.LincTheme

/**
 * Clipboard (1.0.0.11, D-083): send text to the PC's clipboard, Sync now, and a large searchable
 * history grouped by day — pinned first — with Copy here / Send to PC / Pin / Delete per item.
 */
@Composable
fun ClipboardScreen(onSettings: (() -> Unit)? = null) {
    val context = LocalContext.current
    val items by ClipboardHistoryStore.items.collectAsState()
    val connection by CompanionStateHolder.state.collectAsState()
    ClipboardContent(
        items = items,
        connected = connection is ServiceState.Connected,
        keepHistory = ClipboardSettings.keepHistory(context),
        onSettings = onSettings,
    )
}

@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
private fun ClipboardContent(
    items: List<ClipItem>,
    connected: Boolean,
    keepHistory: Boolean,
    onSettings: (() -> Unit)?,
) {
    val context = LocalContext.current
    var draft by rememberSaveable { mutableStateOf("") }
    var query by rememberSaveable { mutableStateOf("") }
    var expanded by remember { mutableStateOf<Long?>(null) }
    var confirmClear by remember { mutableStateOf(false) }
    // The owner: search first, and sending text to the PC behind a button, not a card on top.
    var composing by remember { mutableStateOf(false) }

    LincScaffold(
        title = "Clipboard",
        onSettings = onSettings,
        actions = {
            IconButton(onClick = { ClipboardBridge.syncNow(context) }, enabled = connected) {
                Icon(LincSymbols.Sync, contentDescription = "Sync now")
            }
            IconButton(onClick = { confirmClear = true }, enabled = items.isNotEmpty()) {
                Icon(LincSymbols.ClearAll, contentDescription = "Clear history")
            }
        },
        floatingActionButton = {
            androidx.compose.material3.ExtendedFloatingActionButton(
                onClick = { composing = true },
                icon = { Icon(LincSymbols.Send, contentDescription = null) },
                text = { Text("Send to PC") },
            )
        },
    ) { inner ->
        val notify = rememberNotify()
        val shown = ClipHistory.search(items, query)
        if (composing) {
            androidx.compose.material3.ModalBottomSheet(onDismissRequest = { composing = false }) {
                Column(Modifier.padding(bottom = 24.dp)) {
                    Composer(
                        text = draft,
                        onText = { draft = it },
                        connected = connected,
                        onSend = {
                            if (ClipboardBridge.sendToPc(context, draft.trim())) {
                                notify("Sent — it's on your PC's clipboard")
                                draft = ""
                                composing = false
                            } else {
                                notify("Your PC isn't connected")
                            }
                        },
                        onSyncNow = {
                            ClipboardBridge.syncNow(context)
                            notify(if (connected) "Synced with your PC" else "Your PC isn't connected")
                        },
                    )
                }
            }
        }
        LazyColumn(contentPadding = pagePadding(inner)) {
            if (items.isNotEmpty()) {
                item("search") {
                    OutlinedTextField(
                        value = query,
                        onValueChange = { query = it },
                        singleLine = true,
                        leadingIcon = { Icon(LincSymbols.Search, contentDescription = null) },
                        placeholder = { Text("Search the history") },
                        shape = CircleShape,
                        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter, vertical = 8.dp),
                    )
                }
            }
            item("capture") {
                HelperText(
                    "Android lets Linc read what you copy only while Linc is open. Copies made in other apps reach the PC when you open Linc, or when you share the text to Linc.",
                    modifier = Modifier.padding(horizontal = Kit.gutter, vertical = 8.dp),
                )
            }
            if (!keepHistory) {
                item("off") {
                    HelperText(
                        "History is off: clips sync but aren't kept. Turn it on in Settings → Clipboard.",
                        kind = Explanations.Kind.Warning,
                        modifier = Modifier.padding(horizontal = Kit.gutter, vertical = 8.dp),
                    )
                }
            }
            if (shown.isEmpty()) {
                item("empty") {
                    if (items.isEmpty()) {
                        EmptyState("Nothing copied yet", "Text you copy on either device shows up here.", LincSymbols.Clipboard)
                    } else {
                        EmptyState("No matches", "Nothing in the history contains \"${query.trim()}\".", LincSymbols.Search)
                    }
                }
            }
            ClipHistory.groups(shown).forEach { (label, group) ->
                item("h-$label") { SectionHeader(label) }
                group.forEachIndexed { index, clip ->
                    item(clip.id) {
                        ClipRow(
                            clip = clip,
                            segmentIndex = index,
                            segmentCount = group.size,
                            expanded = expanded == clip.id,
                            connected = connected,
                            onToggle = { expanded = if (expanded == clip.id) null else clip.id },
                            onCopy = { ClipboardBridge.copyHere(context, clip.text); notify("Copied to this phone") },
                            onSend = {
                                if (ClipboardBridge.sendToPc(context, clip.text)) notify("Sent to your PC's clipboard")
                                else notify("Your PC isn't connected")
                            },
                            onPin = { ClipboardHistoryStore.pin(clip.id, !clip.pinned) },
                            onDelete = { ClipboardHistoryStore.delete(clip.id); notify("Deleted") },
                        )
                    }
                }
            }
        }
    }

    if (confirmClear) {
        AlertDialog(
            onDismissRequest = { confirmClear = false },
            icon = { Icon(LincSymbols.ClearAll, contentDescription = null) },
            title = { Text("Clear the clipboard history?") },
            text = { Text("Everything except pinned items is deleted from this phone. Your PC keeps its own history.") },
            confirmButton = { TextButton(onClick = { ClipboardHistoryStore.clear(); confirmClear = false }) { Text("Clear") } },
            dismissButton = { TextButton(onClick = { confirmClear = false }) { Text("Cancel") } },
        )
    }
}

@Composable
private fun Composer(text: String, onText: (String) -> Unit, connected: Boolean, onSend: () -> Unit, onSyncNow: () -> Unit) {
    Surface(
        shape = RoundedCornerShape(Kit.heroRadius),
        color = MaterialTheme.colorScheme.surfaceContainerHigh,
        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text("Send text to your PC", style = MaterialTheme.typography.titleMedium)
            OutlinedTextField(
                value = text,
                onValueChange = onText,
                minLines = 2,
                maxLines = 6,
                placeholder = { Text("Type or paste — it lands on the PC's clipboard") },
                modifier = Modifier.fillMaxWidth(),
            )
            if (!connected) {
                HelperText("Connect to your PC to send. The history below still works.", kind = Explanations.Kind.Blocked)
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                FilledTonalButton(onClick = onSyncNow, shapes = ButtonDefaults.shapes(), modifier = Modifier.heightIn(min = Kit.touch)) {
                    Icon(LincSymbols.Sync, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(8.dp))
                    Text("Sync now")
                }
                Spacer(Modifier.weight(1f))
                Button(
                    onClick = onSend,
                    enabled = connected && text.isNotBlank(),
                    shapes = ButtonDefaults.shapes(),
                    modifier = Modifier.heightIn(min = Kit.touch),
                ) {
                    Icon(LincSymbols.Send, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(8.dp))
                    Text("Send")
                }
            }
        }
    }
}

@Composable
private fun ClipRow(
    clip: ClipItem,
    segmentIndex: Int,
    segmentCount: Int,
    expanded: Boolean,
    connected: Boolean,
    onToggle: () -> Unit,
    onCopy: () -> Unit,
    onSend: () -> Unit,
    onPin: () -> Unit,
    onDelete: () -> Unit,
) {
    val context = LocalContext.current
    val time = remember(clip.at) { DateFormat.getTimeFormat(context).format(java.util.Date(clip.at)) }
    Surface(
        shape = segmentShape(segmentOf(segmentIndex, segmentCount)),
        color = segmentColor(),
        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter, vertical = 1.dp),
    ) {
        Column(
            Modifier.clickable(onClick = onToggle).animateContentSize(MaterialTheme.motionScheme.defaultSpatialSpec()).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.Top) {
                Box(
                    Modifier.size(36.dp).clip(CircleShape).background(MaterialTheme.colorScheme.secondaryContainer),
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(
                        if (clip.fromPhone) LincSymbols.Smartphone else LincSymbols.Computer,
                        contentDescription = if (clip.fromPhone) "From this phone" else "From your PC",
                        tint = MaterialTheme.colorScheme.onSecondaryContainer,
                        modifier = Modifier.size(18.dp),
                    )
                }
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text(
                        clip.text,
                        style = MaterialTheme.typography.bodyLarge,
                        maxLines = if (expanded) Int.MAX_VALUE else 3,
                        overflow = TextOverflow.Ellipsis,
                    )
                    Text(
                        buildString {
                            append(if (clip.fromPhone) "This phone" else "PC")
                            append(" · ").append(time)
                            if (clip.sent) append(" · Sent")
                        },
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                if (clip.pinned) Icon(LincSymbols.Pin, contentDescription = "Pinned", tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(18.dp))
            }
            if (expanded) {
                Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    ClipAction(LincSymbols.Copy, "Copy here", true, onCopy)
                    ClipAction(LincSymbols.Send, "Send to PC", connected, onSend)
                    ClipAction(LincSymbols.Pin, if (clip.pinned) "Unpin" else "Pin", true, onPin)
                    ClipAction(LincSymbols.Delete, "Delete", true, onDelete)
                }
            }
        }
    }
}

@Composable
private fun androidx.compose.foundation.layout.RowScope.ClipAction(icon: androidx.compose.ui.graphics.vector.ImageVector, label: String, enabled: Boolean, onClick: () -> Unit) {
    TextButton(onClick = onClick, enabled = enabled, modifier = Modifier.weight(1f).heightIn(min = Kit.touch)) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Icon(icon, contentDescription = null, modifier = Modifier.size(20.dp))
            Text(label, style = MaterialTheme.typography.labelMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }
}

// ---- previews --------------------------------------------------------------------------------

private val sample = run {
    val now = System.currentTimeMillis()
    listOf(
        ClipItem(1, "https://m3.material.io/blog/building-with-m3-expressive", false, now - 60_000),
        ClipItem(2, "Meeting moved to 3 pm — room 2.14", true, now - 3_600_000, sent = true),
        ClipItem(3, "The quick brown fox jumps over the lazy dog. ".repeat(5), true, now - 90_000_000, pinned = true),
    )
}

@Preview(name = "Clipboard — light", widthDp = 360, heightDp = 900)
@Composable
private fun ClipboardPreviewLight() = LincTheme(darkTheme = false) { ClipboardContent(sample, connected = true, keepHistory = true, onSettings = {}) }

@Preview(name = "Clipboard — dark", widthDp = 411, heightDp = 900)
@Composable
private fun ClipboardPreviewDark() = LincTheme(darkTheme = true) { ClipboardContent(sample, connected = true, keepHistory = true, onSettings = {}) }

@Preview(name = "Clipboard — empty, not connected", widthDp = 360, heightDp = 700)
@Composable
private fun ClipboardPreviewEmpty() = LincTheme { ClipboardContent(emptyList(), connected = false, keepHistory = true, onSettings = {}) }

@Preview(name = "Clipboard — large font", widthDp = 360, heightDp = 900, fontScale = 2f)
@Composable
private fun ClipboardPreviewLargeFont() = LincTheme { ClipboardContent(sample, connected = true, keepHistory = true, onSettings = {}) }
