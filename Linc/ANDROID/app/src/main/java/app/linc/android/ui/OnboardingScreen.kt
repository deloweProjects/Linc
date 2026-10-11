@file:OptIn(androidx.compose.material3.ExperimentalMaterial3ExpressiveApi::class, androidx.compose.material3.ExperimentalMaterial3Api::class)

package app.linc.android.ui

import android.Manifest
import android.content.ActivityNotFoundException
import android.content.Intent
import android.os.Build
import android.os.Environment
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialShapes
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.toShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import app.linc.android.service.CallProvider
import app.linc.android.service.ConnectionWatch
import app.linc.android.service.LincNotificationListener
import app.linc.android.service.SmsProvider
import app.linc.android.ui.kit.HelperText
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.SettingsGroup
import app.linc.android.ui.kit.SettingsRow
import app.linc.android.ui.theme.LincTheme
import kotlinx.coroutines.launch

private enum class Page { Welcome, Notifications, Optional, Background, Connect, Done }

/**
 * First-run setup (1.0.0.11, D-082): one ask per page, each optional page skippable, decorative
 * Expressive shapes (clipped, never shadowed — D-072). Welcome → Notifications → Optional
 * permissions → Background → Connect to PC → Done.
 */
@Composable
fun OnboardingScreen(onDone: () -> Unit) {
    val pager = rememberPagerState { Page.entries.size }
    val scope = rememberCoroutineScope()
    val page = Page.entries[pager.currentPage]
    fun go(to: Int) = scope.launch { pager.animateScrollToPage(to.coerceIn(0, Page.entries.lastIndex)) }

    Surface(color = MaterialTheme.colorScheme.surface, modifier = Modifier.fillMaxSize()) {
        Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()) {
            Dots(current = pager.currentPage, count = Page.entries.size, modifier = Modifier.padding(top = 16.dp).align(Alignment.CenterHorizontally))
            HorizontalPager(state = pager, modifier = Modifier.weight(1f), userScrollEnabled = true) { index ->
                Column(
                    Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 24.dp, vertical = 24.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.spacedBy(16.dp),
                ) { PageContent(Page.entries[index]) }
            }
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                if (pager.currentPage > 0) {
                    TextButton(onClick = { go(pager.currentPage - 1) }, modifier = Modifier.heightIn(min = Kit.touch)) { Text("Back") }
                }
                Spacer(Modifier.weight(1f))
                if (page in setOf(Page.Notifications, Page.Optional, Page.Background)) {
                    TextButton(onClick = { go(pager.currentPage + 1) }, modifier = Modifier.heightIn(min = Kit.touch)) { Text("Skip") }
                }
                Button(
                    onClick = { if (page == Page.Done) onDone() else go(pager.currentPage + 1) },
                    shapes = ButtonDefaults.shapes(),
                    modifier = Modifier.heightIn(min = Kit.touch),
                ) { Text(if (page == Page.Done) "Start using Linc" else "Next") }
            }
        }
    }
}

@Composable
private fun Dots(current: Int, count: Int, modifier: Modifier = Modifier) {
    Row(modifier, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        repeat(count) { i ->
            Box(
                Modifier.size(width = if (i == current) 20.dp else 8.dp, height = 8.dp).clip(CircleShape)
                    .background(if (i == current) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outlineVariant),
            )
        }
    }
}

@Composable
private fun Illustration(icon: ImageVector, shape: Shape) {
    Box(
        Modifier.size(140.dp).clip(shape).background(MaterialTheme.colorScheme.primaryContainer),
        contentAlignment = Alignment.Center,
    ) { Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.onPrimaryContainer, modifier = Modifier.size(56.dp)) }
}

@Composable
private fun Title(text: String) = Text(text, style = MaterialTheme.typography.headlineMedium, textAlign = TextAlign.Center)

@Composable
private fun Body(text: String) = Text(text, style = MaterialTheme.typography.bodyLarge, textAlign = TextAlign.Center,
    color = MaterialTheme.colorScheme.onSurfaceVariant)

