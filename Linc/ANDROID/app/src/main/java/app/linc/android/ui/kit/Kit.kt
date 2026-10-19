@file:OptIn(androidx.compose.material3.ExperimentalMaterial3ExpressiveApi::class, androidx.compose.material3.ExperimentalMaterial3Api::class)

package app.linc.android.ui.kit

import android.widget.Toast
import androidx.compose.animation.animateContentSize
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.ButtonGroupDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LargeFlexibleTopAppBar
import androidx.compose.material3.LinearWavyProgressIndicator
import androidx.compose.material3.LoadingIndicator
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.MaterialShapes
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.ToggleButton
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.material3.toShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import app.linc.android.service.Explanations
import app.linc.android.ui.LincSymbols
import kotlinx.coroutines.launch

// The component kit (1.0.0.11, D-082; Vibe/agent/design/android-m3e.md §3). Every screen is built
// from these; nothing sets its own corner radius, colour role or spacing.

object Kit {
    val gutter = 16.dp
    val groupGap = 12.dp
    val segmentGap = 2.dp
    val heroRadius = 28.dp
    val groupOuter = 24.dp
    val groupInner = 4.dp
    val touch = 48.dp
}

// ---- Explanations (Part 6) ---------------------------------------------------------------------

/** "Show explanations" for this composition (Settings → Appearance). Provided by LincTheme. */
val LocalShowExplanations = compositionLocalOf { true }

/**
 * The ONLY place explanatory text renders (D-084). [kind] says what it is: a helper line hides when
 * explanations are off; a warning, error or blocked reason never does.
 */
@Composable
fun HelperText(
    text: String,
    modifier: Modifier = Modifier,
    kind: Explanations.Kind = Explanations.Kind.Helper,
    maxLines: Int = Int.MAX_VALUE,
    textAlign: TextAlign? = null,
) {
    if (!Explanations.shows(kind, LocalShowExplanations.current)) return
    Text(
        text,
        modifier = modifier,
        style = MaterialTheme.typography.bodyMedium,
        color = when (kind) {
            Explanations.Kind.Error -> MaterialTheme.colorScheme.error
            else -> MaterialTheme.colorScheme.onSurfaceVariant
        },
        maxLines = maxLines,
        overflow = TextOverflow.Ellipsis,
        textAlign = textAlign,
    )
}

// ---- Snackbars ---------------------------------------------------------------------------------

/** The snackbar host of the screen in front; null where there is none (then a Toast is used). */
val LocalSnackbar = staticCompositionLocalOf<SnackbarHostState?> { null }

/** A function that shows a short message: a snackbar where there is a host, else a Toast. */
@Composable
fun rememberNotify(): (String) -> Unit {
    val host = LocalSnackbar.current
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    return remember(host) {
        { message ->
            if (host != null) scope.launch { host.showSnackbar(message) }
            else Toast.makeText(context, message, Toast.LENGTH_SHORT).show()
        }
    }
}

// ---- Scaffold ----------------------------------------------------------------------------------

