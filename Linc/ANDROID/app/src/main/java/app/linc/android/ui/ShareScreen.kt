@file:OptIn(androidx.compose.material3.ExperimentalMaterial3ExpressiveApi::class, androidx.compose.material3.ExperimentalMaterial3Api::class)

package app.linc.android.ui

import android.app.DownloadManager
import android.content.ActivityNotFoundException
import android.content.ClipData
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Environment
import android.provider.DocumentsContract
import android.provider.Settings
import android.text.format.DateUtils
import android.text.format.Formatter
import android.webkit.MimeTypeMap
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
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
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import app.linc.android.service.CompanionStateHolder
import app.linc.android.service.CompanionStateHolder.ServiceState
import app.linc.android.service.QuickShareLauncher
import app.linc.android.service.ReceivedFile
import app.linc.android.service.ShareSender
import app.linc.android.service.ShareStore
import app.linc.android.service.Transfer
import app.linc.android.service.TransferState
import app.linc.android.ui.kit.LincAlertDialog
import app.linc.android.ui.kit.LincBottomSheet
import app.linc.android.ui.kit.EmptyState
import app.linc.android.ui.kit.HelperText
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.LincScaffold
import app.linc.android.ui.kit.ProgressLine
import app.linc.android.ui.kit.Quality
import app.linc.android.ui.kit.SectionHeader
import app.linc.android.ui.kit.Segment
import app.linc.android.ui.kit.StatusChip
import app.linc.android.ui.kit.TileGrid
import app.linc.android.ui.kit.ToolTile
import app.linc.android.ui.kit.pagePadding
import app.linc.android.ui.kit.segmentColor
import app.linc.android.ui.kit.segmentOf
import app.linc.android.ui.kit.segmentShape
import app.linc.android.ui.theme.LincTheme
import java.io.File
import kotlinx.coroutines.launch

/**
 * Share (1.0.0.11, D-083). One mental model on both apps: "Send to my PC" uses the Linc link when
 * it is up — fast, no prompts — and Quick Share when it isn't (or for other people's devices).
 * The page picks by itself and says which it is using.
 */
@Composable
fun ShareScreen(onSettings: (() -> Unit)? = null) {
    val connection by CompanionStateHolder.state.collectAsState()
    val received by ShareStore.received.collectAsState()
    val transfers by ShareStore.transfers.collectAsState()
    val context = LocalContext.current
    var allFiles by remember { mutableStateOf(Environment.isExternalStorageManager()) }
    LaunchedEffect(Unit) {
        ShareStore.attach(context)
        while (true) {
            allFiles = Environment.isExternalStorageManager()
            ShareStore.loadReceived()
            kotlinx.coroutines.delay(3_000)
        }
    }
    ShareContent(
        overLinc = connection is ServiceState.Connected,
        allFiles = allFiles,
        quickShare = remember { QuickShareLauncher.isAvailable(context) },
        received = received,
        transfers = transfers,
        onSettings = onSettings,
    )
}

