package app.linc.android.ui

import android.os.Environment
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
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
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.linc.android.service.CompanionStateHolder
import app.linc.android.service.FolderPair
import app.linc.android.service.PcTools
import app.linc.android.service.SyncRemote
import app.linc.android.service.SyncStore
import app.linc.android.ui.kit.BlockedState
import app.linc.android.ui.kit.ConnectedChoice
import app.linc.android.ui.kit.EmptyState
import app.linc.android.ui.kit.HelperText
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.LincScaffold
import app.linc.android.ui.kit.SectionHeader
import app.linc.android.ui.kit.SettingsGroup
import app.linc.android.ui.kit.SettingsRow
import app.linc.android.ui.kit.pagePadding
import java.io.File

/*
 * Control → Sync (v30): the PC's Sync page, from the phone. What syncs (the lanes), then the
 * folders kept the same on both — each a PC folder and a phone folder, both ways or one way.
 * The PC runs the engine and keeps the setup; this page edits it and shows what the PC says.
 */

private data class Lane(val key: String, val title: String, val hint: String, val icon: ImageVector)

private val lanes = listOf(
    Lane("folders", "Folders", "Folders you choose, kept the same", LincSymbols.Folder),
    Lane("photos", "Photos", "New camera photos copy to the PC", LincSymbols.PhotoLibrary),
    Lane("messages", "Messages", "Read and reply to texts on the PC", LincSymbols.Sms),
    Lane("calls", "Calls", "Call log and dialling on the PC", LincSymbols.Call),
)

private val directions = listOf(
    FolderPair.BOTH to "Both ways",
    FolderPair.TO_PHONE to "PC → phone",
    FolderPair.TO_PC to "Phone → PC",
)

