@file:OptIn(androidx.compose.material3.ExperimentalMaterial3ExpressiveApi::class, androidx.compose.material3.ExperimentalMaterial3Api::class)

package app.linc.android.ui

import android.Manifest
import android.content.ActivityNotFoundException
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.os.Environment
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import app.linc.android.BuildConfig
import app.linc.android.service.TestFeed
import app.linc.android.service.AdbArmOutcome
import app.linc.android.service.AdbArmRules
import app.linc.android.service.AdbArming
import app.linc.android.service.CallProvider
import app.linc.android.service.ClipHistory
import app.linc.android.service.ClipboardSettings
import app.linc.android.service.ConnectionWatch
import app.linc.android.service.Explanations
import app.linc.android.service.LincNotificationListener
import app.linc.android.service.ProjectLinks
import app.linc.android.service.MirrorSettings
import app.linc.android.service.NfcTap
import app.linc.android.service.SmsProvider
import app.linc.android.service.TransportStore
import app.linc.android.service.UpdateCheck
import app.linc.android.service.UpdateSettings
import app.linc.android.ui.kit.LincAlertDialog
import app.linc.android.ui.kit.Chevron
import app.linc.android.ui.kit.ConnectedChoice
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.LincScaffold
import app.linc.android.ui.kit.SectionHeader
import app.linc.android.ui.kit.SettingsGroup
import app.linc.android.ui.kit.SettingsRow
import app.linc.android.ui.kit.pagePadding
import app.linc.android.ui.kit.rememberNotify
import app.linc.android.ui.theme.AppFont
import app.linc.android.ui.theme.AppFontSetting
import app.linc.android.ui.theme.FloatingNav
import app.linc.android.ui.theme.LincTheme
import app.linc.android.ui.theme.headingFamily

/**
 * Settings (1.0.0.11, D-082): one grouped list, in the order of the spec — Connection, Features
 * shared with the PC, Clipboard, Mirror PC, Appearance, Updates, Help & diagnostics, About.
 * Every permission row is always present and says whether it is granted ("disable, never hide").
 */