/**
 * Every screen's frame: a Large flexible top app bar that collapses as the content scrolls, the
 * Settings gear (top-level screens) or a back arrow (pushed screens), an optional FAB, and the
 * snackbar host. [contentInsets] is the system-bar inset the content must still avoid: a screen
 * inside the tab host passes none at the bottom (the navigation bar handles it).
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun LincScaffold(
    title: String,
    modifier: Modifier = Modifier,
    subtitle: String? = null,
    onBack: (() -> Unit)? = null,
    onSettings: (() -> Unit)? = null,
    actions: @Composable RowScope.() -> Unit = {},
    floatingActionButton: @Composable () -> Unit = {},
    snackbar: SnackbarHostState = remember { SnackbarHostState() },
    contentInsets: WindowInsets = WindowInsets(0, 0, 0, 0),
    /** A small top bar, for pages that fill the screen and don't scroll (Remote, Terminal). */
    compactBar: Boolean = false,
    content: @Composable (PaddingValues) -> Unit,
) {
    val scroll = TopAppBarDefaults.exitUntilCollapsedScrollBehavior()
    // Inside Tools, the ⋮ that opens its side bar: in the back arrow's place, or beside it.
    val toolsMenu = app.linc.android.ui.LocalToolsMenu.current
    val navigation: @Composable () -> Unit = {
        if (onBack != null) {
            IconButton(onClick = onBack) {
                Icon(LincSymbols.ArrowBack, contentDescription = "Back")
            }
        } else if (toolsMenu != null) {
            IconButton(onClick = toolsMenu) {
                Icon(LincSymbols.MoreVert, contentDescription = "Tools menu")
            }
        }
    }
    val barActions: @Composable RowScope.() -> Unit = {
        if (onBack != null && toolsMenu != null) {
            IconButton(onClick = toolsMenu) { Icon(LincSymbols.MoreVert, contentDescription = "Tools menu") }
        }
        actions()
        if (onSettings != null) {
            IconButton(onClick = onSettings) {
                Icon(LincSymbols.Settings, contentDescription = "Settings")
            }
        }
    }
    Scaffold(
        modifier = modifier.nestedScroll(scroll.nestedScrollConnection),
        containerColor = MaterialTheme.colorScheme.surface,
        contentWindowInsets = contentInsets,
        snackbarHost = { SnackbarHost(snackbar) },
        floatingActionButton = floatingActionButton,
        topBar = {
            if (compactBar) {
                androidx.compose.material3.TopAppBar(
                    title = {
                        Column {
                            Text(title, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            if (subtitle != null) Text(subtitle, style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                    },
                    navigationIcon = navigation,
                    actions = barActions,
                    colors = TopAppBarDefaults.topAppBarColors(containerColor = MaterialTheme.colorScheme.surface),
                )
                return@Scaffold
            }
            LargeFlexibleTopAppBar(
                title = { Text(title, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                subtitle = subtitle?.let { { Text(it, maxLines = 1, overflow = TextOverflow.Ellipsis) } },
                navigationIcon = navigation,
                actions = barActions,
                scrollBehavior = scroll,
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = MaterialTheme.colorScheme.surface,
                    scrolledContainerColor = MaterialTheme.colorScheme.surfaceContainer,
                ),
            )
        },
    ) { inner ->
        androidx.compose.runtime.CompositionLocalProvider(LocalSnackbar provides snackbar) {
            content(inner)
        }
    }
}

// ---- Section header ----------------------------------------------------------------------------

@Composable
fun SectionHeader(text: String, modifier: Modifier = Modifier, trailing: @Composable RowScope.() -> Unit = {}) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .padding(start = Kit.gutter, end = Kit.gutter - 4.dp, top = 20.dp, bottom = 8.dp)
            .semantics { this.contentDescription = text },
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            text,
            modifier = Modifier.weight(1f),
            style = MaterialTheme.typography.titleSmall,
            fontWeight = FontWeight.SemiBold,
            color = MaterialTheme.colorScheme.primary,
        )
        trailing()
    }
}

// ---- Grouped list ------------------------------------------------------------------------------

/** Where a row sits in its group, which decides its corners (24 dp outside, 4 dp inside). */
enum class Segment { Only, First, Middle, Last }

fun segmentOf(index: Int, count: Int): Segment = when {
    count <= 1 -> Segment.Only
    index == 0 -> Segment.First
    index == count - 1 -> Segment.Last
    else -> Segment.Middle
}

fun segmentShape(segment: Segment): Shape {
    val o = Kit.groupOuter
    val i = Kit.groupInner
    return when (segment) {
        Segment.Only -> RoundedCornerShape(o)
        Segment.First -> RoundedCornerShape(topStart = o, topEnd = o, bottomStart = i, bottomEnd = i)
        Segment.Middle -> RoundedCornerShape(i)
        Segment.Last -> RoundedCornerShape(topStart = i, topEnd = i, bottomStart = o, bottomEnd = o)
    }
}

/** The colour of a grouped-list segment: one step above the page in either theme. */
@Composable
fun segmentColor(): Color = MaterialTheme.colorScheme.surfaceContainer

/**
 * A segmented group (the Android 16 Settings look): rows 2 dp apart, the group's outer corners
 * 24 dp, inner 4 dp. Build rows with [SettingsRow] inside [rows]; each gets its [Segment].
 */
@Composable
fun SettingsGroup(
    modifier: Modifier = Modifier,
    rows: List<@Composable (Segment) -> Unit>,
) {
    Column(
        modifier = modifier.fillMaxWidth().padding(horizontal = Kit.gutter),
        verticalArrangement = Arrangement.spacedBy(Kit.segmentGap),
    ) {
        rows.forEachIndexed { index, row -> row(segmentOf(index, rows.size)) }
    }
}

/**
 * One row: a leading tonal icon in a 40 dp circle, the headline, a [HelperText] supporting line
 * (or a [value] that always shows), and a trailing slot — a Switch, a value, a chevron or a button.
 * The whole row is the click target when [onClick] is set.
 */
@Composable
fun SettingsRow(
    title: String,
    segment: Segment = Segment.Only,
    icon: ImageVector? = null,
    supporting: String? = null,
    supportingKind: Explanations.Kind = Explanations.Kind.Helper,
    value: String? = null,
    enabled: Boolean = true,
    onClick: (() -> Unit)? = null,
    iconTint: Color = MaterialTheme.colorScheme.onSecondaryContainer,
    iconContainer: Color = MaterialTheme.colorScheme.secondaryContainer,
    trailing: @Composable (() -> Unit)? = null,
) {
    Surface(
        shape = segmentShape(segment),
        color = segmentColor(),
        modifier = Modifier.fillMaxWidth(),
    ) {
        Row(
            modifier = Modifier
                .then(if (onClick != null) Modifier.clickable(enabled = enabled, role = Role.Button, onClick = onClick) else Modifier)
                .heightIn(min = 64.dp)
                .padding(horizontal = Kit.gutter, vertical = 12.dp)
                .alpha(if (enabled) 1f else 0.38f),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            if (icon != null) {
                Box(
                    Modifier.size(40.dp).clip(CircleShape).background(iconContainer),
                    contentAlignment = Alignment.Center,
                ) { Icon(icon, contentDescription = null, tint = iconTint, modifier = Modifier.size(22.dp)) }
            }
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(title, style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurface)
                if (value != null) {
                    Text(value, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                if (supporting != null) HelperText(supporting, kind = supportingKind, maxLines = 3)
            }
            if (trailing != null) trailing()
        }
    }
}

/** The trailing chevron for rows that open something. */
@Composable
fun Chevron() = Icon(LincSymbols.ChevronRight, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)

// ---- Hero card ---------------------------------------------------------------------------------

/** The one hero per screen: 28 dp, primaryContainer. */
@Composable
fun HeroCard(
    modifier: Modifier = Modifier,
    onClick: (() -> Unit)? = null,
    container: Color = MaterialTheme.colorScheme.primaryContainer,
    content: @Composable ColumnScope.() -> Unit,
) {
    val shape = RoundedCornerShape(Kit.heroRadius)
    Surface(
        shape = shape,
        color = container,
        modifier = modifier
            .fillMaxWidth()
            .padding(horizontal = Kit.gutter)
            .clip(shape)
            .then(if (onClick != null) Modifier.clickable(role = Role.Button, onClick = onClick) else Modifier),
    ) {
        Column(Modifier.padding(20.dp).animateContentSize(MaterialTheme.motionScheme.defaultSpatialSpec()),
            verticalArrangement = Arrangement.spacedBy(8.dp), content = content)
    }
}

// ---- Tool tile ---------------------------------------------------------------------------------

/**
 * A 2-up grid tile: icon clipped to an Expressive shape (clip only — never a shadow, D-072), a
 * title and a live state line. Pressing morphs the corners 28 → 16 dp on the motion scheme's
 * spring. Disabled tiles stay visible and keep explaining why ([state]).
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun ToolTile(
    title: String,
    icon: ImageVector,
    modifier: Modifier = Modifier,
    state: String? = null,
    active: Boolean = false,
    enabled: Boolean = true,
    shape: Shape = MaterialShapes.Cookie9Sided.toShape(),
    container: Color = if (active) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.secondaryContainer,
    onClick: () -> Unit,
) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val corner by animateDpAsState(if (pressed) 16.dp else Kit.heroRadius, MaterialTheme.motionScheme.fastSpatialSpec(), label = "tilePress")
    val content = if (active) MaterialTheme.colorScheme.onPrimaryContainer else MaterialTheme.colorScheme.onSecondaryContainer
    Surface(
        shape = RoundedCornerShape(corner),
        color = container,
        contentColor = content,
        modifier = modifier
            .heightIn(min = 112.dp)
            .clip(RoundedCornerShape(corner))
            .clickable(interactionSource = interaction, indication = androidx.compose.material3.ripple(), enabled = enabled, role = Role.Button, onClick = onClick)
            .alpha(if (enabled) 1f else 0.5f),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Box(
                Modifier.size(44.dp).clip(shape).background(LocalContentColor.current.copy(alpha = 0.12f)),
                contentAlignment = Alignment.Center,
            ) { Icon(icon, contentDescription = null, modifier = Modifier.size(24.dp)) }
            Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold, maxLines = 2, overflow = TextOverflow.Ellipsis)
            if (state != null) {
                Text(state, style = MaterialTheme.typography.bodyMedium, color = LocalContentColor.current.copy(alpha = 0.8f), maxLines = 2, overflow = TextOverflow.Ellipsis)
            }
        }
    }
}

/** Two tiles per row, 12 dp apart, 16 dp gutters. */
@Composable
fun TileGrid(modifier: Modifier = Modifier, tiles: List<@Composable (Modifier) -> Unit>) {
    Column(modifier.padding(horizontal = Kit.gutter), verticalArrangement = Arrangement.spacedBy(Kit.groupGap)) {
        tiles.chunked(2).forEach { pair ->
            Row(horizontalArrangement = Arrangement.spacedBy(Kit.groupGap)) {
                pair.forEach { tile -> tile(Modifier.weight(1f)) }
                if (pair.size == 1) Spacer(Modifier.weight(1f))
            }
        }
    }
}

// ---- Status chip -------------------------------------------------------------------------------

/** Connection quality, the one place literal colours are allowed (D-082 tokens). */
enum class Quality(val dot: Color) { Good(Color(0xFF34A853)), Fair(Color(0xFFF9AB00)), Bad(Color(0xFFEA4335)), Off(Color(0xFF9AA0A6)) }

@Composable
fun StatusChip(label: String, quality: Quality, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier
            .clip(CircleShape)
            .background(MaterialTheme.colorScheme.surfaceContainerHighest)
            .padding(horizontal = 12.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Box(Modifier.size(8.dp).clip(CircleShape).background(quality.dot))
        Text(label, style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurface)
    }
}

// ---- Blocked / empty states --------------------------------------------------------------------

/**
 * Why something can't be used right now, and the one thing to do about it. The reason is never
 * hidden by the explanations switch.
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun BlockedState(
    title: String,
    reason: String,
    icon: ImageVector,
    modifier: Modifier = Modifier,
    action: String? = null,
    onAction: (() -> Unit)? = null,
) {
    Column(
        modifier.fillMaxWidth().padding(horizontal = 32.dp, vertical = 24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Box(
            Modifier.size(96.dp).clip(MaterialShapes.Cookie9Sided.toShape()).background(MaterialTheme.colorScheme.secondaryContainer),
            contentAlignment = Alignment.Center,
        ) { Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.onSecondaryContainer, modifier = Modifier.size(40.dp)) }
        Text(title, style = MaterialTheme.typography.titleLarge, textAlign = TextAlign.Center)
        HelperText(reason, kind = Explanations.Kind.Blocked, textAlign = TextAlign.Center)
        if (action != null && onAction != null) {
            FilledTonalButton(onClick = onAction, shapes = ButtonDefaults.shapes(), modifier = Modifier.heightIn(min = Kit.touch)) { Text(action) }
        }
    }
}

/** An empty list: same shape as [BlockedState], with helper text (hideable) instead of a reason. */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun EmptyState(title: String, hint: String, icon: ImageVector, modifier: Modifier = Modifier) {
    Column(
        modifier.fillMaxWidth().padding(horizontal = 32.dp, vertical = 32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Box(
            Modifier.size(80.dp).clip(MaterialShapes.Clover4Leaf.toShape()).background(MaterialTheme.colorScheme.surfaceContainerHigh),
            contentAlignment = Alignment.Center,
        ) { Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.size(32.dp)) }
        Text(title, style = MaterialTheme.typography.titleMedium, textAlign = TextAlign.Center)
        HelperText(hint, textAlign = TextAlign.Center)
    }
}

// ---- Connected choice --------------------------------------------------------------------------

/** 2–3 exclusive options as a connected button group. */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun <T> ConnectedChoice(
    options: List<Pair<T, String>>,
    selected: T,
    onSelect: (T) -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
) {
    Row(modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(ButtonGroupDefaults.ConnectedSpaceBetween)) {
        options.forEachIndexed { index, (value, label) ->
            ToggleButton(
                checked = value == selected,
                onCheckedChange = { if (it) onSelect(value) },
                enabled = enabled,
                shapes = when (index) {
                    0 -> ButtonGroupDefaults.connectedLeadingButtonShapes()
                    options.lastIndex -> ButtonGroupDefaults.connectedTrailingButtonShapes()
                    else -> ButtonGroupDefaults.connectedMiddleButtonShapes()
                },
                modifier = Modifier.weight(1f).heightIn(min = Kit.touch).semantics { contentDescription = label },
            ) { Text(label, maxLines = 1, overflow = TextOverflow.Ellipsis) }
        }
    }
}

// ---- Loading -----------------------------------------------------------------------------------

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun Loading(modifier: Modifier = Modifier, label: String? = null) {
    Column(modifier.fillMaxWidth().padding(24.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(12.dp)) {
        LoadingIndicator()
        if (label != null) Text(label, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

/** A download or transfer with a known size: wavy bar and a percentage. */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun ProgressLine(progress: Float, label: String, modifier: Modifier = Modifier) {
    Column(modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(label, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text("${(progress * 100).toInt()} %", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        LinearWavyProgressIndicator(progress = { progress.coerceIn(0f, 1f) }, modifier = Modifier.fillMaxWidth())
    }
}

/** Page padding for lazy lists inside a [LincScaffold]: top bar above, room for a FAB below. */
fun pagePadding(inner: PaddingValues, bottomExtra: Dp = 96.dp) =
    PaddingValues(top = inner.calculateTopPadding() + 4.dp, bottom = inner.calculateBottomPadding() + bottomExtra)

/** Fill the remaining height (for non-scrolling pages like Remote). */
fun Modifier.page(inner: PaddingValues) = this.fillMaxSize().padding(top = inner.calculateTopPadding())

