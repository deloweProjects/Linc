@file:OptIn(androidx.compose.material3.ExperimentalMaterial3ExpressiveApi::class, androidx.compose.material3.ExperimentalMaterial3Api::class)

package app.linc.android.ui

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.SystemClock
import android.text.format.DateUtils
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import app.linc.android.service.CompanionService
import app.linc.android.service.CompanionStateHolder
import app.linc.android.service.CompanionStateHolder.ServiceState
import app.linc.android.service.MirrorControl
import app.linc.android.service.MirrorSession
import app.linc.android.service.ReconnectSignal
import app.linc.android.ui.kit.LincBottomSheet
import app.linc.android.ui.kit.HelperText
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.SettingsGroup
import app.linc.android.ui.kit.SettingsRow

/**
 * The Connection sheet (1.0.0.11, D-082; was the Status tab): what the link is, in rows whose
 * technical values live in the supporting line, plus Reconnect and Start/Stop.
 */
@Composable
fun ConnectionSheet(onDismiss: () -> Unit) {
    val context = LocalContext.current
    val state by CompanionStateHolder.state.collectAsState()
    val link by CompanionStateHolder.link.collectAsState()
    val pcLink by CompanionStateHolder.pcLink.collectAsState()
    val rtt by MirrorSession.rttMs.collectAsState()
    val connected = state as? ServiceState.Connected
    val notificationPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
        startCompanion(context)
    }

    // Time the link while the sheet is open: v20's mirror ping is answered by the PC at once.
    LaunchedEffect(connected) {
        while (connected != null && connected.protocolVersion >= 20) {
            MirrorControl.ping(SystemClock.elapsedRealtime())
            kotlinx.coroutines.delay(2_000)
        }
    }

    LincBottomSheet(onDismissRequest = onDismiss) {
        Column(Modifier.fillMaxWidth().padding(bottom = 24.dp), verticalArrangement = Arrangement.spacedBy(Kit.groupGap)) {
            Text("Connection", style = MaterialTheme.typography.headlineSmall, modifier = Modifier.padding(horizontal = Kit.gutter))
            SettingsGroup(rows = listOfNotNull(
                { s -> SettingsRow("PC", s, LincSymbols.Computer, value = connected?.host ?: if (connected != null) "Your PC" else "Not connected") },
                { s -> SettingsRow("Connection", s, LincSymbols.Wifi, value = when (state) {
                    is ServiceState.Connected -> transportLabel(context, link, pcLink) ?: "Connected"
                    is ServiceState.Listening -> "Looking for your PC"
                    is ServiceState.Stopped -> "Linc isn't running"
                }) },
                // What the PC says about ADB (v32 link.state); absent from an older PC.
                pcLink?.takeIf { connected != null }?.let { p -> { s -> SettingsRow("ADB", s, LincSymbols.Usb, value = when {
                    p.arming -> "Turning on…"
                    !p.adb && !app.linc.android.service.AdbArming.isPermissionHeld(context) -> "Not up · grant needed: plug in the cable once"
                    !p.adb -> "Not up"
                    p.adbVia == "cable" -> "Up, over the cable"
                    p.adbVia == "wireless" -> "Up, over wireless debugging"
                    else -> "Up"
                }) } },
                link?.address?.let { address -> { s -> SettingsRow("Address", s, LincSymbols.Link, value = address) } },
                connected?.let { c -> { s -> SettingsRow("Linc on the PC", s, LincSymbols.Info, value = "Version ${c.desktopVersion}", supporting = "Protocol v${c.protocolVersion}") } },
                if (connected != null && rtt >= 0) { s -> SettingsRow("Round trip", s, LincSymbols.Schedule, value = "$rtt ms") } else null,
                link?.let { l -> { s -> SettingsRow("Connected", s, LincSymbols.History,
                    value = DateUtils.getRelativeTimeSpanString(l.since, System.currentTimeMillis(), DateUtils.MINUTE_IN_MILLIS).toString()) } },
            ))
            PcsSection()
            if (state is ServiceState.Listening) {
                HelperText(
                    "Linc finds your PC on the same Wi-Fi, over the USB cable, or on the PC's hotspot. Keep Linc open on the PC." +
                        // LincLink's pairing by code: only with the new engine (and a PC that has it).
                        if (app.linc.android.service.link.LinkStore.engineOn(context)) {
                            " No cable, and a PC this phone hasn't met? On the PC, choose Add a phone, then \"Wi-Fi — scan a QR code\", " +
                                "and point this phone's camera at the top code."
                        } else "",
                    modifier = Modifier.padding(horizontal = Kit.gutter),
                )
            }
            Row(Modifier.fillMaxWidth().padding(horizontal = Kit.gutter), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                FilledTonalButton(
                    onClick = { ReconnectSignal.poke("you asked to reconnect") },
                    enabled = state !is ServiceState.Stopped,
                    shapes = ButtonDefaults.shapes(),
                    modifier = Modifier.weight(1f).heightIn(min = 56.dp),
                ) { Text("Reconnect") }
                Button(
                    onClick = {
                        if (state is ServiceState.Stopped) {
                            if (needsNotificationPermission(context)) notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
                            else startCompanion(context)
                        } else {
                            // Stopped by hand: a restart or opening Linc leaves it stopped (LincLink).
                            app.linc.android.service.link.LinkStore.setUserStopped(context, true)
                            context.stopService(Intent(context, CompanionService::class.java))
                        }
                    },
                    shapes = ButtonDefaults.shapes(),
                    modifier = Modifier.weight(1f).heightIn(min = 56.dp),
                ) { Text(if (state is ServiceState.Stopped) "Start Linc" else "Stop Linc") }
            }
        }
    }
}

internal fun needsNotificationPermission(context: Context): Boolean =
    Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
        ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED

/** Apply a connection-engine change: Linc's service restarts with it (a running link reconnects). */
internal fun restartCompanionIfRunning(context: Context) {
    if (app.linc.android.service.CompanionStateHolder.state.value is ServiceState.Stopped) return
    context.stopService(Intent(context, CompanionService::class.java))
    startCompanion(context)
}

internal fun startCompanion(context: Context) {
    app.linc.android.service.link.LinkStore.setUserStopped(context, false)
    runCatching { context.startForegroundService(Intent(context, CompanionService::class.java)) }
}