private fun directionText(direction: String) = when (direction) {
    FolderPair.TO_PHONE -> "PC → phone · this phone gets a copy"
    FolderPair.TO_PC -> "Phone → PC · the PC gets a copy"
    else -> "Both ways · the newer copy wins"
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun SyncScreen(onBack: (() -> Unit)? = null, onSettings: (() -> Unit)? = null) {
    val context = LocalContext.current
    val connection by CompanionStateHolder.state.collectAsState()
    val setup by SyncRemote.setup.collectAsState()
    val ready = SyncRemote.available()
    LaunchedEffect(ready, connection) { if (ready) SyncRemote.refresh() }
    var editing by remember { mutableStateOf<FolderPair?>(null) }
    var adding by remember { mutableStateOf(false) }

    LincScaffold(title = "Sync", subtitle = (connection as? CompanionStateHolder.ServiceState.Connected)?.host, onBack = onBack, onSettings = onSettings) { inner ->
        if (!ready) {
            BlockedState(
                title = "Sync is set up with the PC",
                reason = "Connect to a PC running the newest Linc to choose what syncs and which folders stay the same.",
                icon = LincSymbols.Sync,
                modifier = Modifier.padding(top = inner.calculateTopPadding() + 24.dp),
            )
            return@LincScaffold
        }
        fun laneOn(key: String) = if (setup.loaded) setup.lanes[key] == true else SyncStore.isOn(context, key)
        LazyColumn(contentPadding = pagePadding(inner), verticalArrangement = Arrangement.spacedBy(Kit.segmentGap)) {
            item { SectionHeader("What syncs") }
            item {
                SettingsGroup(rows = lanes.map { lane ->
                    val row: @Composable (app.linc.android.ui.kit.Segment) -> Unit = { seg ->
                        SettingsRow(
                            title = lane.title, segment = seg, icon = lane.icon, supporting = lane.hint,
                            onClick = { SyncRemote.setLane(context, lane.key, !laneOn(lane.key)) },
                            trailing = { Switch(checked = laneOn(lane.key), onCheckedChange = { SyncRemote.setLane(context, lane.key, it) }) },
                        )
                    }
                    row
                })
            }

            item {
                SectionHeader("Folders") {
                    TextButton(onClick = { adding = true }, enabled = laneOn("folders")) {
                        Icon(LincSymbols.Add, contentDescription = null, modifier = Modifier.size(18.dp))
                        Text("Add folder", modifier = Modifier.padding(start = 6.dp))
                    }
                }
            }
            when {
                !laneOn("folders") -> item {
                    HelperText("Turn on Folders above, then add the folders to keep the same on this phone and the PC.",
                        modifier = Modifier.padding(horizontal = Kit.gutter))
                }
                setup.pairs.isEmpty() -> item {
                    EmptyState("No folders yet", "Add one — Documents, Music, a work folder — and it stays the same on both.", LincSymbols.Folder)
                }
                else -> items(setup.pairs, key = { it.id }) { pair ->
                    PairCard(pair, onOpen = { editing = pair },
                        onEnabled = { SyncRemote.savePair(pair.copy(enabled = it)) },
                        modifier = Modifier.padding(horizontal = Kit.gutter, vertical = 4.dp))
                }
            }

            if (laneOn("photos") && setup.photosPcPath.isNotBlank()) {
                item {
                    Surface(shape = RoundedCornerShape(20.dp), color = MaterialTheme.colorScheme.surfaceContainer,
                        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter, vertical = 8.dp)) {
                        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                            Icon(LincSymbols.PhotoLibrary, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
                            Text("New camera photos copy to ${setup.photosPcPath} on the PC.", style = MaterialTheme.typography.bodyMedium)
                        }
                    }
                }
            }

            if (laneOn("folders") || laneOn("photos")) {
                item {
                    Row(Modifier.fillMaxWidth().padding(horizontal = Kit.gutter, vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text(setup.status.ifBlank { "Syncs by itself while connected." }, style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.weight(1f))
                        FilledTonalButton(onClick = { SyncRemote.syncNow() }, shapes = ButtonDefaults.shapes()) {
                            Icon(LincSymbols.Sync, contentDescription = null, modifier = Modifier.size(18.dp))
                            Text("Sync now", modifier = Modifier.padding(start = 6.dp))
                        }
                    }
                }
                item {
                    HelperText("Deletes are never copied, so nothing is lost by accident. Top-level files only.",
                        modifier = Modifier.padding(horizontal = Kit.gutter))
                }
            }
        }
    }

    if (adding || editing != null) {
        PairEditor(
            existing = editing,
            onDismiss = { adding = false; editing = null },
            onSave = { SyncRemote.savePair(it); adding = false; editing = null },
            onRemove = { SyncRemote.removePair(it.id); editing = null },
        )
    }
}

@Composable
private fun PairCard(pair: FolderPair, onOpen: () -> Unit, onEnabled: (Boolean) -> Unit, modifier: Modifier = Modifier) {
    Surface(shape = RoundedCornerShape(24.dp), color = MaterialTheme.colorScheme.surfaceContainer, modifier = modifier.fillMaxWidth()) {
        Column(Modifier.clickable(onClick = onOpen).padding(16.dp).alpha(if (pair.enabled) 1f else 0.55f),
            verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.size(40.dp).clip(RoundedCornerShape(12.dp)).background(MaterialTheme.colorScheme.primaryContainer),
                    contentAlignment = Alignment.Center) {
                    Icon(LincSymbols.Folder, contentDescription = null, tint = MaterialTheme.colorScheme.onPrimaryContainer)
                }
                Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                    Text(pair.name, style = MaterialTheme.typography.titleMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(directionText(pair.direction), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                Switch(checked = pair.enabled, onCheckedChange = onEnabled)
            }
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                PathChip(LincSymbols.DesktopWindows, pair.pcPath, Modifier.weight(1f))
                Icon(
                    when (pair.direction) {
                        FolderPair.TO_PHONE -> LincSymbols.ArrowRight
                        FolderPair.TO_PC -> LincSymbols.ArrowLeft
                        else -> LincSymbols.Sync
                    },
                    contentDescription = directionText(pair.direction), tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(20.dp),
                )
                PathChip(LincSymbols.Smartphone, pair.phonePath, Modifier.weight(1f))
            }
        }
    }
}