@Composable
private fun ShareContent(
    overLinc: Boolean,
    allFiles: Boolean,
    quickShare: Boolean,
    received: List<ReceivedFile>,
    transfers: List<Transfer>,
    onSettings: (() -> Unit)?,
) {
    val context = LocalContext.current
    var textSheet by remember { mutableStateOf(false) }
    // "Received from your PC": the newest 20 until Show all; long-press a row to select.
    var showAll by remember { mutableStateOf(false) }
    var selecting by remember { mutableStateOf(false) }
    var selection by remember { mutableStateOf(emptySet<String>()) }
    var confirmDelete by remember { mutableStateOf(false) }
    // Only files still listed count (one can be deleted or moved while it is selected).
    val chosen = received.filter { it.path in selection }

    // Hoisted out of the scaffold so the selection actions in the top bar can use them too.
    val snackbar = remember { SnackbarHostState() }
    val scope = rememberCoroutineScope()
    val notify: (String) -> Unit = { message -> scope.launch { snackbar.showSnackbar(message) } }
    fun sendUris(uris: List<Uri>) {
        if (uris.isEmpty()) return
        if (overLinc) {
            uris.forEach { ShareStore.send(context, it) }
        } else if (!QuickShareLauncher.send(context, uris)) {
            notify("Quick Share isn't available on this phone, and Linc isn't connected")
        }
    }
    val files = rememberLauncherForActivityResult(ActivityResultContracts.GetMultipleContents()) { sendUris(it) }
    val photos = rememberLauncherForActivityResult(ActivityResultContracts.PickMultipleVisualMedia()) { sendUris(it) }

    fun endSelection() {
        selecting = false
        selection = emptySet()
    }
    fun toggle(path: String) {
        selection = if (path in selection) selection - path else selection + path
        if (selection.isEmpty()) selecting = false
    }
    if (selecting) BackHandler { endSelection() }

    LincScaffold(
        title = if (selecting) "${chosen.size} selected" else "Share",
        onBack = if (selecting) ({ endSelection() }) else null,
        onSettings = if (selecting) null else onSettings,
        actions = {
            if (selecting) {
                IconButton(
                    onClick = {
                        sendUris(chosen.mapNotNull { runCatching { uriFor(context, it) }.getOrNull() })
                        endSelection()
                    },
                    enabled = chosen.isNotEmpty() && (!overLinc || allFiles),
                ) { Icon(LincSymbols.Send, contentDescription = if (overLinc) "Send to your PC" else "Send with Quick Share") }
                IconButton(onClick = { shareAll(context, chosen) }, enabled = chosen.isNotEmpty()) {
                    Icon(LincSymbols.Share, contentDescription = "Share")
                }
                IconButton(onClick = { confirmDelete = true }, enabled = chosen.isNotEmpty()) {
                    Icon(LincSymbols.Delete, contentDescription = "Delete")
                }
            }
        },
        snackbar = snackbar,
    ) { inner ->
        LazyColumn(contentPadding = pagePadding(inner), verticalArrangement = Arrangement.spacedBy(Kit.groupGap)) {
            item("mode") {
                Column(Modifier.padding(horizontal = Kit.gutter), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    StatusChip(
                        if (overLinc) "Sending over Linc" else "Linc isn't connected — sending with Quick Share",
                        if (overLinc) Quality.Good else Quality.Off,
                    )
                    HelperText(
                        if (overLinc) "Straight to your PC, no prompts. Use Quick Share from the system share sheet for other people's devices."
                        else "Quick Share asks the PC to accept. When Linc reconnects, sending goes back to the Linc link.",
                    )
                }
            }
            if (overLinc && !allFiles) {
                item("allfiles") {
                    Surface(
                        color = MaterialTheme.colorScheme.errorContainer,
                        contentColor = MaterialTheme.colorScheme.onErrorContainer,
                        shape = RoundedCornerShape(Kit.groupOuter),
                        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter),
                    ) {
                        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                            Icon(LincSymbols.Folder, contentDescription = null)
                            Column(Modifier.weight(1f)) {
                                Text("Allow All files access", style = MaterialTheme.typography.titleMedium)
                                Text("Linc hands files to your PC through its Download/Linc folder, which needs this permission.",
                                    style = MaterialTheme.typography.bodyMedium)
                            }
                            Button(onClick = { openAllFilesSettings(context) }, shapes = ButtonDefaults.shapes()) { Text("Allow") }
                        }
                    }
                }
            }
            item("actions") {
                TileGrid(tiles = listOf(
                    { m -> ToolTile("Send files", LincSymbols.Folder, m, state = if (overLinc) "To your PC" else "With Quick Share",
                        enabled = !overLinc || allFiles, onClick = { files.launch("*/*") }) },
                    { m -> ToolTile("Send photos", LincSymbols.Photos, m, state = if (overLinc) "To your PC" else "With Quick Share",
                        enabled = !overLinc || allFiles, onClick = { photos.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageAndVideo)) }) },
                    { m -> ToolTile("Send text or a link", LincSymbols.InsertLink, m, state = if (overLinc) "Opens on your PC" else "With Quick Share",
                        onClick = { textSheet = true }) },
                    { m -> ToolTile("Receive with Quick Share", LincSymbols.NearMe, m, state = "Be visible for a few minutes", enabled = quickShare,
                        onClick = {
                            notify(if (QuickShareLauncher.becomeVisible(context)) "Visible — send from your PC's Share page now"
                            else "Open Quick Share from quick settings and choose Receive")
                        }) },
                ))
            }
            item("received-h") {
                if (selecting) {
                    val all = chosen.size == received.size
                    SectionHeader("${chosen.size} of ${received.size} selected") {
                        TextButton(onClick = {
                            if (all) {
                                selection = emptySet()
                            } else {
                                selection = received.map { it.path }.toSet()
                                showAll = true // so everything selected is on screen
                            }
                        }) { Text(if (all) "Select none" else "Select all") }
                        TextButton(onClick = { endSelection() }) { Text("Cancel") }
                    }
                } else {
                    SectionHeader("Received from your PC") {
                        TextButton(onClick = {
                            if (!openReceivedFolder(context)) notify("No files app on this phone can open Download/Linc")
                        }) {
                            Icon(LincSymbols.Folder, contentDescription = null, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.width(8.dp))
                            Text("Open folder")
                        }
                    }
                }
            }
            if (received.isEmpty()) {
                item("received-empty") {
                    EmptyState("Nothing yet", "Files your PC sends land here and in Download/Linc.", LincSymbols.Download)
                }
            } else {
                val shown = if (showAll) received else received.take(RECEIVED_PREVIEW)
                shown.forEachIndexed { index, file ->
                    item("r-${file.path}") {
                        Column(Modifier.padding(horizontal = Kit.gutter)) {
                            ReceivedRow(
                                file = file,
                                segment = segmentOf(index, shown.size),
                                selecting = selecting,
                                selected = file.path in selection,
                                onClick = { if (selecting) toggle(file.path) else open(context, file) },
                                onLongClick = {
                                    if (selecting) {
                                        toggle(file.path)
                                    } else {
                                        selecting = true
                                        selection = setOf(file.path)
                                    }
                                },
                                onShare = { shareOn(context, file) },
                            )
                        }
                    }
                }
                if (received.size > RECEIVED_PREVIEW) {
                    item("received-more") {
                        Row(Modifier.fillMaxWidth().padding(horizontal = Kit.gutter), horizontalArrangement = Arrangement.Center) {
                            TextButton(onClick = { showAll = !showAll }) {
                                Text(if (showAll) "Show fewer" else "Show all ${received.size}")
                            }
                        }
                    }
                }
            }
            if (transfers.isNotEmpty()) {
                item("transfers-h") { SectionHeader("Transfers") }
                transfers.forEach { t -> item("t-${t.id}") { TransferRow(t) } }
            }
        }
    }

    if (textSheet) {
        TextShareSheet(overLinc = overLinc, onDismiss = { textSheet = false })
    }
    if (confirmDelete) {
        val count = chosen.size
        LincAlertDialog(
            onDismissRequest = { confirmDelete = false },
            icon = { Icon(LincSymbols.Delete, contentDescription = null) },
            title = { Text(if (count == 1) "Delete ${chosen.first().name}?" else "Delete $count files?") },
            text = { Text("${if (count == 1) "It is" else "They are"} removed from Download/Linc on this phone. What your PC sent from stays on the PC.") },
            confirmButton = {
                TextButton(onClick = {
                    confirmDelete = false
                    val paths = chosen.map { it.path }
                    val removed = ShareStore.deleteReceived(paths)
                    endSelection()
                    notify(
                        when {
                            removed == paths.size -> if (removed == 1) "Deleted" else "Deleted $removed files"
                            !Environment.isExternalStorageManager() -> "Couldn't delete — Linc needs All files access for Download/Linc"
                            else -> "Couldn't delete ${paths.size - removed} of ${paths.size}"
                        },
                    )
                }, enabled = count > 0) { Text("Delete") }
            },
            dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text("Cancel") } },
        )
    }
}