@Composable
private fun PageContent(page: Page) {
    val context = LocalContext.current
    var tick by remember { mutableIntStateOf(0) }
    LaunchedEffect(page) { while (true) { kotlinx.coroutines.delay(1_500); tick++ } }
    val askNotifications = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { tick++ }
    val askSms = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { tick++ }
    val askCalls = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { tick++ }

    when (page) {
        Page.Welcome -> {
            Illustration(LincSymbols.Devices, MaterialShapes.Cookie9Sided.toShape())
            Title("Your phone and PC, together")
            Body("Linc connects this phone to your Windows PC: notifications, messages and calls on the PC, the clipboard on both, files either way, and your PC in your hand.")
        }
        Page.Notifications -> {
            Illustration(LincSymbols.Notifications, MaterialShapes.Clover4Leaf.toShape())
            Title("Stay in the loop")
            Body("Linc shows a small ongoing notification while it's connected, and can show your phone's notifications on the PC.")
            val granted = remember(tick) { !needsNotificationPermission(context) }
            val listener = remember(tick) { LincNotificationListener.isEnabled(context) }
            SettingsGroup(rows = listOf(
                { s -> SettingsRow("Linc's notification", s, LincSymbols.Notifications, value = if (granted) "Allowed" else "Not allowed",
                    trailing = if (granted) null else ({ FilledTonalButton(onClick = {
                        if (Build.VERSION.SDK_INT >= 33) askNotifications.launch(Manifest.permission.POST_NOTIFICATIONS)
                    }) { Text("Allow") } })) },
                { s -> SettingsRow("Phone notifications on the PC", s, LincSymbols.Computer, value = if (listener) "Allowed" else "Not allowed",
                    trailing = if (listener) null else ({ FilledTonalButton(onClick = {
                        open(context, Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
                    }) { Text("Allow") } })) },
            ))
        }
        Page.Optional -> {
            Illustration(LincSymbols.Sms, MaterialShapes.Sunny.toShape())
            Title("Optional extras")
            Body("Each of these is optional — skip any you don't want. You can change them later in Settings.")
            val sms = remember(tick) { SmsProvider.canRead(context) }
            val calls = remember(tick) { CallProvider.canReadLog(context) }
            val files = remember(tick) { Environment.isExternalStorageManager() }
            SettingsGroup(rows = listOf(
                { s -> SettingsRow("Messages", s, LincSymbols.Sms, value = if (sms) "Allowed" else "Read and send texts from the PC",
                    trailing = if (sms) null else ({ FilledTonalButton(onClick = {
                        askSms.launch(arrayOf(Manifest.permission.READ_SMS, Manifest.permission.SEND_SMS, Manifest.permission.RECEIVE_SMS))
                    }) { Text("Allow") } })) },
                { s -> SettingsRow("Calls", s, LincSymbols.Call, value = if (calls) "Allowed" else "See calls and dial from the PC",
                    trailing = if (calls) null else ({ FilledTonalButton(onClick = {
                        askCalls.launch(arrayOf(Manifest.permission.READ_CALL_LOG, Manifest.permission.CALL_PHONE,
                            Manifest.permission.READ_PHONE_STATE, Manifest.permission.ANSWER_PHONE_CALLS))
                    }) { Text("Allow") } })) },
                { s -> SettingsRow("All files", s, LincSymbols.Folder, value = if (files) "Allowed" else "Send files and browse them from the PC",
                    trailing = if (files) null else ({ FilledTonalButton(onClick = { openAllFilesSettings(context) }) { Text("Allow") } })) },
            ))
        }
        Page.Background -> {
            Illustration(LincSymbols.BatteryAlert, MaterialShapes.SoftBurst.toShape())
            Title("Stay connected")
            Body("Android pauses apps in the background to save battery, which drops the Wi-Fi link to your PC. Let Linc keep it.")
            val exempt = remember(tick) { ConnectionWatch.isBatteryExempt(context) }
            Button(onClick = { ConnectionWatch.requestBatteryExemption(context) }, enabled = !exempt, shapes = ButtonDefaults.shapes(),
                modifier = Modifier.fillMaxWidth().heightIn(min = 56.dp)) { Text(if (exempt) "Allowed" else "Allow") }
            if (ConnectionWatch.hasMakerBackgroundManager) {
                OutlinedButton(onClick = { ConnectionWatch.openMakerBackgroundSettings(context) }, shapes = ButtonDefaults.shapes(),
                    modifier = Modifier.fillMaxWidth().heightIn(min = 56.dp)) { Text("Open auto-start") }
                HelperText("This phone's maker adds its own manager — turn Linc on in its auto-start list too.", textAlign = TextAlign.Center)
            }
        }
        Page.Connect -> {
            Illustration(LincSymbols.Laptop, MaterialShapes.Pentagon.toShape())
            Title("Connect to your PC")
            Body("On the PC, open Linc and choose Add a phone. It walks you through it — USB cable or Wi-Fi.")
            SettingsGroup(rows = listOf(
                { s -> SettingsRow("1. Get Linc for Windows", s, LincSymbols.Download, supporting = "From github.com/deloweProjects/Linc/releases — or ask the PC to send this app to new phones.") },
                { s -> SettingsRow("2. Turn on developer options", s, LincSymbols.Info, supporting = "Settings → About phone → tap Build number seven times.",
                    onClick = { open(context, Intent(Settings.ACTION_DEVICE_INFO_SETTINGS)) }) },
                { s -> SettingsRow("3. Turn on wireless debugging", s, LincSymbols.Wifi, supporting = "Or USB debugging, and plug in the cable.",
                    onClick = { open(context, Intent(Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS)) }) },
                { s -> SettingsRow("4. On the PC, choose Add a phone", s, LincSymbols.Computer, supporting = "Scan its QR code, or plug in. That's it — it reconnects by itself after that.") },
            ))
        }
        Page.Done -> {
            Illustration(LincSymbols.CheckCircle, MaterialShapes.Cookie12Sided.toShape())
            Title("You're set")
            Body("Linc connects by itself whenever your PC is nearby. Everything is in Home, Remote, Clipboard and Share.")
        }
    }
}

private fun open(context: android.content.Context, intent: Intent) {
    try { context.startActivity(intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) } catch (_: ActivityNotFoundException) { /* hidden on this OEM build */ }
}

@Preview(name = "Onboarding — light", widthDp = 360, heightDp = 760)
@Composable
private fun OnboardingPreview() = LincTheme(darkTheme = false) { OnboardingScreen {} }

@Preview(name = "Onboarding — dark, large font", widthDp = 360, heightDp = 760, fontScale = 2f)
@Composable
private fun OnboardingPreviewDark() = LincTheme(darkTheme = true) { OnboardingScreen {} }
