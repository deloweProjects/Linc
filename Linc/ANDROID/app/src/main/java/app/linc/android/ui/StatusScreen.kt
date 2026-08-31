package app.linc.android.ui

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
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
import androidx.compose.ui.res.stringResource
import androidx.core.content.ContextCompat
import app.linc.android.R
import app.linc.android.service.CompanionService
import app.linc.android.service.CompanionStateHolder
import app.linc.android.service.CompanionStateHolder.ServiceState
import app.linc.android.service.DeviceStatusReporter
import app.linc.android.ui.theme.Dimens
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

@Composable
fun StatusScreen() {
    val context = LocalContext.current
    val serviceState by CompanionStateHolder.state.collectAsState()
    var device by remember { mutableStateOf<DeviceStatusReporter.DeviceStatus?>(null) }

    LaunchedEffect(serviceState) {
        device = withContext(Dispatchers.IO) { DeviceStatusReporter.snapshot(context) }
    }

    // The service runs without the notification permission too; the ask is only so
    // the user can see the ongoing-service notification on Android 13+.
    val notificationPermission = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { startCompanionService(context) }

    LincScreen(title = stringResource(R.string.nav_status)) {
        LincCard {
            Text(
                text = when (val s = serviceState) {
                    is ServiceState.Stopped -> stringResource(R.string.state_stopped)
                    is ServiceState.Listening -> stringResource(R.string.state_listening)
                    is ServiceState.Connected -> stringResource(R.string.state_connected, s.desktopApp)
                },
                style = MaterialTheme.typography.titleMedium,
            )
            if (serviceState is ServiceState.Listening) {
                Text(
                    stringResource(R.string.state_listening_hint),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Button(
                modifier = Modifier.fillMaxWidth().heightIn(min = Dimens.touchTarget),
                onClick = {
                    if (serviceState is ServiceState.Stopped) {
                        if (needsNotificationPermission(context)) {
                            notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
                        } else {
                            startCompanionService(context)
                        }
                    } else {
                        context.stopService(Intent(context, CompanionService::class.java))
                    }
                },
            ) {
                Text(stringResource(
                    if (serviceState is ServiceState.Stopped) R.string.action_start else R.string.action_stop
                ))
            }
        }

        LincCard(title = stringResource(R.string.status_phone_header)) {
            val d = device
            if (d == null) {
                Text(
                    stringResource(R.string.status_phone_pending),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            } else {
                InfoRow(stringResource(R.string.info_battery), stringResource(
                    if (d.charging) R.string.info_battery_charging else R.string.info_battery_value,
                    d.battery,
                ))
                InfoRow(
                    stringResource(R.string.info_storage),
                    stringResource(R.string.info_storage_value, gb(d.storageFreeBytes), gb(d.storageTotalBytes)),
                )
            }
        }
    }
}

@Composable
private fun InfoRow(label: String, value: String) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            label,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Text(value, style = MaterialTheme.typography.bodyMedium)
    }
}

private fun gb(bytes: Long): String = "%.1f".format(bytes / 1_000_000_000.0)

private fun needsNotificationPermission(context: Context): Boolean =
    Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
        ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) !=
        PackageManager.PERMISSION_GRANTED

private fun startCompanionService(context: Context) {
    context.startForegroundService(Intent(context, CompanionService::class.java))
}
