package app.linc.android

import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.PredictiveBackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.core.content.edit
import app.linc.android.service.ClipboardBridge
import app.linc.android.service.UpdateCheck
import app.linc.android.ui.ClipboardScreen
import app.linc.android.ui.HomeDestination
import app.linc.android.ui.HomeScreen
import app.linc.android.ui.LincSymbols
import app.linc.android.ui.LogsScreen
import app.linc.android.ui.OnboardingScreen
import app.linc.android.ui.ToolPage
import app.linc.android.ui.ToolsNav
import app.linc.android.ui.ToolsScreen
import app.linc.android.ui.SettingsScreen
import app.linc.android.ui.ShareScreen
import app.linc.android.ui.UpdateRequiredScreen
import app.linc.android.ui.theme.LincTheme
import kotlin.coroutines.cancellation.CancellationException

private const val PREFS_NAME = "linc"
private const val KEY_ONBOARDED = "onboarded"

class MainActivity : ComponentActivity() {

    // Clipboard is only readable while this activity is focused (Android 10+),
    // so phone -> desktop clipboard sync lives here, not in the service.
    private val clipboardListener = ClipboardManager.OnPrimaryClipChangedListener {
        ClipboardBridge.pushIfChanged(this)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        app.linc.android.service.LogStore.init(this)
        app.linc.android.service.ClipboardHistoryStore.init(this)
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
        // Opening Linc is the user saying "connect" (D-081): skip the dialler's wait.
        app.linc.android.service.ReconnectSignal.poke("Linc opened")
    }

    override fun onPause() {
        getSystemService(ClipboardManager::class.java)
            .removePrimaryClipChangedListener(clipboardListener)
        super.onPause()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        ClipboardBridge.focused = hasFocus
        if (hasFocus) {
            ClipboardBridge.pushIfChanged(this)
        }
    }
}

/** The four destinations (1.0.0.11, D-082). Settings is the gear in every top bar, not a tab. */
private enum class Tab(val label: String, val icon: ImageVector) {
    Home("Home", LincSymbols.Home),
    Remote("Tools", LincSymbols.Remote),
    Clipboard("Clipboard", LincSymbols.Clipboard),
    Share("Share", LincSymbols.Share),
}

/** Screens pushed over the tabs, with a back arrow and predictive back. */
private enum class Pushed { Settings, Logs }

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun LincApp() {
    val context = LocalContext.current
    val prefs = remember { context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE) }
    var onboarded by remember { mutableStateOf(prefs.getBoolean(KEY_ONBOARDED, false)) }
    val update by UpdateCheck.offer.collectAsState()
    val updateProgress by UpdateCheck.progress.collectAsState()
    val updateProblem by UpdateCheck.problem.collectAsState()
    LaunchedEffect(Unit) { UpdateCheck.check(context) }

    if (!onboarded) {
        OnboardingScreen(onDone = {
            prefs.edit { putBoolean(KEY_ONBOARDED, true) }
            onboarded = true
        })
        return
    }
    update?.takeIf { it.forced }?.let { forced ->
        UpdateRequiredScreen(forced.notes, updateProblem, updateProgress, onUpdate = { UpdateCheck.install(context) })
        return
    }

    var tab by rememberSaveable { mutableStateOf(Tab.Home) }
    var stack by rememberSaveable { mutableStateOf(listOf<String>()) }
    var backProgress by remember { mutableFloatStateOf(0f) }
    fun push(p: Pushed) { stack = stack + p.name }
    fun pop() { stack = stack.dropLast(1) }
    val openMirror = { context.startActivity(Intent(context, MirrorActivity::class.java)) }
    val rerunSetup = {
        prefs.edit { putBoolean(KEY_ONBOARDED, false) }
        stack = emptyList()
        onboarded = false
    }

    // Predictive back for pushed screens: the screen follows the gesture and pops on release.
    PredictiveBackHandler(enabled = stack.isNotEmpty()) { progress ->
        try {
            progress.collect { backProgress = it.progress }
            pop()
        } catch (e: CancellationException) {
            // The user changed their mind mid-gesture; the screen springs back.
        } finally {
            backProgress = 0f
        }
    }

    val spatial = MaterialTheme.motionScheme.defaultSpatialSpec<androidx.compose.ui.unit.IntOffset>()
    val effects = MaterialTheme.motionScheme.defaultEffectsSpec<Float>()
    val fastEffects = MaterialTheme.motionScheme.fastEffectsSpec<Float>()
    AnimatedContent(
        targetState = stack.lastOrNull(),
        transitionSpec = {
            // Shared X axis: forward slides in from the right, back slides it away.
            val forward = (initialState == null && targetState != null) || targetState == Pushed.Logs.name
            if (forward) {
                (slideInHorizontally(spatial) { it / 4 } + fadeIn(effects)) togetherWith
                    (slideOutHorizontally(spatial) { -it / 8 } + fadeOut(effects))
            } else {
                (slideInHorizontally(spatial) { -it / 8 } + fadeIn(effects)) togetherWith
                    (slideOutHorizontally(spatial) { it / 4 } + fadeOut(effects))
            }
        },
        label = "pushed",
    ) { top ->
        val following = top != null && top == stack.lastOrNull()
        Box(
            Modifier.fillMaxSize().then(
                if (following) Modifier.graphicsLayer {
                    translationX = backProgress * 64.dp.toPx()
                    scaleX = 1f - backProgress * 0.08f
                    scaleY = 1f - backProgress * 0.08f
                } else Modifier,
            ),
        ) {
            when (top) {
                Pushed.Settings.name -> SettingsScreen(onBack = { pop() }, onOpenLogs = { push(Pushed.Logs) }, onRerunSetup = rerunSetup)
                Pushed.Logs.name -> LogsScreen(onBack = { pop() })
                else -> Scaffold(
                    contentWindowInsets = WindowInsets(0, 0, 0, 0),
                    bottomBar = {
                        NavigationBar {
                            Tab.entries.forEach { t ->
                                NavigationBarItem(
                                    selected = tab == t,
                                    onClick = { tab = t },
                                    icon = { Icon(t.icon, contentDescription = null) },
                                    label = { Text(t.label) },
                                )
                            }
                        }
                    },
                ) { inner ->
                    AnimatedContent(
                        targetState = tab,
                        transitionSpec = { fadeIn(effects) togetherWith fadeOut(fastEffects) },
                        modifier = Modifier.padding(bottom = inner.calculateBottomPadding()),
                        label = "tab",
                    ) { current ->
                        val settings = { push(Pushed.Settings) }
                        when (current) {
                            Tab.Home -> HomeScreen(
                                onSettings = settings,
                                onNavigate = { d ->
                                    tab = when (d) {
                                        HomeDestination.Share -> Tab.Share
                                        HomeDestination.Clipboard -> Tab.Clipboard
                                        else -> {
                                            ToolsNav.open(when (d) {
                                                HomeDestination.Sound -> ToolPage.Sound
                                                HomeDestination.Pc -> ToolPage.Pc
                                                HomeDestination.Files -> ToolPage.Files
                                                HomeDestination.Terminal -> ToolPage.Terminal
                                                else -> ToolPage.Remote
                                            })
                                            Tab.Remote
                                        }
                                    }
                                },
                                onMirror = openMirror,
                                onSetUp = rerunSetup,
                            )
                            Tab.Remote -> ToolsScreen(onSettings = settings, onMirror = openMirror)
                            Tab.Clipboard -> ClipboardScreen(onSettings = settings)
                            Tab.Share -> ShareScreen(onSettings = settings)
                        }
                    }
                }
            }
        }
    }
}
