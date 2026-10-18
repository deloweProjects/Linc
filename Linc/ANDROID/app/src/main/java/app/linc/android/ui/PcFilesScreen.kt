package app.linc.android.ui

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.linc.android.service.Explanations
import app.linc.android.service.PcFileEntry
import app.linc.android.service.PcTools
import app.linc.android.ui.kit.HelperText
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.LincScaffold
import app.linc.android.ui.kit.pagePadding
import app.linc.android.ui.kit.rememberNotify
import java.text.DateFormat
import java.util.Date

/**
 * Tools → Files (D-097): the PC's files from the phone, as the PC's Files page shows the phone's.
 * Tap a folder to go in; tap a file for what to do with it — download to the phone, open on the
 * PC, rename, or delete (to the PC's Recycle Bin, so it can be brought back). Upload and New
 * folder put things where you are.
 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
fun PcFilesScreen(onBack: () -> Unit) {
    val context = LocalContext.current
    val listing by PcTools.listing.collectAsState()
    val transfers by PcTools.transfers.collectAsState()
    val note by PcTools.fileNote.collectAsState()
    val notify = rememberNotify()
    val ready = PcTools.ready()
    var menuFor by remember { mutableStateOf<PcFileEntry?>(null) }
    var renaming by remember { mutableStateOf<PcFileEntry?>(null) }
    var deleting by remember { mutableStateOf<PcFileEntry?>(null) }
    var newFolder by remember { mutableStateOf(false) }
    val pick = rememberLauncherForActivityResult(ActivityResultContracts.GetMultipleContents()) { uris ->
        for (uri in uris) PcTools.upload(context, listing.path, uri)
    }
    LaunchedEffect(ready) { if (ready && listing.entries.isEmpty()) PcTools.list("") }
    LaunchedEffect(note) { note?.let { notify(it); PcTools.clearNote() } }

    val atRoots = listing.path.isEmpty()
    LincScaffold(
        title = if (atRoots) "PC files" else listing.path.trimEnd('\\').substringAfterLast('\\').ifEmpty { listing.path },
        subtitle = if (atRoots) null else listing.path,
        onBack = { if (atRoots) onBack() else PcTools.list(listing.parent ?: "") },
        actions = {
            IconButton(onClick = { PcTools.list("") }, enabled = ready) { Icon(LincSymbols.Home, contentDescription = "Start") }
            IconButton(onClick = { PcTools.list(listing.path) }, enabled = ready) { Icon(LincSymbols.Refresh, contentDescription = "Refresh") }
        },
        floatingActionButton = {
            if (!atRoots && ready) Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                ExtendedFloatingActionButton(onClick = { newFolder = true }, icon = { Icon(LincSymbols.Add, null) }, text = { Text("Folder") })
                ExtendedFloatingActionButton(onClick = { pick.launch("*/*") }, icon = { Icon(LincSymbols.UploadFile, null) }, text = { Text("Upload") })
            }
        },
    ) { inner ->
        LazyColumn(contentPadding = pagePadding(inner), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            if (!ready) item { HelperText("Connect the PC, and update Linc on it, to see its files.", kind = Explanations.Kind.Blocked, modifier = Modifier.padding(horizontal = Kit.gutter)) }
            listing.error?.let { item { HelperText(it, kind = Explanations.Kind.Error, modifier = Modifier.padding(horizontal = Kit.gutter)) } }
            if (listing.loading) item { LinearProgressIndicator(Modifier.fillMaxWidth().padding(horizontal = Kit.gutter)) }
            for (t in transfers.filter { !it.done || it.error != null }.take(4)) item(t.id) {
                Column(Modifier.padding(horizontal = Kit.gutter, vertical = 4.dp)) {
                    Text((if (t.upload) "Sending " else "Downloading ") + t.name + (t.error?.let { " — $it" } ?: ""),
                        style = MaterialTheme.typography.bodySmall, color = if (t.error != null) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
                    if (t.error == null) LinearProgressIndicator(progress = { t.progress }, modifier = Modifier.fillMaxWidth())
                }
            }
            if (!listing.loading && listing.entries.isEmpty() && listing.error == null && ready) item {
                HelperText(if (atRoots) "Nothing to show yet." else "This folder is empty.", modifier = Modifier.padding(horizontal = Kit.gutter))
            }
            items(listing.entries, key = { it.path }) { e ->
                Surface(
                    shape = RoundedCornerShape(16.dp),
                    color = MaterialTheme.colorScheme.surfaceContainer,
                    modifier = Modifier.fillMaxWidth().combinedClickable(
                        onClick = { if (e.dir) PcTools.list(e.path) else menuFor = e },
                        onLongClick = { if (!e.drive && !e.place) menuFor = e },
                    ),
                ) {
                    Row(Modifier.padding(horizontal = 16.dp, vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                        Icon(when { e.drive -> LincSymbols.Storage; e.dir -> LincSymbols.Folder; else -> LincSymbols.TextSnippet },
                            contentDescription = null, modifier = Modifier.size(24.dp))
                        Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                            Text(e.name, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            val details = when {
                                e.drive -> e.freeGb?.let { "%.0f GB free".format(it) }
                                e.place -> null
                                e.dir -> date(e.modified)
                                else -> listOfNotNull(size(e.size), date(e.modified)).joinToString(" · ")
                            }
                            details?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                        }
                        if (!e.drive && !e.place) Box {
                            IconButton(onClick = { menuFor = e }) { Icon(LincSymbols.MoreVert, contentDescription = "More for ${e.name}") }
                            DropdownMenu(expanded = menuFor == e, onDismissRequest = { menuFor = null }) {
                                if (!e.dir) DropdownMenuItem(text = { Text("Download to this phone") }, leadingIcon = { Icon(LincSymbols.Download, null) },
                                    onClick = { menuFor = null; PcTools.download(context, e); notify("Downloading to Download/Linc…") })
                                DropdownMenuItem(text = { Text("Open on the PC") }, leadingIcon = { Icon(LincSymbols.OpenInNew, null) },
                                    onClick = { menuFor = null; PcTools.openFile(e.path) })
                                DropdownMenuItem(text = { Text("Rename") }, leadingIcon = { Icon(LincSymbols.Edit, null) },
                                    onClick = { menuFor = null; renaming = e })
                                DropdownMenuItem(text = { Text("Delete") }, leadingIcon = { Icon(LincSymbols.Delete, null) },
                                    onClick = { menuFor = null; deleting = e })
                            }
                        }
                    }
                }
            }
        }
    }

    if (newFolder) NameDialog("New folder", "New folder", "Make") { name -> newFolder = false; name?.let { PcTools.makeFolder(listing.path, it) } }
    renaming?.let { e -> NameDialog("Rename", e.name, "Rename") { name -> renaming = null; if (name != null && name != e.name) PcTools.rename(e.path, name) } }
    deleting?.let { e ->
        AlertDialog(
            onDismissRequest = { deleting = null },
            title = { Text("Delete ${e.name}?") },
            text = { Text("It goes to the PC's Recycle Bin, so it can be restored there.") },
            confirmButton = { TextButton(onClick = { PcTools.delete(e.path); deleting = null }) { Text("Delete") } },
            dismissButton = { TextButton(onClick = { deleting = null }) { Text("Cancel") } },
        )
    }
}

@Composable
private fun Box(content: @Composable () -> Unit) = androidx.compose.foundation.layout.Box { content() }

@Composable
private fun NameDialog(title: String, initial: String, confirm: String, onDone: (String?) -> Unit) {
    var name by remember { mutableStateOf(initial) }
    AlertDialog(
        onDismissRequest = { onDone(null) },
        title = { Text(title) },
        text = { OutlinedTextField(value = name, onValueChange = { name = it }, singleLine = true) },
        confirmButton = { TextButton(onClick = { onDone(name.trim()) }, enabled = name.isNotBlank()) { Text(confirm) } },
        dismissButton = { TextButton(onClick = { onDone(null) }) { Text("Cancel") } },
    )
}

private fun size(bytes: Long): String = when {
    bytes < 1024 -> "$bytes B"
    bytes < 1024 * 1024 -> "%.0f KB".format(bytes / 1024.0)
    bytes < 1024L * 1024 * 1024 -> "%.1f MB".format(bytes / (1024.0 * 1024))
    else -> "%.2f GB".format(bytes / (1024.0 * 1024 * 1024))
}

private fun date(ms: Long): String? = if (ms <= 0) null else DateFormat.getDateTimeInstance(DateFormat.MEDIUM, DateFormat.SHORT).format(Date(ms))