@Composable
fun SettingsScreen(onBack: () -> Unit, onOpenLogs: () -> Unit, onRerunSetup: () -> Unit) {
    val context = LocalContext.current
    var tick by remember { mutableIntStateOf(0) }
    // Poll the grants so rows update after a trip to Android's settings.
    LaunchedEffect(Unit) { while (true) { kotlinx.coroutines.delay(1_500); tick++ } }

    var presence by remember { mutableStateOf(TransportStore.presenceEnabled(context)) }
    var engine by remember { mutableStateOf(app.linc.android.service.link.LinkStore.engineOn(context)) }
    var clipSync by remember { mutableStateOf(ClipboardSettings.syncEnabled(context)) }
    var keepHistory by remember { mutableStateOf(ClipboardSettings.keepHistory(context)) }
    var historySize by remember { mutableIntStateOf(ClipboardSettings.historySize(context)) }
    var skipSensitive by remember { mutableStateOf(ClipboardSettings.skipSensitive(context)) }
    var quality by remember { mutableStateOf(MirrorSettings.get(context)) }
    var pointer by remember { mutableStateOf(MirrorSettings.pointerMode(context)) }
    var autoKeyboard by remember { mutableStateOf(MirrorSettings.autoKeyboard(context)) }
    var phoneMic by remember { mutableStateOf(MirrorSettings.voicePhoneMic(context)) }
    var updates by remember { mutableStateOf(UpdateSettings.updatesEnabled(context)) }
    val explanations by Explanations.flow(context).collectAsState(initial = Explanations.get(context))
    val smsPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { tick++ }
    val callPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { tick++ }

    val exempt = remember(tick) { ConnectionWatch.isBatteryExempt(context) }
    val notifications = remember(tick) { LincNotificationListener.isEnabled(context) }
    val sms = remember(tick) { SmsProvider.canRead(context) }
    val calls = remember(tick) { CallProvider.canReadLog(context) }
    val allFiles = remember(tick) { Environment.isExternalStorageManager() }
    val wallpaperBackground by app.linc.android.ui.theme.WallpaperBackground.flow(context).collectAsState()
    val floatingNav by FloatingNav.flow(context).collectAsState()
    val appFont by AppFontSetting.flow(context).collectAsState()
    var choosingFont by remember { mutableStateOf(false) }
    if (choosingFont) {
        FontDialog(
            current = appFont ?: AppFont.Default,
            onPick = { AppFontSetting.set(context, it); choosingFont = false },
            onDismiss = { choosingFont = false },
        )
    }

    LincScaffold(title = "Settings", onBack = onBack) { inner ->
        val notify = rememberNotify()
        LazyColumn(contentPadding = pagePadding(inner, bottomExtra = 32.dp)) {
            item { SectionHeader("Connection") }
            item {
                SettingsGroup(rows = listOf(
                    { s -> SettingsRow("Background connection", s, LincSymbols.Wifi,
                        supporting = "Find your PC on Wi-Fi by itself, without a cable.",
                        trailing = { Switch(presence, { presence = it; TransportStore.savePresenceEnabled(context, it) }) }) },
                    { s -> SettingsRow("New connection engine", s, LincSymbols.Link,
                        supporting = "Finds your PC on any Wi-Fi; short drops don't show. Off: the old way.",
                        trailing = { Switch(engine, {
                            engine = it
                            app.linc.android.service.link.LinkStore.setEngineOn(context, it)
                            restartCompanionIfRunning(context)
                        }) }) },
                    { s -> SettingsRow("Stay connected in the background", s, LincSymbols.BatteryAlert,
                        value = if (exempt) "Allowed" else "Not allowed",
                        supporting = if (exempt) null else "Android may cut the link to your PC when the screen is off.",
                        onClick = if (exempt) null else ({ ConnectionWatch.requestBatteryExemption(context) }),
                        trailing = if (exempt) null else ({ TextButton(onClick = { ConnectionWatch.requestBatteryExemption(context) }) { Text("Allow") } })) },
                    { s -> SettingsRow("Phone maker's auto-start", s, LincSymbols.PowerOff,
                        supporting = if (ConnectionWatch.hasMakerBackgroundManager) "This phone has its own battery manager — turn Linc on there too."
                        else "Opens Linc's app info, where background use can be checked.",
                        onClick = { ConnectionWatch.openMakerBackgroundSettings(context) }, trailing = { Chevron() }) },
                    { s -> SettingsRow("Hotspot quick link", s, LincSymbols.Hotspot,
                        value = when {
                            !AdbArming.isPermissionHeld(context) -> "Not set up"
                            AdbArming.isArmed(context) -> "Ready — wireless debugging is on"
                            else -> "Ready"
                        },
                        supporting = if (!AdbArming.isPermissionHeld(context)) AdbArmRules.describe(AdbArmOutcome.DENIED)
                        else "Your PC connects the moment you share a hotspot either way.") },
                ))
            }

            item { SectionHeader("Features shared with your PC") }
            item {
                SettingsGroup(rows = listOf(
                    { s -> GrantRow(s, "Notifications", LincSymbols.Notifications, notifications,
                        "Your PC shows this phone's notifications.") { openSafely(context, Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS)) } },
                    { s -> GrantRow(s, "Messages", LincSymbols.Sms, sms, "Read and send texts from your PC.") {
                        smsPermission.launch(arrayOf(Manifest.permission.READ_SMS, Manifest.permission.SEND_SMS, Manifest.permission.RECEIVE_SMS)) } },
                    { s -> GrantRow(s, "Calls", LincSymbols.Call, calls, "See calls and dial from your PC.") {
                        callPermission.launch(arrayOf(Manifest.permission.READ_CALL_LOG, Manifest.permission.CALL_PHONE,
                            Manifest.permission.READ_PHONE_STATE, Manifest.permission.ANSWER_PHONE_CALLS)) } },
                    { s -> SettingsRow("Clipboard sync", s, LincSymbols.Clipboard,
                        supporting = "Copy on one device, paste on the other.",
                        trailing = { Switch(clipSync, { clipSync = it; ClipboardSettings.setSyncEnabled(context, it) }) }) },
                    { s -> GrantRow(s, "All files access", LincSymbols.Folder, allFiles,
                        "Send files to your PC, browse this phone's files from it, and show its wallpaper.") { openAllFilesSettings(context) } },
                ))
            }

            item { SectionHeader("Clipboard") }
            item {
                SettingsGroup(rows = listOf(
                    { s -> SettingsRow("Keep history", s, LincSymbols.History,
                        supporting = if (keepHistory) "Saved on this phone only." else "Clips sync but aren't kept.",
                        trailing = { Switch(keepHistory, { keepHistory = it; ClipboardSettings.setKeepHistory(context, it) }) }) },
                    { s -> SettingsRow("History size", s, LincSymbols.ClearAll, value = "%,d items".format(historySize), enabled = keepHistory) },
                    { s -> SettingsRow("Don't save sensitive copies", s, LincSymbols.VisibilityOff,
                        supporting = "Passwords and codes apps mark as sensitive are never saved or sent.",
                        trailing = { Switch(skipSensitive, { skipSensitive = it; ClipboardSettings.setSkipSensitive(context, it) }) }) },
                ))
            }
            item {
                ConnectedChoice(
                    options = ClipHistory.SIZES.map { it to "%,d".format(it) },
                    selected = historySize,
                    onSelect = { historySize = it; ClipboardSettings.setHistorySize(context, it) },
                    enabled = keepHistory,
                    modifier = Modifier.padding(horizontal = Kit.gutter, vertical = 8.dp),
                )
            }

            item { SectionHeader("Mirror PC") }
            item {
                SettingsGroup(rows = listOf(
                    { s -> SettingsRow("Picture quality", s, LincSymbols.Tune, value = quality.label, supporting = quality.blurb) },
                    { s -> SettingsRow("Pointer", s, LincSymbols.Mouse,
                        value = if (pointer == MirrorSettings.PointerMode.TOUCH) "Touch — the pointer goes where you touch" else "Mouse — drag like a trackpad") },
                    { s -> SettingsRow("Keyboard on text fields", s, LincSymbols.Keyboard,
                        supporting = "Show the keyboard when a text box on the PC is selected.",
                        trailing = { Switch(autoKeyboard, { autoKeyboard = it; MirrorSettings.setAutoKeyboard(context, it) }) }) },
                ))
            }
            item {
                Column(Modifier.padding(horizontal = Kit.gutter, vertical = 8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    ConnectedChoice(MirrorSettings.Quality.entries.map { it to it.label }, quality, { quality = it; MirrorSettings.set(context, it) })
                    ConnectedChoice(listOf(MirrorSettings.PointerMode.TOUCH to "Touch", MirrorSettings.PointerMode.MOUSE to "Mouse"),
                        pointer, { pointer = it; MirrorSettings.setPointerMode(context, it) })
                    Text("Which microphone the PC hears", style = androidx.compose.material3.MaterialTheme.typography.titleSmall)
                    ConnectedChoice(listOf(true to "This phone", false to "The PC's own"), phoneMic,
                        { phoneMic = it; MirrorSettings.setVoicePhoneMic(context, it) })
                }
            }

            item { SectionHeader("Appearance") }
            item {
                SettingsGroup(rows = listOf(
                    { s -> SettingsRow("Show explanations", s, LincSymbols.Info,
                        supporting = "The short lines that explain each setting. Warnings and errors always show.",
                        trailing = { Switch(explanations, { Explanations.set(context, it) }) }) },
                    { s -> SettingsRow("Wallpaper background", s, LincSymbols.Wallpaper,
                        supporting = if (allFiles) "Your wallpaper, blurred and frosted, behind every screen."
                            else "Your wallpaper, blurred, behind every screen. Needs All files access (above) to read it.",
                        trailing = { Switch(wallpaperBackground == true, { app.linc.android.ui.theme.WallpaperBackground.set(context, it) }) }) },
                    { s -> SettingsRow("Floating navigation bar", s, LincSymbols.TaskView,
                        supporting = "The tabs as a floating pill above the bottom edge, instead of a full-width bar.",
                        trailing = { Switch(floatingNav == true, { FloatingNav.set(context, it) }) }) },
                    { s -> SettingsRow("Font", s, LincSymbols.TextSnippet,
                        value = (appFont ?: AppFont.Default).label,
                        supporting = "The typeface used across Linc.",
                        onClick = { choosingFont = true }, trailing = { Chevron() }) },
                ))
            }

            item { SectionHeader("Updates") }
            item {
                SettingsGroup(rows = listOf(
                    { s -> SettingsRow("Keep Linc up to date", s, LincSymbols.SystemUpdate,
                        supporting = if (updates) "Linc checks for a new version when you open it." else "Get new versions from ${UpdateSettings.RELEASES_URL}",
                        trailing = { Switch(updates, { updates = it; UpdateSettings.setUpdatesEnabled(context, it) }) }) },
                    { s -> SettingsRow("Check now", s, LincSymbols.Refresh, enabled = updates,
                        onClick = { UpdateCheck.check(context, force = true) { notify(it) } }, trailing = { Chevron() }) },
                ))
            }

            item { SectionHeader("Help & diagnostics") }
            item {
                val nfc = (context as? android.app.Activity)?.let { NfcTap.isSupported(it) } == true
                SettingsGroup(rows = listOfNotNull(
                    { s -> SettingsRow("Logs", s, LincSymbols.Logs, supporting = "What Linc has been doing.", onClick = onOpenLogs, trailing = { Chevron() }) },
                    { s -> SettingsRow("Copy log", s, LincSymbols.Copy,
                        supporting = "A report to paste to whoever is helping you.",
                        onClick = {
                            context.getSystemService(ClipboardManager::class.java)
                                .setPrimaryClip(ClipData.newPlainText("Linc log", ConnectionWatch.report(context)))
                            notify("Log copied")
                        }) },
                    if (nfc) { s -> NfcRow(s) } else null,
                    { s -> SettingsRow("Run setup again", s, LincSymbols.Restart, onClick = onRerunSetup, trailing = { Chevron() }) },
                ))
            }

            item { SectionHeader("About") }
            item {
                SettingsGroup(rows = listOf(
                    { s -> SettingsRow("Linc for Android", s, LincSymbols.Smartphone, value = "Version ${BuildConfig.VERSION_NAME}" +
                        if (BuildConfig.TEST_BUILD > 0) " · ${TestFeed.label(BuildConfig.TEST_BUILD, BuildConfig.TEST_NAME)}" else "") },
                    { s -> SettingsRow("Developer", s, LincSymbols.Info, value = "deloweProjects (${ProjectLinks.DEVELOPER}, ${ProjectLinks.DEVELOPER_HANDLE})",
                        onClick = { openLink(context, ProjectLinks.DEVELOPER_GITHUB) }) },
                    { s -> SettingsRow("Telegram channel", s, LincSymbols.Send, supporting = "News, new versions and help — t.me/LincApp",
                        onClick = { openLink(context, ProjectLinks.TELEGRAM) }, trailing = { Chevron() }) },
                    { s -> SettingsRow("Source code on GitHub", s, LincSymbols.OpenInNew, supporting = "Releases, issues and the code — github.com/deloweProjects/Linc",
                        onClick = { openLink(context, ProjectLinks.GITHUB) }, trailing = { Chevron() }) },
                ))
            }
        }
    }
}

