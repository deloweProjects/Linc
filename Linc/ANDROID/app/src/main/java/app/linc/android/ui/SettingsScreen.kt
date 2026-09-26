package app.linc.android.ui

import android.content.ActivityNotFoundException
import android.content.Intent
import android.net.Uri
import android.os.Environment
import android.provider.Settings
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.ui.Alignment
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import app.linc.android.R
import app.linc.android.ui.theme.Dimens
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import app.linc.android.service.AdbArmOutcome
import app.linc.android.service.UpdateSettings
import app.linc.android.service.AdbArmRules
import app.linc.android.service.AdbArming
import app.linc.android.service.CallProvider
import app.linc.android.service.LincNotificationListener
import app.linc.android.service.MirrorSettings
import app.linc.android.service.SmsProvider
import app.linc.android.service.TransportStore
import kotlinx.coroutines.delay

@Composable
fun SettingsScreen(onRerunSetup: () -> Unit, onOpenLogs: () -> Unit = {}) {
    val context = LocalContext.current
    var quality by remember { mutableStateOf(MirrorSettings.get(context)) }

    // Poll the grants so the rows update after a trip to Settings.
    var notifAccess by remember { mutableStateOf(LincNotificationListener.isEnabled(context)) }
    var wallpaperAccess by remember { mutableStateOf(Environment.isExternalStorageManager()) }
    var presenceEnabled by remember { mutableStateOf(TransportStore.presenceEnabled(context)) }
    var smsAccess by remember { mutableStateOf(SmsProvider.canRead(context)) }
    val smsPermission = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { smsAccess = SmsProvider.canRead(context) }
    var callAccess by remember { mutableStateOf(CallProvider.canReadLog(context)) }
    val callPermission = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { callAccess = CallProvider.canReadLog(context) }
    // M13c §2.1: the one-time WRITE_SECURE_SETTINGS grant, and whether adbd is armed right now.
    var selfArmGranted by remember { mutableStateOf(AdbArming.isPermissionHeld(context)) }
    var adbArmed by remember { mutableStateOf(AdbArming.isArmed(context)) }
    // M19 C3: "Keep Linc up to date", default ON. The manifest address is a build-time constant
    // now (UpdateSettings.MANIFEST_URL), so this flag is the only update setting there is.
    var updatesEnabled by remember { mutableStateOf(UpdateSettings.updatesEnabled(context)) }
    LaunchedEffect(Unit) {
        while (true) {
            notifAccess = LincNotificationListener.isEnabled(context)
            wallpaperAccess = Environment.isExternalStorageManager()
            presenceEnabled = TransportStore.presenceEnabled(context)
            smsAccess = SmsProvider.canRead(context)
            callAccess = CallProvider.canReadLog(context)
            selfArmGranted = AdbArming.isPermissionHeld(context)
            adbArmed = AdbArming.isArmed(context)
            delay(2_000)
        }
    }

    LincScreen(title = stringResource(R.string.nav_settings)) {

        LincCard(title = stringResource(R.string.settings_permissions_header)) {
            GrantRow(
                granted = notifAccess,
                onText = stringResource(R.string.notif_access_on),
                offText = stringResource(R.string.notif_access_off),
                actionLabel = stringResource(R.string.notif_access_action),
                onAction = {
                    try {
                        context.startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
                    } catch (_: ActivityNotFoundException) {
                        // No settings screen on this OEM build; nothing else we can do.
                    }
                },
            )
            GrantRow(
                granted = wallpaperAccess,
                onText = stringResource(R.string.wallpaper_access_on),
                offText = stringResource(R.string.wallpaper_access_off),
                actionLabel = stringResource(R.string.wallpaper_access_action),
                onAction = {
                    try {
                        context.startActivity(Intent(
                            Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION,
                            Uri.fromParts("package", context.packageName, null),
                        ))
                    } catch (_: ActivityNotFoundException) {
                        runCatching {
                            context.startActivity(Intent(Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION))
                        }
                    }
                },
            )
            GrantRow(
                granted = smsAccess,
                onText = stringResource(R.string.sms_access_on),
                offText = stringResource(R.string.sms_access_off),
                actionLabel = stringResource(R.string.sms_access_action),
                onAction = {
                    smsPermission.launch(arrayOf(
                        android.Manifest.permission.READ_SMS,
                        android.Manifest.permission.SEND_SMS,
                        android.Manifest.permission.RECEIVE_SMS,
                    ))
                },
            )
            GrantRow(
                granted = callAccess,
                onText = stringResource(R.string.call_access_on),
                offText = stringResource(R.string.call_access_off),
                actionLabel = stringResource(R.string.call_access_action),
                onAction = {
                    callPermission.launch(arrayOf(
                        android.Manifest.permission.READ_CALL_LOG,
                        android.Manifest.permission.CALL_PHONE,
                        android.Manifest.permission.READ_PHONE_STATE,
                        android.Manifest.permission.ANSWER_PHONE_CALLS,
                    ))
                },
            )

            Row(
                modifier = Modifier.fillMaxWidth().heightIn(min = Dimens.touchTarget),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = stringResource(
                        if (presenceEnabled) R.string.presence_on else R.string.presence_off),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.weight(1f),
                )
                Switch(
                    checked = presenceEnabled,
                    onCheckedChange = {
                        presenceEnabled = it
                        TransportStore.savePresenceEnabled(context, it)
                    },
                )
            }
        }

        // M13c 2.1: disable, don't hide. The instant-hotspot-link card is always visible and
        // always says why it can't work, rather than vanishing on a phone whose OEM refused
        // the one-time grant. There is no toggle here on purpose - arming is something the
        // PC asks for over the protocol, not a switch that silently changes the device.
        LincCard(title = stringResource(R.string.settings_hotspot_header)) {
            Text(
                when {
                    !selfArmGranted -> AdbArmRules.describe(AdbArmOutcome.DENIED)
                    adbArmed -> "Ready. Wireless debugging is on, so your PC connects the moment " +
                        "you share a hotspot either way."
                    else -> "Ready. Linc will switch wireless debugging on for you when your PC asks."
                },
                style = MaterialTheme.typography.bodyMedium,
                // Not-granted is informational, not an error: red here made a normal,
                // perfectly usable phone look broken.
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }

        NfcTapCard()

        LincCard(title = stringResource(R.string.settings_mirror_header)) {
            Text(
                quality.blurb,
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Row(horizontalArrangement = Arrangement.spacedBy(Dimens.s)) {
                MirrorSettings.Quality.entries.forEach { option ->
                    FilterChip(
                        modifier = Modifier.heightIn(min = Dimens.touchTarget),
                        selected = quality == option,
                        onClick = {
                            quality = option
                            MirrorSettings.set(context, option)
                        },
                        label = { Text(option.label) },
                    )
                }
            }
            Text(
                "Applies the next time you open Mirror PC.",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }

        // M19 C3: one switch, default ON. Off means the update code path cannot execute at all -
        // no request, no UI, nothing in the logs - and the card then says where to get updates by
        // hand instead. There is nothing else to configure: the address is baked into the build.
        LincCard(title = stringResource(R.string.settings_updates_header)) {
            Row(
                modifier = Modifier.fillMaxWidth().heightIn(min = Dimens.touchTarget),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = stringResource(R.string.settings_updates_label),
                    style = MaterialTheme.typography.bodyMedium,
                    modifier = Modifier.weight(1f),
                )
                Switch(
                    checked = updatesEnabled,
                    onCheckedChange = {
                        updatesEnabled = it
                        UpdateSettings.setUpdatesEnabled(context, it)
                    },
                )
            }
            Text(
                stringResource(
                    if (updatesEnabled) R.string.settings_updates_on
                    else R.string.settings_updates_off
                ),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }

        LincCard(title = stringResource(R.string.settings_about_header)) {
            OutlinedButton(
                modifier = Modifier.fillMaxWidth().heightIn(min = Dimens.touchTarget),
                onClick = onOpenLogs,
            ) {
                Text(stringResource(R.string.action_view_logs))
            }
            TextButton(
                modifier = Modifier.fillMaxWidth().heightIn(min = Dimens.touchTarget),
                onClick = onRerunSetup,
            ) {
                Text(stringResource(R.string.action_rerun_setup))
            }
        }
    }
}

/**
 * M17a B1: one row shape for every permission - the granted sentence, or the ungranted
 * sentence plus the button that fixes it. "Disable, never hide" is unchanged: the row is
 * always present either way, and it always says which state it is in.
 */
@Composable
private fun GrantRow(
    granted: Boolean,
    onText: String,
    offText: String,
    actionLabel: String,
    onAction: () -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(Dimens.s)) {
        Text(
            if (granted) onText else offText,
            style = MaterialTheme.typography.bodyMedium,
            color = if (granted) {
                MaterialTheme.colorScheme.onSurfaceVariant
            } else {
                MaterialTheme.colorScheme.onSurface
            },
        )
        if (!granted) {
            OutlinedButton(
                modifier = Modifier.fillMaxWidth().heightIn(min = Dimens.touchTarget),
                onClick = onAction,
            ) {
                Text(actionLabel)
            }
        }
    }
}

/**
 * Tap-to-connect: write a cheap NFC sticker once, put it by the PC, and tapping it connects Linc
 * straight away. Hidden entirely on a phone with no NFC, rather than showing a dead button.
 */
@Composable
private fun NfcTapCard() {
    val activity = LocalContext.current as? android.app.Activity ?: return
    if (!app.linc.android.service.NfcTap.isSupported(activity)) return
    val state by app.linc.android.service.NfcTap.writeState.collectAsState()
    DisposableEffect(Unit) {
        onDispose { app.linc.android.service.NfcTap.cancelWrite(activity) }
    }
    LincCard(title = "Tap to connect (NFC)") {
        Text(
            when (val s = state) {
                app.linc.android.service.NfcTap.WriteState.Idle ->
                    "Stick a blank NFC tag (NTAG213 or bigger) by your PC. Write it once here, then tapping " +
                        "your phone on it connects Linc instantly — no need to open the app."
                app.linc.android.service.NfcTap.WriteState.Waiting ->
                    "Hold the tag against the back of the phone…"
                app.linc.android.service.NfcTap.WriteState.Written ->
                    "Done. Put the tag by your PC and tap your phone on it to connect."
                is app.linc.android.service.NfcTap.WriteState.Failed -> s.reason
            },
            style = MaterialTheme.typography.bodyMedium,
            color = if (state is app.linc.android.service.NfcTap.WriteState.Failed) MaterialTheme.colorScheme.error
            else MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Row(horizontalArrangement = Arrangement.spacedBy(Dimens.s)) {
            if (state == app.linc.android.service.NfcTap.WriteState.Waiting) {
                OutlinedButton(
                    modifier = Modifier.heightIn(min = Dimens.touchTarget),
                    onClick = { app.linc.android.service.NfcTap.cancelWrite(activity) },
                ) { Text("Cancel") }
            } else {
                OutlinedButton(
                    modifier = Modifier.heightIn(min = Dimens.touchTarget),
                    onClick = {
                        app.linc.android.service.NfcTap.resetWriteState()
                        app.linc.android.service.NfcTap.beginWrite(activity)
                    },
                ) { Text(if (state == app.linc.android.service.NfcTap.WriteState.Written) "Write another tag" else "Write a tag") }
            }
        }
    }
}
