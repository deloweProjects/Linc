package app.linc.android.ui

import android.content.Context
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.LinearWavyProgressIndicator
import androidx.compose.material3.MaterialShapes
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SmallFloatingActionButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.edit
import app.linc.android.service.Explanations
import app.linc.android.service.PcFileEntry
import app.linc.android.service.PcTools
import app.linc.android.ui.kit.HelperText
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.LincScaffold
import app.linc.android.ui.kit.SectionHeader
import app.linc.android.ui.kit.rememberNotify
import app.linc.android.ui.kit.segmentOf
import app.linc.android.ui.kit.segmentShape
import java.text.DateFormat
import java.util.Date

/**
 * Tools → Files (D-097): the PC's files from the phone, as Explorer shows them — the user's own
 * places (Desktop, Documents, Downloads, Pictures, Music, Videos) and the drives first, then
 * folders as a grid or a list. The path along the top is a row of steps: tap any one to go back
 * to it. Tap a file for what to do with it — download to the phone, open on the PC, rename, or
 * delete (to the PC's Recycle Bin, so it can be brought back). Upload and New folder put things
 * where you are.
 */
@OptIn(ExperimentalFoundationApi::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun PcFilesScreen(onBack: (() -> Unit)? = null, onSettings: (() -> Unit)? = null) {
    val context = LocalContext.current
    val prefs = remember { context.getSharedPreferences("linc", Context.MODE_PRIVATE) }
    val listing by PcTools.listing.collectAsState()
    val transfers by PcTools.transfers.collectAsState()
    val note by PcTools.fileNote.collectAsState()
    val notify = rememberNotify()
    // Follow the connection: "ready" read once went stale when the page opened mid-reconnect
    // (common over Wi-Fi), and the page then sat on "Connect the PC" or an old listing for ever.
    val connection by app.linc.android.service.CompanionStateHolder.state.collectAsState()
    val ready = remember(connection) { PcTools.ready() }
    var grid by remember { mutableStateOf(prefs.getBoolean(KEY_GRID, true)) }
    var menuFor by remember { mutableStateOf<PcFileEntry?>(null) }
    var renaming by remember { mutableStateOf<PcFileEntry?>(null) }
    var deleting by remember { mutableStateOf<PcFileEntry?>(null) }
    var newFolder by remember { mutableStateOf(false) }
    val pick = rememberLauncherForActivityResult(ActivityResultContracts.GetMultipleContents()) { uris ->
        for (uri in uris) PcTools.upload(context, listing.path, uri)
    }
    // On open and on every reconnect, ask again for where we are (or the start page).
    LaunchedEffect(ready, connection) { if (ready) PcTools.list(listing.path) }
    // An answer that never comes says so, with a way to try again.
    var slow by remember { mutableStateOf(false) }
    LaunchedEffect(listing.loading) {
        slow = false
        if (listing.loading) { kotlinx.coroutines.delay(8_000); slow = listing.loading }
    }
    LaunchedEffect(note) { note?.let { notify(it); PcTools.clearNote() } }

    val atRoots = listing.path.isEmpty()
    // Back goes up a folder before it leaves the page.
    BackHandler(enabled = !atRoots) { PcTools.list(listing.parent ?: "") }
    fun open(e: PcFileEntry) { if (e.dir) PcTools.list(e.path) else menuFor = e }

    LincScaffold(
        title = if (atRoots) "Files" else folderName(listing.path),
        subtitle = if (atRoots) "On your PC" else null,
        onBack = if (atRoots) onBack else ({ PcTools.list(listing.parent ?: "") }),
        onSettings = onSettings,
        actions = {
            IconButton(onClick = {
                grid = !grid
                prefs.edit { putBoolean(KEY_GRID, grid) }
            }) { Icon(if (grid) LincSymbols.ListView else LincSymbols.GridView, contentDescription = if (grid) "Show as a list" else "Show as a grid") }
            IconButton(onClick = { PcTools.list(listing.path) }, enabled = ready) { Icon(LincSymbols.Refresh, contentDescription = "Refresh") }
        },
        floatingActionButton = {
            if (!atRoots && ready) Column(horizontalAlignment = Alignment.End, verticalArrangement = Arrangement.spacedBy(12.dp)) {
                SmallFloatingActionButton(onClick = { newFolder = true }) { Icon(LincSymbols.Add, contentDescription = "New folder") }
                ExtendedFloatingActionButton(onClick = { pick.launch("*/*") }, icon = { Icon(LincSymbols.UploadFile, null) }, text = { Text("Upload") })
            }
        },
    ) { inner ->
        LazyVerticalGrid(
            columns = if (grid) GridCells.Adaptive(104.dp) else GridCells.Fixed(1),
            // Full rows span maxLineSpan, the grid's own width in columns; Int.MAX_VALUE left the
            // page blank under its title (UI test, emulator). And the grid fills the page rather
            // than measuring to its content.
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(start = Kit.gutter, end = Kit.gutter, top = inner.calculateTopPadding() + 4.dp, bottom = 120.dp),
            horizontalArrangement = Arrangement.spacedBy(if (grid) 10.dp else 0.dp),
            verticalArrangement = Arrangement.spacedBy(if (grid) 10.dp else 2.dp),
        ) {
            item(span = { GridItemSpan(maxLineSpan) }) { Breadcrumbs(listing.path, enabled = ready) { PcTools.list(it) } }
            if (!ready) item(span = { GridItemSpan(maxLineSpan) }) {
                HelperText("Connect the PC, and update Linc on it, to see its files.", kind = Explanations.Kind.Blocked)
            }
            listing.error?.let { item(span = { GridItemSpan(maxLineSpan) }) { HelperText(it, kind = Explanations.Kind.Error) } }
            if (slow && ready) item(span = { GridItemSpan(maxLineSpan) }) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    HelperText("The PC hasn't answered yet.", kind = Explanations.Kind.Warning, modifier = Modifier.weight(1f))
                    TextButton(onClick = { PcTools.list(listing.path) }) { Text("Try again") }
                }
            }
            if (listing.loading) item(span = { GridItemSpan(maxLineSpan) }) { LinearWavyProgressIndicator(Modifier.fillMaxWidth().padding(vertical = 4.dp)) }
            for (t in transfers.filter { !it.done || it.error != null }.take(4)) item(t.id, span = { GridItemSpan(maxLineSpan) }) {
                Surface(shape = RoundedCornerShape(16.dp), color = MaterialTheme.colorScheme.secondaryContainer) {
                    Column(Modifier.fillMaxWidth().padding(12.dp)) {
                        Text((if (t.upload) "Sending " else "Downloading ") + t.name + (t.error?.let { " — $it" } ?: ""),
                            style = MaterialTheme.typography.bodySmall, maxLines = 2,
                            color = if (t.error != null) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSecondaryContainer)
                        if (t.error == null) LinearProgressIndicator(progress = { t.progress }, modifier = Modifier.fillMaxWidth().padding(top = 6.dp))
                    }
                }
            }
            if (!listing.loading && listing.entries.isEmpty() && listing.error == null && ready) item(span = { GridItemSpan(maxLineSpan) }) {
                HelperText(if (atRoots) "Nothing to show yet." else "This folder is empty.", modifier = Modifier.padding(vertical = 24.dp),
                    textAlign = TextAlign.Center)
            }
            if (atRoots) {
                // The start page: places as tiles, drives as cards with how full they are.
                val places = listing.entries.filter { it.place }
                val drives = listing.entries.filter { it.drive }
                if (places.isNotEmpty()) {
                    item(span = { GridItemSpan(maxLineSpan) }) { SectionHeader("Places", Modifier.padding(start = 0.dp)) }
                    items(places, key = { it.path }) { e -> PlaceTile(e) { open(e) } }
                }
                if (drives.isNotEmpty()) {
                    item(span = { GridItemSpan(maxLineSpan) }) { SectionHeader("Drives") }
                    items(drives, key = { it.path }, span = { GridItemSpan(maxLineSpan) }) { e -> DriveCard(e) { open(e) } }
                }
            } else {
                val entries = listing.entries
                items(entries, key = { it.path }) { e ->
                    val index = entries.indexOf(e)
                    Box {
                        if (grid) GridItem(e, onClick = { open(e) }, onLongClick = { menuFor = e })
                        else ListItem(e, segmentOf(index, entries.size), onClick = { open(e) }, onMore = { menuFor = e })
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

    if (newFolder) NameDialog("New folder", "New folder", "Make") { name -> newFolder = false; name?.let { PcTools.makeFolder(listing.path, it) } }
    renaming?.let { e -> NameDialog("Rename", e.name, "Rename") { name -> renaming = null; if (name != null && name != e.name) PcTools.rename(e.path, name) } }
    deleting?.let { e ->
        AlertDialog(
            onDismissRequest = { deleting = null },
            icon = { Icon(LincSymbols.Delete, contentDescription = null) },
            title = { Text("Delete ${e.name}?") },
            text = { Text("It goes to the PC's Recycle Bin, so it can be restored there.") },
            confirmButton = { TextButton(onClick = { PcTools.delete(e.path); deleting = null }) { Text("Delete") } },
            dismissButton = { TextButton(onClick = { deleting = null }) { Text("Cancel") } },
        )
    }
}

// ---- the path ----------------------------------------------------------------------------------

/** One step of the path: what it says, and where it goes ("" is the start page). */
internal data class Crumb(val label: String, val path: String)

/** "C:\Users\Sam\Music" → This PC › C: › Users › Sam › Music, each step its own folder. */
internal fun crumbs(path: String): List<Crumb> {
    val out = mutableListOf(Crumb("This PC", ""))
    if (path.isEmpty()) return out
    val parts = path.trimEnd('\\').split('\\').filter { it.isNotEmpty() }
    var at = ""
    for ((i, part) in parts.withIndex()) {
        at = if (i == 0) "$part\\" else if (i == 1) at + part else "$at\\$part"
        out += Crumb(part, at)
    }
    return out
}

@Composable
private fun Breadcrumbs(path: String, enabled: Boolean, onGo: (String) -> Unit) {
    val steps = crumbs(path)
    val scroll = rememberScrollState()
    LaunchedEffect(path) { scroll.scrollTo(scroll.maxValue) }
    Row(Modifier.fillMaxWidth().horizontalScroll(scroll).padding(vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        steps.forEachIndexed { i, c ->
            val last = i == steps.lastIndex
            Surface(
                onClick = { if (!last) onGo(c.path) },
                enabled = enabled,
                shape = CircleShape,
                color = if (last) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surfaceContainerHigh,
            ) {
                Row(Modifier.padding(horizontal = 12.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    if (i == 0) {
                        Icon(LincSymbols.Computer, contentDescription = null, modifier = Modifier.size(16.dp))
                        Spacer(Modifier.width(6.dp))
                    }
                    Text(c.label, style = MaterialTheme.typography.labelLarge, maxLines = 1,
                        fontWeight = if (last) FontWeight.SemiBold else null,
                        color = if (last) MaterialTheme.colorScheme.onSecondaryContainer else MaterialTheme.colorScheme.onSurface)
                }
            }
            if (!last) Icon(LincSymbols.ChevronRight, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.size(18.dp).padding(horizontal = 1.dp))
        }
    }
}

private fun folderName(path: String) = path.trimEnd('\\').substringAfterLast('\\').ifEmpty { path }

// ---- items -------------------------------------------------------------------------------------

/** A place's own icon and colours, so Desktop, Music and the rest are told apart at a glance. */
@Composable
private fun placeLook(name: String): Triple<ImageVector, Color, Color> {
    val c = MaterialTheme.colorScheme
    return when (name) {
        "Desktop" -> Triple(LincSymbols.DesktopWindows, c.primaryContainer, c.onPrimaryContainer)
        "Documents" -> Triple(LincSymbols.Description, c.secondaryContainer, c.onSecondaryContainer)
        "Downloads" -> Triple(LincSymbols.Download, c.tertiaryContainer, c.onTertiaryContainer)
        "Pictures" -> Triple(LincSymbols.PhotoLibrary, c.primaryContainer, c.onPrimaryContainer)
        "Music" -> Triple(LincSymbols.LibraryMusic, c.tertiaryContainer, c.onTertiaryContainer)
        "Videos" -> Triple(LincSymbols.VideoLibrary, c.secondaryContainer, c.onSecondaryContainer)
        else -> Triple(LincSymbols.Folder, c.surfaceContainerHighest, c.onSurface)
    }
}

/** What a file is, from its extension. */
internal enum class FileKind { Folder, Image, Audio, Video, Archive, Code, Document, App, Other }

internal fun kindOf(e: PcFileEntry): FileKind {
    if (e.dir) return FileKind.Folder
    return when (e.name.substringAfterLast('.', "").lowercase()) {
        "jpg", "jpeg", "png", "gif", "webp", "bmp", "heic", "svg", "ico", "tif", "tiff", "raw" -> FileKind.Image
        "mp3", "wav", "flac", "m4a", "aac", "ogg", "opus", "wma" -> FileKind.Audio
        "mp4", "mkv", "mov", "avi", "webm", "wmv", "m4v" -> FileKind.Video
        "zip", "rar", "7z", "tar", "gz", "xz", "bz2", "iso" -> FileKind.Archive
        "kt", "java", "cs", "js", "ts", "py", "c", "cpp", "h", "json", "xml", "html", "css", "ps1", "bat", "sh", "xaml", "yml", "yaml", "md" -> FileKind.Code
        "pdf", "doc", "docx", "txt", "rtf", "odt", "xls", "xlsx", "csv", "ppt", "pptx" -> FileKind.Document
        "exe", "msi", "apk", "appx", "msix", "lnk" -> FileKind.App
        else -> FileKind.Other
    }
}

@Composable
private fun kindLook(kind: FileKind): Triple<ImageVector, Color, Color> {
    val c = MaterialTheme.colorScheme
    return when (kind) {
        FileKind.Folder -> Triple(LincSymbols.Folder, c.primaryContainer, c.onPrimaryContainer)
        FileKind.Image -> Triple(LincSymbols.Image, c.tertiaryContainer, c.onTertiaryContainer)
        FileKind.Audio -> Triple(LincSymbols.AudioFile, c.secondaryContainer, c.onSecondaryContainer)
        FileKind.Video -> Triple(LincSymbols.VideoFile, c.tertiaryContainer, c.onTertiaryContainer)
        FileKind.Archive -> Triple(LincSymbols.FolderZip, c.surfaceContainerHighest, c.onSurface)
        FileKind.Code -> Triple(LincSymbols.Code, c.secondaryContainer, c.onSecondaryContainer)
        FileKind.Document -> Triple(LincSymbols.Description, c.secondaryContainer, c.onSecondaryContainer)
        FileKind.App -> Triple(LincSymbols.Apps, c.primaryContainer, c.onPrimaryContainer)
        FileKind.Other -> Triple(LincSymbols.Article, c.surfaceContainerHighest, c.onSurfaceVariant)
    }
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun PlaceTile(e: PcFileEntry, onClick: () -> Unit) {
    val (icon, container, content) = placeLook(e.name)
    Surface(onClick = onClick, shape = RoundedCornerShape(24.dp), color = MaterialTheme.colorScheme.surfaceContainer) {
        Column(Modifier.fillMaxWidth().padding(vertical = 16.dp, horizontal = 8.dp), horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Box(Modifier.size(52.dp).clip(MaterialShapes.Cookie9Sided.toShape()).background(container), contentAlignment = Alignment.Center) {
                Icon(icon, contentDescription = null, tint = content)
            }
            Text(e.name, style = MaterialTheme.typography.labelLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun DriveCard(e: PcFileEntry, onClick: () -> Unit) {
    Surface(onClick = onClick, shape = RoundedCornerShape(20.dp), color = MaterialTheme.colorScheme.surfaceContainer) {
        Row(Modifier.fillMaxWidth().padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(44.dp).clip(RoundedCornerShape(12.dp)).background(MaterialTheme.colorScheme.secondaryContainer),
                contentAlignment = Alignment.Center) { Icon(LincSymbols.HardDrive, contentDescription = null, tint = MaterialTheme.colorScheme.onSecondaryContainer) }
            Column(Modifier.weight(1f).padding(start = 14.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Text(e.name, style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                val free = e.freeGb
                val total = e.totalGb
                if (free != null && total != null && total > 0) {
                    LinearProgressIndicator(progress = { ((total - free) / total).toFloat().coerceIn(0f, 1f) }, modifier = Modifier.fillMaxWidth())
                    Text("%.0f GB free of %.0f GB".format(free, total), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                } else if (free != null) {
                    Text("%.0f GB free".format(free), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun GridItem(e: PcFileEntry, onClick: () -> Unit, onLongClick: () -> Unit) {
    val (icon, container, content) = kindLook(kindOf(e))
    Surface(shape = RoundedCornerShape(20.dp), color = MaterialTheme.colorScheme.surfaceContainer,
        modifier = Modifier.clip(RoundedCornerShape(20.dp)).combinedClickable(onClick = onClick, onLongClick = onLongClick)) {
        Column(Modifier.fillMaxWidth().padding(10.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Box(Modifier.fillMaxWidth().aspectRatio(1.3f).clip(RoundedCornerShape(14.dp)).background(container), contentAlignment = Alignment.Center) {
                Icon(icon, contentDescription = null, tint = content, modifier = Modifier.size(36.dp))
                if (!e.dir) extension(e.name)?.let {
                    Text(it, style = MaterialTheme.typography.labelSmall, color = content,
                        modifier = Modifier.align(Alignment.BottomEnd).padding(6.dp))
                }
            }
            Text(e.name, style = MaterialTheme.typography.bodySmall, maxLines = 2, overflow = TextOverflow.Ellipsis, textAlign = TextAlign.Center,
                modifier = Modifier.fillMaxWidth())
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun ListItem(e: PcFileEntry, segment: app.linc.android.ui.kit.Segment, onClick: () -> Unit, onMore: () -> Unit) {
    val (icon, container, content) = kindLook(kindOf(e))
    Surface(shape = segmentShape(segment), color = MaterialTheme.colorScheme.surfaceContainer,
        modifier = Modifier.fillMaxWidth().clip(segmentShape(segment)).combinedClickable(onClick = onClick, onLongClick = onMore)) {
        Row(Modifier.padding(start = 12.dp, end = 4.dp, top = 10.dp, bottom = 10.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(40.dp).clip(RoundedCornerShape(12.dp)).background(container), contentAlignment = Alignment.Center) {
                Icon(icon, contentDescription = null, tint = content, modifier = Modifier.size(22.dp))
            }
            Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                Text(e.name, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                val details = if (e.dir) date(e.modified) else listOfNotNull(size(e.size), date(e.modified)).joinToString(" · ")
                details?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            }
            IconButton(onClick = onMore) { Icon(LincSymbols.MoreVert, contentDescription = "More for ${e.name}") }
        }
    }
}

private fun extension(name: String): String? = name.substringAfterLast('.', "").takeIf { it.isNotEmpty() && it.length <= 5 }?.uppercase()

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

private const val KEY_GRID = "pc_files_grid"