/** How many received files show before "Show all". */
private const val RECEIVED_PREVIEW = 20

/**
 * A received file: tap opens it, long-press starts selecting. While selecting, a checkbox leads
 * and tap toggles; otherwise the look of the kit's SettingsRow with a Share button.
 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun ReceivedRow(
    file: ReceivedFile,
    segment: Segment,
    selecting: Boolean,
    selected: Boolean,
    onClick: () -> Unit,
    onLongClick: () -> Unit,
    onShare: () -> Unit,
) {
    val context = LocalContext.current
    val size = remember(file.path) { runCatching { File(file.path).length() }.getOrDefault(0L) }
    val shape = segmentShape(segment)
    Surface(
        shape = shape,
        color = if (selected) MaterialTheme.colorScheme.secondaryContainer else segmentColor(),
        modifier = Modifier
            .fillMaxWidth()
            .clip(shape)
            .combinedClickable(
                onClickLabel = if (selecting) "Select or unselect" else "Open",
                onLongClickLabel = "Select",
                onLongClick = onLongClick,
                onClick = onClick,
            ),
    ) {
        Row(
            modifier = Modifier
                .heightIn(min = 64.dp)
                .padding(start = if (selecting) 4.dp else Kit.gutter, end = if (selecting) Kit.gutter else 4.dp, top = 12.dp, bottom = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(if (selecting) 4.dp else 16.dp),
        ) {
            if (selecting) {
                Checkbox(checked = selected, onCheckedChange = { onClick() })
            } else {
                Box(
                    Modifier.size(40.dp).clip(CircleShape).background(MaterialTheme.colorScheme.secondaryContainer),
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(iconFor(file.name), contentDescription = null, tint = MaterialTheme.colorScheme.onSecondaryContainer,
                        modifier = Modifier.size(22.dp))
                }
            }
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(file.name, style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurface,
                    maxLines = 2, overflow = TextOverflow.Ellipsis)
                Text(
                    buildString {
                        if (size > 0) append(Formatter.formatShortFileSize(context, size)).append(" · ")
                        append(DateUtils.getRelativeTimeSpanString(file.at, System.currentTimeMillis(), DateUtils.MINUTE_IN_MILLIS))
                    },
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            if (!selecting) {
                IconButton(onClick = onShare) {
                    Icon(LincSymbols.Share, contentDescription = "Share ${file.name}")
                }
            }
        }
    }
}

@Composable
private fun TransferRow(t: Transfer) {
    val context = LocalContext.current
    Surface(
        shape = RoundedCornerShape(Kit.groupOuter),
        color = MaterialTheme.colorScheme.surfaceContainer,
        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            when (t.state) {
                TransferState.Copying -> ProgressLine(t.progress, t.name)
                else -> Row(verticalAlignment = Alignment.CenterVertically) {
                    Icon(
                        when (t.state) {
                            TransferState.Failed -> LincSymbols.Error
                            TransferState.Waiting -> LincSymbols.Schedule
                            else -> LincSymbols.CheckCircle
                        },
                        contentDescription = null,
                        tint = if (t.state == TransferState.Failed) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.primary,
                    )
                    Column(Modifier.weight(1f).padding(start = 12.dp)) {
                        Text(t.name, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(
                            t.message ?: when (t.state) {
                                TransferState.Sent -> "Sent to your PC"
                                TransferState.Waiting -> "Waiting for your PC"
                                else -> "Couldn't send"
                            },
                            style = MaterialTheme.typography.bodyMedium,
                            color = if (t.state == TransferState.Failed) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                    if (t.state == TransferState.Failed && t.uri != null) {
                        TextButton(onClick = { ShareStore.retry(context, t.id) }) { Text("Retry") }
                    } else {
                        IconButton(onClick = { ShareStore.dismiss(t.id) }) { Icon(LincSymbols.Close, contentDescription = "Dismiss") }
                    }
                }
            }
        }
    }
}

@Composable
private fun TextShareSheet(overLinc: Boolean, onDismiss: () -> Unit) {
    val context = LocalContext.current
    var text by remember { mutableStateOf("") }
    LincBottomSheet(onDismissRequest = onDismiss) {
        Column(Modifier.fillMaxWidth().padding(horizontal = Kit.gutter).padding(bottom = 24.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text("Send text or a link", style = MaterialTheme.typography.headlineSmall)
            HelperText(if (overLinc) "A link opens on your PC; text lands on its clipboard." else "Sent with Quick Share.")
            OutlinedTextField(value = text, onValueChange = { text = it }, minLines = 2, maxLines = 6, modifier = Modifier.fillMaxWidth(),
                placeholder = { Text("Paste a link or type a note") })
            Button(
                onClick = {
                    val sent = if (overLinc) ShareSender.sendText(text) else QuickShareLauncher.sendText(context, text)
                    if (sent) onDismiss()
                },
                enabled = text.isNotBlank(),
                shapes = ButtonDefaults.shapes(),
                modifier = Modifier.fillMaxWidth().heightIn(min = 56.dp),
            ) { Text("Send") }
        }
    }
}

private fun iconFor(name: String) = when (name.substringAfterLast('.', "").lowercase()) {
    "jpg", "jpeg", "png", "gif", "webp", "heic", "bmp" -> LincSymbols.Image
    "txt", "md", "pdf", "doc", "docx" -> LincSymbols.TextSnippet
    else -> LincSymbols.AttachFile
}

private fun uriFor(context: Context, file: ReceivedFile): Uri =
    FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", File(file.path))

private fun mimeFor(name: String) =
    MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substringAfterLast('.', "").lowercase()) ?: "*/*"

