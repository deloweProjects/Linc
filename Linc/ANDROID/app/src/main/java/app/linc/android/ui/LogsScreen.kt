package app.linc.android.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import app.linc.android.R
import app.linc.android.service.LogLevel
import app.linc.android.service.LogStore
import app.linc.android.ui.theme.Dimens
import java.text.SimpleDateFormat
import java.util.Locale

private val timeFormat = SimpleDateFormat("HH:mm:ss", Locale.getDefault())

@Composable
fun LogsScreen(onBack: () -> Unit = {}) {
    val entries by LogStore.entries.collectAsState()

    LincScreen(
        title = stringResource(R.string.nav_logs),
        navigationIcon = {
            IconButton(onClick = onBack, modifier = Modifier.size(Dimens.touchTarget)) {
                Icon(
                    Icons.AutoMirrored.Filled.ArrowBack,
                    contentDescription = stringResource(R.string.logs_back),
                )
            }
        },
        actions = {
            TextButton(onClick = { LogStore.clear() }) { Text(stringResource(R.string.logs_clear)) }
        },
        // The list scrolls itself (LazyColumn): 500 entries in a plain Column would measure
        // every row on every frame.
        scrollable = false,
    ) {
        if (entries.isEmpty()) {
            LincEmpty(
                icon = {
                    Icon(
                        Icons.AutoMirrored.Filled.List,
                        contentDescription = null,
                        modifier = Modifier.size(Dimens.xxl),
                        tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                },
                text = stringResource(R.string.logs_empty),
            )
        } else {
            // One card holds the whole log so the list reads as a single block instead of
            // free-floating rows on the background (B1: one card treatment everywhere).
            Surface(
                modifier = Modifier.fillMaxWidth().weight(1f),
                shape = RoundedCornerShape(Dimens.radiusCard),
                color = MaterialTheme.colorScheme.surfaceContainer,
            ) {
                LazyColumn(contentPadding = PaddingValues(Dimens.l)) {
                    items(entries.asReversed()) { entry ->
                    Row(
                        modifier = Modifier.fillMaxWidth().padding(vertical = Dimens.xs),
                        horizontalArrangement = Arrangement.spacedBy(Dimens.m),
                        verticalAlignment = Alignment.Top,
                    ) {
                        Text(
                            text = timeFormat.format(entry.timestamp),
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            textAlign = TextAlign.Start,
                            modifier = Modifier.width(Dimens.xxl * 2),
                        )
                        Text(
                            text = entry.message,
                            style = MaterialTheme.typography.bodyMedium,
                            color = if (entry.level == LogLevel.ERROR) {
                                MaterialTheme.colorScheme.error
                            } else {
                                MaterialTheme.colorScheme.onSurface
                            },
                        )
                    }
                    }
                }
            }
        }
    }
}