@Composable
private fun PathChip(icon: ImageVector, path: String, modifier: Modifier = Modifier) {
    Surface(shape = RoundedCornerShape(14.dp), color = MaterialTheme.colorScheme.surfaceContainerHigh, modifier = modifier) {
        Row(Modifier.padding(horizontal = 10.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(icon, contentDescription = null, modifier = Modifier.size(16.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
            Text(shortPath(path), style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis,
                modifier = Modifier.padding(start = 6.dp))
        }
    }
}

/** The end of a path says which folder it is; the start is the same drive every time. */
internal fun shortPath(path: String): String {
    val sep = if ('\\' in path) '\\' else '/'
    val parts = path.trimEnd('\\', '/').split('\\', '/').filter { it.isNotEmpty() }
    return if (parts.size <= 3) path else "…" + sep + parts.takeLast(2).joinToString(sep.toString())
}

/** Add or edit one pair: its name, the PC folder, this phone's folder and which way files go. */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun PairEditor(existing: FolderPair?, onDismiss: () -> Unit, onSave: (FolderPair) -> Unit, onRemove: (FolderPair) -> Unit) {
    var name by remember { mutableStateOf(existing?.name ?: "") }
    var pcPath by remember { mutableStateOf(existing?.pcPath ?: "") }
    var phonePath by remember { mutableStateOf(existing?.phonePath ?: "/sdcard/Download") }
    var direction by remember { mutableStateOf(existing?.direction ?: FolderPair.BOTH) }
    var pickPc by remember { mutableStateOf(false) }
    var pickPhone by remember { mutableStateOf(false) }

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)) {
        Column(Modifier.fillMaxWidth().padding(horizontal = Kit.gutter).padding(bottom = 24.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            Text(if (existing == null) "Add a folder to sync" else "Edit ${existing.name}", style = MaterialTheme.typography.headlineSmall)
            OutlinedTextField(value = name, onValueChange = { name = it }, label = { Text("Name") }, singleLine = true,
                placeholder = { Text(if (pcPath.isBlank()) "Uses the PC folder's name" else FolderPair.nameFor(pcPath)) },
                modifier = Modifier.fillMaxWidth())
            FolderField("Folder on the PC", pcPath, LincSymbols.DesktopWindows, "Choose a folder on the PC") { pickPc = true }
            FolderField("Folder on this phone", phonePath, LincSymbols.Smartphone, "Choose a folder on this phone") { pickPhone = true }
            Text("Which way", style = MaterialTheme.typography.labelLarge)
            ConnectedChoice(options = directions, selected = direction, onSelect = { direction = it })
            HelperText(when (direction) {
                FolderPair.TO_PHONE -> "The PC's files are copied here. Changes made on this phone stay on the phone."
                FolderPair.TO_PC -> "This phone's files are copied to the PC. Changes made on the PC stay on the PC."
                else -> "New and changed files copy both ways; when both changed, the newer one wins."
            })
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                if (existing != null) {
                    OutlinedButton(onClick = { onRemove(existing) }, shapes = ButtonDefaults.shapes()) {
                        Icon(LincSymbols.Delete, contentDescription = null, modifier = Modifier.size(18.dp))
                        Text("Stop syncing", modifier = Modifier.padding(start = 6.dp))
                    }
                }
                Box(Modifier.weight(1f))
                Button(
                    onClick = {
                        onSave(FolderPair(
                            id = existing?.id ?: FolderPair.newId(),
                            name = name.trim().ifBlank { FolderPair.nameFor(pcPath) },
                            pcPath = pcPath.trim(),
                            phonePath = phonePath.trim(),
                            direction = direction,
                            enabled = existing?.enabled ?: true,
                        ))
                    },
                    enabled = pcPath.isNotBlank() && phonePath.isNotBlank(),
                    shapes = ButtonDefaults.shapes(),
                    modifier = Modifier.heightIn(min = Kit.touch),
                ) { Text(if (existing == null) "Add" else "Save") }
            }
        }
    }

    if (pickPc) PcFolderPicker(start = pcPath, onDismiss = { pickPc = false }, onPick = { pcPath = it; pickPc = false })
    if (pickPhone) PhoneFolderPicker(start = phonePath, onDismiss = { pickPhone = false }, onPick = { phonePath = it; pickPhone = false })
}

@Composable
private fun FolderField(label: String, path: String, icon: ImageVector, empty: String, onClick: () -> Unit) {
    Surface(shape = RoundedCornerShape(16.dp), color = MaterialTheme.colorScheme.surfaceContainerHigh, modifier = Modifier.fillMaxWidth()) {
        Row(Modifier.clickable(onClick = onClick).padding(horizontal = 16.dp, vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
            Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                Text(label, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Text(path.ifBlank { empty }, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            Icon(LincSymbols.ChevronRight, contentDescription = label)
        }
    }
}

/** A folder list to walk through: Up, the folders inside, and "Use this folder". */
@Composable
private fun FolderBrowser(
    title: String,
    path: String,
    folders: List<Pair<String, String>>,
    note: String?,
    canUse: Boolean,
    onUp: (() -> Unit)?,
    onOpen: (String) -> Unit,
    onUse: () -> Unit,
    onDismiss: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    IconButton(onClick = { onUp?.invoke() }, enabled = onUp != null) { Icon(LincSymbols.ArrowUp, contentDescription = "Up") }
                    Text(path.ifBlank { "This PC" }, style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                HorizontalDivider()
                LazyColumn(Modifier.heightIn(max = 360.dp)) {
                    if (note != null) item { Text(note, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(12.dp)) }
                    items(folders, key = { it.second }) { (label, full) ->
                        Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).clickable { onOpen(full) }.padding(12.dp),
                            verticalAlignment = Alignment.CenterVertically) {
                            Icon(LincSymbols.Folder, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(20.dp))
                            Text(label, style = MaterialTheme.typography.bodyLarge, modifier = Modifier.padding(start = 12.dp), maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = onUse, enabled = canUse) { Text("Use this folder") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

/** The PC's folders, through Control → Files' own listing (v27 pc.files.list). */
@Composable
private fun PcFolderPicker(start: String, onDismiss: () -> Unit, onPick: (String) -> Unit) {
    val listing by PcTools.listing.collectAsState()
    LaunchedEffect(Unit) { PcTools.list(start) }
    val folders = listing.entries.filter { it.dir || it.drive || it.place }.map { it.name to it.path }
    FolderBrowser(
        title = "Folder on the PC",
        path = listing.path,
        folders = folders,
        note = when {
            listing.loading -> "Loading…"
            listing.error != null -> listing.error
            folders.isEmpty() -> "No folders inside"
            else -> null
        },
        canUse = listing.path.isNotBlank(),
        onUp = if (listing.path.isNotBlank()) ({ PcTools.list(listing.parent ?: "") }) else null,
        onOpen = { PcTools.list(it) },
        onUse = { onPick(listing.path) },
        onDismiss = onDismiss,
    )
}

/** This phone's folders under shared storage (what the PC reaches as /sdcard). */
@Composable
private fun PhoneFolderPicker(start: String, onDismiss: () -> Unit, onPick: (String) -> Unit) {
    val root = Environment.getExternalStorageDirectory().absolutePath
    // The PC speaks of /sdcard; on the phone that is the same place as the storage root.
    fun toLocal(p: String) = if (p.startsWith("/sdcard")) root + p.removePrefix("/sdcard") else p
    fun toPc(p: String) = if (p.startsWith(root)) "/sdcard" + p.removePrefix(root) else p
    var path by remember { mutableStateOf(toLocal(start).takeIf { File(it).isDirectory } ?: root) }
    val folders = remember(path) {
        File(path).listFiles().orEmpty().filter { it.isDirectory && !it.name.startsWith(".") }
            .sortedBy { it.name.lowercase() }.map { it.name to it.absolutePath }
    }
    FolderBrowser(
        title = "Folder on this phone",
        path = toPc(path),
        folders = folders,
        note = if (folders.isEmpty()) "No folders inside" else null,
        canUse = true,
        onUp = if (path != root) ({ path = File(path).parent ?: root }) else null,
        onOpen = { path = it },
        onUse = { onPick(toPc(path)) },
        onDismiss = onDismiss,
    )
}
