@file:OptIn(androidx.compose.material3.ExperimentalMaterial3ExpressiveApi::class, androidx.compose.material3.ExperimentalMaterial3Api::class)

package app.linc.android.ui

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Environment
import android.provider.Settings
import android.text.format.DateUtils
import android.text.format.Formatter
import android.webkit.MimeTypeMap
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
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
import app.linc.android.ui.kit.EmptyState
import app.linc.android.ui.kit.HelperText
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.LincScaffold
import app.linc.android.ui.kit.ProgressLine
import app.linc.android.ui.kit.Quality
import app.linc.android.ui.kit.SectionHeader
import app.linc.android.ui.kit.SettingsRow
import app.linc.android.ui.kit.StatusChip
import app.linc.android.ui.kit.TileGrid
import app.linc.android.ui.kit.ToolTile
import app.linc.android.ui.kit.pagePadding
import app.linc.android.ui.kit.rememberNotify
import app.linc.android.ui.kit.segmentOf
import app.linc.android.ui.theme.LincTheme
import java.io.File

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

    LincScaffold(title = "Share", onSettings = onSettings) { inner ->
        val notify = rememberNotify()
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
            item("received-h") { SectionHeader("Received from your PC") }
            if (received.isEmpty()) {
                item("received-empty") {
                    EmptyState("Nothing yet", "Files your PC sends land here and in Download/Linc.", LincSymbols.Download)
                }
            } else {
                received.take(20).forEachIndexed { index, file ->
                    item("r-${file.path}") {
                        val size = remember(file.path) { runCatching { File(file.path).length() }.getOrDefault(0L) }
                        Column(Modifier.padding(horizontal = Kit.gutter)) {
                            SettingsRow(
                                title = file.name,
                                segment = segmentOf(index, minOf(received.size, 20)),
                                icon = iconFor(file.name),
                                value = buildString {
                                    if (size > 0) append(Formatter.formatShortFileSize(context, size)).append(" · ")
                                    append(DateUtils.getRelativeTimeSpanString(file.at, System.currentTimeMillis(), DateUtils.MINUTE_IN_MILLIS))
                                },
                                onClick = { open(context, file) },
                                trailing = {
                                    IconButton(onClick = { shareOn(context, file) }) {
                                        Icon(LincSymbols.Share, contentDescription = "Share ${file.name}")
                                    }
                                },
                            )
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
    ModalBottomSheet(onDismissRequest = onDismiss) {
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
