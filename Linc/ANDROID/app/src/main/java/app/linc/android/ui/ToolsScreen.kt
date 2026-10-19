package app.linc.android.ui

import android.content.Context
import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialShapes
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.toShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.core.content.edit
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/** The pages inside Tools, in the rail's order. */
enum class ToolPage(val label: String, val icon: ImageVector) {
    Remote("Remote", LincSymbols.Mouse),
    Sound("Sound", LincSymbols.GraphicEq),
    Pc("PC", LincSymbols.DesktopWindows),
    Files("Files", LincSymbols.Folder),
    Terminal("Terminal", LincSymbols.Terminal),
}

/** Which Tools page shows; Home's shortcuts set it before switching to the Tools tab. */
object ToolsNav {
    private val _page = MutableStateFlow(ToolPage.Remote)
    val page: StateFlow<ToolPage> = _page
    fun open(page: ToolPage) { _page.value = page }
}

/**
 * Tools: each page — Remote, Sound, PC, Files, Terminal — fills the screen, and a side bar of
 * them slides in from the left when the ⋮ at the top of the page is tapped.
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun ToolsScreen(onSettings: (() -> Unit)?, onBack: (() -> Unit)? = null, onMirror: () -> Unit) {
    val page by ToolsNav.page.collectAsState()
    // The side bar is hidden until the ⋮ at the top of the page opens it: the page gets the
    // whole screen (the owner's ask). It always starts closed.
    var open by remember { mutableStateOf(false) }

    Box(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.surface)) {
        BackHandler(enabled = open) { open = false }
        val enter = MaterialTheme.motionScheme.defaultEffectsSpec<Float>()
        val exit = MaterialTheme.motionScheme.fastEffectsSpec<Float>()
        androidx.compose.runtime.CompositionLocalProvider(LocalToolsMenu provides { open = true }) {
            AnimatedContent(
                targetState = page,
                transitionSpec = { fadeIn(enter) togetherWith fadeOut(exit) },
                label = "toolPage",
            ) { current ->
                when (current) {
                    ToolPage.Remote -> RemoteScreen(onSettings = onSettings, onBack = onBack, onMirror = onMirror)
                    ToolPage.Sound -> SoundScreen(onBack = onBack, onSettings = onSettings)
                    ToolPage.Pc -> PcScreen(onBack = onBack, onSettings = onSettings)
                    ToolPage.Files -> PcFilesScreen(onBack = onBack, onSettings = onSettings)
                    ToolPage.Terminal -> TerminalHome(onBack = onBack, onSettings = onSettings)
                }
            }
        }
        // The side bar, over the page from the left, with a scrim that closes it.
        AnimatedVisibility(visible = open, enter = fadeIn(), exit = fadeOut()) {
            Box(
                Modifier.fillMaxSize()
                    .background(MaterialTheme.colorScheme.scrim.copy(alpha = 0.32f))
                    .clickable(interactionSource = remember { MutableInteractionSource() }, indication = null) { open = false },
            )
        }
        AnimatedVisibility(
            visible = open,
            enter = androidx.compose.animation.slideInHorizontally { -it } + fadeIn(),
            exit = androidx.compose.animation.slideOutHorizontally { -it } + fadeOut(),
        ) {
            ToolsRail(
                page = page,
                expanded = true,
                onToggle = { open = false },
                onSelect = { ToolsNav.open(it); open = false },
                onMirror = { open = false; onMirror() },
                floating = true,
            )
        }
    }
}

/** Opens the Tools side bar; provided by [ToolsScreen], read by every page's top bar (the ⋮). */
val LocalToolsMenu = androidx.compose.runtime.compositionLocalOf<(() -> Unit)?> { null }

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun ToolsRail(
    page: ToolPage,
    expanded: Boolean,
    onToggle: () -> Unit,
    onSelect: (ToolPage) -> Unit,
    onMirror: () -> Unit,
    floating: Boolean = false,
) {
    val width by animateDpAsState(if (expanded) RAIL_EXPANDED else RAIL_COLLAPSED, MaterialTheme.motionScheme.defaultSpatialSpec(), label = "railWidth")
    Surface(
        color = if (floating) MaterialTheme.colorScheme.surfaceContainer else MaterialTheme.colorScheme.surfaceContainerLow,
        shape = if (floating) RoundedCornerShape(topEnd = 28.dp, bottomEnd = 28.dp) else RoundedCornerShape(0.dp),
        shadowElevation = if (floating) 6.dp else 0.dp,
        modifier = Modifier.width(width).fillMaxHeight(),
    ) {
        Column(
            Modifier.fillMaxHeight().statusBarsPadding().padding(vertical = 12.dp),
            horizontalAlignment = if (expanded) Alignment.Start else Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            IconButton(onClick = onToggle, modifier = Modifier.padding(start = if (expanded) 12.dp else 0.dp)) {
                Icon(LincSymbols.MenuOpen, contentDescription = "Close the tools menu")
            }
            // Mirror PC: the one action that leaves the page, as the rail's FAB.
            FloatingActionButton(
                onClick = onMirror,
                containerColor = MaterialTheme.colorScheme.primaryContainer,
                contentColor = MaterialTheme.colorScheme.onPrimaryContainer,
                modifier = Modifier.padding(start = if (expanded) 16.dp else 0.dp, top = 4.dp, bottom = 12.dp)
                    .then(if (expanded) Modifier.height(56.dp) else Modifier.size(56.dp)),
                shape = RoundedCornerShape(16.dp),
            ) {
                Row(Modifier.padding(horizontal = if (expanded) 16.dp else 0.dp), verticalAlignment = Alignment.CenterVertically) {
                    Icon(LincSymbols.ScreenShare, contentDescription = "Mirror the PC")
                    if (expanded) {
                        Spacer(Modifier.width(12.dp))
                        Text("Mirror PC", style = MaterialTheme.typography.labelLarge)
                    }
                }
            }
            for (p in ToolPage.entries) RailItem(p, p == page, expanded) { onSelect(p) }
        }
    }
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun RailItem(page: ToolPage, selected: Boolean, expanded: Boolean, onClick: () -> Unit) {
    val indicator = if (selected) MaterialTheme.colorScheme.secondaryContainer else androidx.compose.ui.graphics.Color.Transparent
    val content = if (selected) MaterialTheme.colorScheme.onSecondaryContainer else MaterialTheme.colorScheme.onSurfaceVariant
    if (expanded) {
        Row(
            Modifier.padding(horizontal = 12.dp).fillMaxWidth().height(56.dp).clip(CircleShape).background(indicator)
                .clickable(role = Role.Tab, onClick = onClick).semantics { this.selected = selected }
                .padding(horizontal = 16.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(page.icon, contentDescription = null, tint = content)
            Spacer(Modifier.width(12.dp))
            Text(page.label, style = MaterialTheme.typography.labelLarge, color = content,
                fontWeight = if (selected) FontWeight.SemiBold else null, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    } else {
        Column(
            Modifier.width(RAIL_COLLAPSED).clickable(role = Role.Tab, onClick = onClick)
                .semantics { this.selected = selected; contentDescription = page.label }.padding(vertical = 6.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            Box(
                Modifier.size(width = 56.dp, height = 32.dp)
                    .clip(if (selected) MaterialShapes.Pill.toShape() else CircleShape).background(indicator),
                contentAlignment = Alignment.Center,
            ) { Icon(page.icon, contentDescription = null, tint = content) }
            Text(page.label, style = MaterialTheme.typography.labelMedium, color = content,
                fontWeight = if (selected) FontWeight.SemiBold else null, maxLines = 1)
        }
    }
}

private val RAIL_COLLAPSED: Dp = 80.dp
private val RAIL_EXPANDED: Dp = 240.dp
