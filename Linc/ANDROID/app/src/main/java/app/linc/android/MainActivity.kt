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
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
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
import app.linc.android.ui.theme.FloatingNav
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
        // LincLink: and it starts Linc if it isn't running (unless it was stopped by hand), so the
        // PC can find the phone without anyone tapping Start.
        if (app.linc.android.service.link.LinkStore.engineOn(this) &&
            !app.linc.android.service.link.LinkStore.userStopped(this) &&
            app.linc.android.service.TransportStore.hasAnyPc(this) &&
            app.linc.android.service.CompanionStateHolder.state.value is app.linc.android.service.CompanionStateHolder.ServiceState.Stopped
        ) {
            runCatching { startForegroundService(android.content.Intent(this, app.linc.android.service.CompanionService::class.java)) }
        }
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
    Remote("Control", LincSymbols.Remote),
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
    val floatingNav by FloatingNav.flow(context).collectAsState()
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
                        if (floatingNav == true) {
                            FloatingTabBar(selected = tab, onSelect = { tab = it })
                        } else {
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
                                                HomeDestination.Sync -> ToolPage.Sync
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

/**
 * The floating tab bar (Settings › Appearance › Floating navigation bar): a pill above the gesture
 * bar, like the PC's floating dock. The open tab shows its icon and name; the others show the icon,
 * with the name as its content description (TalkBack and the uishots script find tabs by it).
 * It sits in the Scaffold's bottom slot, so pages are padded above it and nothing hides under it.
 */
@Composable
private fun FloatingTabBar(selected: Tab, onSelect: (Tab) -> Unit) {
    val colors = MaterialTheme.colorScheme
    Box(
        Modifier
            .fillMaxWidth()
            .navigationBarsPadding()
            .padding(start = 16.dp, end = 16.dp, top = 8.dp, bottom = 12.dp),
        contentAlignment = Alignment.Center,
    ) {
        Surface(
            shape = RoundedCornerShape(32.dp),
            color = colors.surfaceContainer,
            contentColor = colors.onSurfaceVariant,
            shadowElevation = 6.dp,
            modifier = Modifier.fillMaxWidth(),
        ) {
            Row(
                Modifier.selectableGroup().padding(8.dp),
                horizontalArrangement = Arrangement.spacedBy(4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Tab.entries.forEach { t -> FloatingTabItem(t, selected == t) { onSelect(t) } }
            }
        }
    }
}

@Composable
private fun RowScope.FloatingTabItem(tab: Tab, selected: Boolean, onClick: () -> Unit) {
    val colors = MaterialTheme.colorScheme
    val tint = if (selected) colors.onSecondaryContainer else colors.onSurfaceVariant
    Row(
        Modifier
            .then(if (selected) Modifier.widthIn(max = 200.dp) else Modifier.weight(1f))
            .height(48.dp)
            .clip(RoundedCornerShape(24.dp))
            .background(if (selected) colors.secondaryContainer else Color.Transparent)
            .selectable(selected = selected, onClick = onClick, role = Role.Tab)
            .padding(horizontal = if (selected) 18.dp else 0.dp),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(tab.icon, contentDescription = if (selected) null else tab.label, tint = tint)
        if (selected) {
            Text(
                tab.label,
                style = MaterialTheme.typography.labelLarge,
                color = tint,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.padding(start = 8.dp),
            )
        }
    }
}
