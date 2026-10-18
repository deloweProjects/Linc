@file:OptIn(androidx.compose.material3.ExperimentalMaterial3ExpressiveApi::class, androidx.compose.material3.ExperimentalMaterial3Api::class)

package app.linc.android.ui

import android.content.ClipData
import android.content.ClipboardManager
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import app.linc.android.service.ConnectionWatch
import app.linc.android.service.LogEntry
import app.linc.android.service.LogLevel
import app.linc.android.service.LogStore
import app.linc.android.ui.kit.EmptyState
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.LincScaffold
import app.linc.android.ui.kit.pagePadding
import app.linc.android.ui.kit.rememberNotify
import app.linc.android.ui.theme.LincTheme
import java.text.SimpleDateFormat
import java.util.Locale

private enum class LogFilter(val label: String) { All("All"), Warnings("Warnings"), Errors("Errors") }

/** Logs (Settings → Help & diagnostics): newest first, monospace, filterable, one Copy. */
@Composable
fun LogsScreen(onBack: () -> Unit) {
    val entries by LogStore.entries.collectAsState()
    LogsContent(entries, onBack)
}

@Composable
private fun LogsContent(entries: List<LogEntry>, onBack: () -> Unit) {
    val context = LocalContext.current
    var filter by rememberSaveable { mutableStateOf(LogFilter.All) }
    val time = SimpleDateFormat("HH:mm:ss", Locale.getDefault())
    LincScaffold(
        title = "Logs",
        onBack = onBack,
        actions = {
            IconButton(onClick = { LogStore.clear() }, enabled = entries.isNotEmpty()) {
                Icon(LincSymbols.Delete, contentDescription = "Clear the log")
            }
        },
        floatingActionButton = {
            val notify = rememberNotify()
            ExtendedFloatingActionButton(
                text = { Text("Copy log") },
                icon = { Icon(LincSymbols.Copy, contentDescription = null) },
                onClick = {
                    // A report someone else can read: phone, background settings, why Android
                    // ended Linc recently, and the log including earlier runs (D-073).
                    context.getSystemService(ClipboardManager::class.java)
                        .setPrimaryClip(ClipData.newPlainText("Linc log", ConnectionWatch.report(context)))
                    notify("Log copied — paste it to whoever is helping you")
                },
            )
        },
    ) { inner ->
        val shown = entries.asReversed().filter {
            when (filter) {
                LogFilter.All -> true
                LogFilter.Warnings -> it.level != LogLevel.INFO
                LogFilter.Errors -> it.level == LogLevel.ERROR
            }
        }
        LazyColumn(contentPadding = pagePadding(inner)) {
            item("filters") {
                Row(
                    Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = Kit.gutter, vertical = 8.dp),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    LogFilter.entries.forEach { f ->
                        FilterChip(selected = filter == f, onClick = { filter = f }, label = { Text(f.label) })
                    }
                }
            }
            if (shown.isEmpty()) {
                item("empty") { EmptyState(if (entries.isEmpty()) "Nothing logged yet" else "Nothing here", "Connections, syncs and problems show up here.", LincSymbols.Logs) }
            }
            items(shown) { entry ->
                Column(Modifier.fillMaxWidth().padding(horizontal = Kit.gutter, vertical = 4.dp)) {
                    Text(
                        "${time.format(entry.timestamp)}  ${entry.level.name.padEnd(5)}",
                        style = MaterialTheme.typography.labelSmall.copy(fontFamily = FontFamily.Monospace),
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    Text(
                        entry.message,
                        style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                        color = when (entry.level) {
                            LogLevel.ERROR -> MaterialTheme.colorScheme.error
                            LogLevel.WARN -> MaterialTheme.colorScheme.tertiary
                            LogLevel.INFO -> MaterialTheme.colorScheme.onSurface
                        },
                    )
                }
            }
        }
    }
}

@Preview(name = "Logs — light", widthDp = 360, heightDp = 700)
@Composable
private fun LogsPreview() = LincTheme {
    LogsContent(listOf(
        LogEntry(0, LogLevel.INFO, "Connected to linc-desktop (protocol v24)"),
        LogEntry(1, LogLevel.WARN, "Direct connection to the PC ended after 84 s (SocketException: Software caused connection abort)"),
        LogEntry(2, LogLevel.ERROR, "Couldn't save the clipboard history: IOException"),
    ), {})
}

@Preview(name = "Logs — empty, dark", widthDp = 360, heightDp = 700)
@Composable
private fun LogsPreviewEmpty() = LincTheme(darkTheme = true) { LogsContent(emptyList(), {}) }
