package app.linc.android

import android.content.ClipboardManager
import android.content.Context
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Icon
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.core.content.edit
import app.linc.android.service.ClipboardBridge
import app.linc.android.ui.HomeScreen
import app.linc.android.ui.LogsScreen
import app.linc.android.ui.OnboardingScreen
import app.linc.android.ui.Screen
import app.linc.android.ui.SettingsScreen
import app.linc.android.ui.StatusScreen
import app.linc.android.ui.theme.LincTheme

private const val PREFS_NAME = "linc"
private const val KEY_ONBOARDED = "onboarded"

class MainActivity : ComponentActivity() {

    // Clipboard is only readable while this activity is focused (Android 10+),
    // so phone -> desktop clipboard sync lives here, not in the service.
    private val clipboardListener = ClipboardManager.OnPrimaryClipChangedListener {
        ClipboardBridge.pushIfChanged(this)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            LincTheme {
                LincApp()
            }
        }
    }

    override fun onResume() {
        super.onResume()
        getSystemService(ClipboardManager::class.java)
            .addPrimaryClipChangedListener(clipboardListener)
    }

    override fun onPause() {
        getSystemService(ClipboardManager::class.java)
            .removePrimaryClipChangedListener(clipboardListener)
        super.onPause()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) {
            ClipboardBridge.pushIfChanged(this)
        }
    }
}

@Composable
fun LincApp() {
    val context = LocalContext.current
    val prefs = remember { context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE) }
    var onboarded by remember { mutableStateOf(prefs.getBoolean(KEY_ONBOARDED, false)) }

    if (!onboarded) {
        OnboardingScreen(onDone = {
            prefs.edit { putBoolean(KEY_ONBOARDED, true) }
            onboarded = true
        })
        return
    }

    var currentScreen by remember { mutableStateOf(Screen.Home) }

    Scaffold(
        bottomBar = {
            NavigationBar {
                // Logs isn't a primary destination — it's reached from Settings.
                Screen.entries.filter { it != Screen.Logs }.forEach { screen ->
                    NavigationBarItem(
                        selected = currentScreen == screen,
                        onClick = { currentScreen = screen },
                        icon = { Icon(screen.icon, contentDescription = screen.label) },
                        label = { Text(screen.label) },
                    )
                }
            }
        },
    ) { innerPadding ->
        // Constrain screen content to the area above the bottom navigation bar. Without
        // this, content is laid out at full window height and bottom-anchored controls
        // (e.g. the Status screen's Start button) render behind the nav bar and are lost.
        Box(modifier = Modifier.padding(innerPadding)) {
            when (currentScreen) {
                Screen.Home -> HomeScreen()
                Screen.Status -> StatusScreen()
                Screen.Logs -> LogsScreen(onBack = { currentScreen = Screen.Settings })
                Screen.Settings -> SettingsScreen(
                    onRerunSetup = {
                        prefs.edit { putBoolean(KEY_ONBOARDED, false) }
                        onboarded = false
                    },
                    onOpenLogs = { currentScreen = Screen.Logs },
                )
            }
        }
    }
}