private fun open(context: Context, file: ReceivedFile) {
    runCatching {
        context.startActivity(Intent(Intent.ACTION_VIEW).setDataAndType(uriFor(context, file), mimeFor(file.name))
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK))
    }
}

private fun shareOn(context: Context, file: ReceivedFile) {
    runCatching {
        val send = Intent(Intent.ACTION_SEND).setType(mimeFor(file.name)).putExtra(Intent.EXTRA_STREAM, uriFor(context, file))
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        context.startActivity(Intent.createChooser(send, file.name).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    }
}

/** Several received files in one share sheet (a single one goes through [shareOn]). */
private fun shareAll(context: Context, files: List<ReceivedFile>) {
    if (files.isEmpty()) return
    if (files.size == 1) {
        shareOn(context, files[0])
        return
    }
    runCatching {
        val uris = files.map { uriFor(context, it) }
        val send = Intent(Intent.ACTION_SEND_MULTIPLE).setType(commonMime(files.map { mimeFor(it.name) }))
            .putParcelableArrayListExtra(Intent.EXTRA_STREAM, ArrayList(uris))
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        // ClipData carries the read grant to whichever app the chooser hands it to.
        send.clipData = ClipData.newUri(context.contentResolver, "files", uris[0]).apply {
            uris.drop(1).forEach { addItem(ClipData.Item(it)) }
        }
        context.startActivity(Intent.createChooser(send, "${files.size} files").addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    }
}

/** One type for a mixed selection: all the same, the same family (all images), or anything. */
private fun commonMime(types: List<String>): String {
    val distinct = types.distinct()
    if (distinct.size == 1) return distinct[0]
    val families = distinct.map { it.substringBefore('/') }.distinct()
    return if (families.size == 1 && families[0] != "*") "${families[0]}/*" else "*/*"
}

private const val EXTERNAL_STORAGE_DOCS = "com.android.externalstorage.documents"

/**
 * Download/Linc in the system Files app (a DocumentsContract folder link). Falls back to the
 * system Downloads view for phones whose Files app doesn't take one, or before the folder exists.
 * False when nothing opened.
 */
private fun openReceivedFolder(context: Context): Boolean {
    val dir = File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "Linc")
    val attempts = buildList {
        if (dir.isDirectory) {
            val folder = DocumentsContract.buildDocumentUri(EXTERNAL_STORAGE_DOCS, "primary:${Environment.DIRECTORY_DOWNLOADS}/Linc")
            add(Intent(Intent.ACTION_VIEW).setDataAndType(folder, DocumentsContract.Document.MIME_TYPE_DIR))
        }
        add(Intent(DownloadManager.ACTION_VIEW_DOWNLOADS))
    }
    return attempts.any { intent ->
        try {
            context.startActivity(intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            true
        } catch (_: ActivityNotFoundException) {
            false
        } catch (_: SecurityException) {
            false
        }
    }
}

internal fun openAllFilesSettings(context: Context) {
    try {
        context.startActivity(Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION, Uri.fromParts("package", context.packageName, null))
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    } catch (_: ActivityNotFoundException) {
        runCatching { context.startActivity(Intent(Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
    }
}

// ---- previews --------------------------------------------------------------------------------

private val sampleReceived = listOf(
    ReceivedFile("Quarterly report.pdf", "/x/Quarterly report.pdf", System.currentTimeMillis() - 120_000),
    ReceivedFile("holiday.jpg", "/x/holiday.jpg", System.currentTimeMillis() - 7_200_000),
)
private val sampleTransfers = listOf(
    Transfer(1, "video.mp4", TransferState.Copying, 0, 0.42f),
    Transfer(2, "notes.txt", TransferState.Failed, 0, message = "Couldn't copy notes.txt — is the phone's storage full?"),
)

@Preview(name = "Share — light", widthDp = 360, heightDp = 1100)
@Composable
private fun SharePreviewLight() = LincTheme(darkTheme = false) {
    ShareContent(overLinc = true, allFiles = true, quickShare = true, received = sampleReceived, transfers = sampleTransfers, onSettings = {})
}

@Preview(name = "Share — dark, not connected", widthDp = 411, heightDp = 1000)
@Composable
private fun SharePreviewDark() = LincTheme(darkTheme = true) {
    ShareContent(overLinc = false, allFiles = true, quickShare = true, received = emptyList(), transfers = emptyList(), onSettings = {})
}

@Preview(name = "Share — needs all files access", widthDp = 360, heightDp = 900, fontScale = 2f)
@Composable
private fun SharePreviewBlocked() = LincTheme {
    ShareContent(overLinc = true, allFiles = false, quickShare = false, received = emptyList(), transfers = emptyList(), onSettings = {})
}