/** A permission row: granted state as the value, and the fix as a button when not granted. */
@Composable
private fun GrantRow(
    segment: app.linc.android.ui.kit.Segment,
    title: String,
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    granted: Boolean,
    what: String,
    fix: () -> Unit,
) {
    SettingsRow(
        title = title,
        segment = segment,
        icon = icon,
        value = if (granted) "Allowed" else "Not allowed",
        supporting = what,
        onClick = if (granted) null else fix,
        trailing = if (granted) null else ({ FilledTonalButton(onClick = fix) { Text("Allow") } }),
    )
}

/** The font choices, each name drawn in its own font (headings' font for the serif pairing). */
@Composable
private fun FontDialog(current: AppFont, onPick: (AppFont) -> Unit, onDismiss: () -> Unit) {
    LincAlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Font") },
        text = {
            Column(Modifier.selectableGroup()) {
                AppFont.entries.forEach { f ->
                    Row(
                        Modifier
                            .fillMaxWidth()
                            .selectable(selected = f == current, onClick = { onPick(f) }, role = Role.RadioButton)
                            .padding(vertical = 8.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        RadioButton(selected = f == current, onClick = null)
                        Column(Modifier.padding(start = 16.dp)) {
                            // Default previews the phone's own font even while another is chosen.
                            val family = f.headingFamily() ?: FontFamily.Default
                            Text(f.label, style = MaterialTheme.typography.titleLarge.copy(fontFamily = family))
                            Text(f.blurb, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("Close") } },
    )
}

@Composable
private fun NfcRow(segment: app.linc.android.ui.kit.Segment) {
    val activity = LocalContext.current as? android.app.Activity ?: return
    val state by NfcTap.writeState.collectAsState()
    DisposableEffect(Unit) { onDispose { NfcTap.cancelWrite(activity) } }
    SettingsRow(
        title = "Tap to connect (NFC tag)",
        segment = segment,
        icon = LincSymbols.Nfc,
        value = when (val s = state) {
            NfcTap.WriteState.Idle -> null
            NfcTap.WriteState.Waiting -> "Hold the tag against the back of the phone…"
            NfcTap.WriteState.Written -> "Done — put the tag by your PC"
            is NfcTap.WriteState.Failed -> s.reason
        },
        supporting = "Write a blank NFC tag once; tapping it connects Linc without opening the app.",
        onClick = {
            if (state == NfcTap.WriteState.Waiting) NfcTap.cancelWrite(activity)
            else { NfcTap.resetWriteState(); NfcTap.beginWrite(activity) }
        },
        trailing = { Text(if (state == NfcTap.WriteState.Waiting) "Cancel" else "Write", color = androidx.compose.material3.MaterialTheme.colorScheme.primary) },
    )
}

private fun openLink(context: android.content.Context, url: String) =
    openSafely(context, Intent(Intent.ACTION_VIEW, android.net.Uri.parse(url)))

private fun openSafely(context: android.content.Context, intent: Intent) {
    try { context.startActivity(intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) } catch (_: ActivityNotFoundException) { /* no screen on this OEM build */ }
}


@Preview(name = "Settings — light", widthDp = 360, heightDp = 2400)
@Composable
private fun SettingsPreviewLight() = LincTheme(darkTheme = false) { SettingsScreen({}, {}, {}) }

@Preview(name = "Settings — dark", widthDp = 411, heightDp = 2400)
@Composable
private fun SettingsPreviewDark() = LincTheme(darkTheme = true) { SettingsScreen({}, {}, {}) }

@Preview(name = "Settings — large font", widthDp = 360, heightDp = 3200, fontScale = 2f)
@Composable
private fun SettingsPreviewLargeFont() = LincTheme { SettingsScreen({}, {}, {}) }
