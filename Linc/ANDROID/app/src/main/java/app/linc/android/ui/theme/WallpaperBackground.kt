package app.linc.android.ui.theme

import android.app.WallpaperManager
import android.content.Context
import android.graphics.Bitmap
import android.os.Build
import android.view.Window
import android.view.WindowManager
import androidx.annotation.RequiresApi
import androidx.compose.foundation.BorderStroke
import androidx.compose.material3.AlertDialogDefaults
import androidx.compose.material3.BottomSheetDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.MenuDefaults
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.window.DialogWindowProvider
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.ColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.blur
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.unit.dp
import androidx.core.graphics.drawable.toBitmap
import androidx.core.graphics.scale
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.withContext

/**
 * The owner's "phone app too dark or white to me": an optional background of the phone's own
 * wallpaper, blurred and frosted, behind every screen. Off by default (Settings › Appearance).
 * Reading the wallpaper needs All-files access on Android 13+, as for the PC's Home background;
 * without it the switch stays on and the plain surface shows.
 */
object WallpaperBackground {
    private const val PREFS = "linc"
    private const val KEY = "wallpaper_background"

    private val _on = MutableStateFlow<Boolean?>(null)

    fun flow(context: Context): StateFlow<Boolean?> {
        if (_on.value == null) _on.value = prefs(context).getBoolean(KEY, false)
        return _on
    }

    fun set(context: Context, on: Boolean) {
        prefs(context).edit().putBoolean(KEY, on).apply()
        _on.value = on
    }

    /** A small copy of the wallpaper (the blur hides detail anyway), or null when unreadable. */
    fun load(context: Context): Bitmap? = runCatching {
        val drawable = WallpaperManager.getInstance(context).drawable ?: return null
        val full = drawable.toBitmap()
        val scale = 480f / maxOf(full.width, full.height)
        if (scale < 1f) full.scale((full.width * scale).toInt().coerceAtLeast(1), (full.height * scale).toInt().coerceAtLeast(1)) else full
    }.getOrNull()

    private fun prefs(context: Context) = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
}

/**
 * Surfaces see-through enough for the wallpaper to show, opaque enough to read on. The page's own
 * background is fully clear (screens stack a Scaffold on a Scaffold, and two 70 % layers left the
 * wallpaper at a few per cent — the owner saw no wallpaper at all); cards stay frosted.
 */
internal fun ColorScheme.frosted(dark: Boolean): ColorScheme {
    val a = if (dark) 0.30f else 0.34f
    return copy(
        background = Color.Transparent,
        surface = Color.Transparent,
        surfaceContainerLowest = surfaceContainerLowest.copy(alpha = a),
        surfaceContainerLow = surfaceContainerLow.copy(alpha = a + 0.08f),
        surfaceContainer = surfaceContainer.copy(alpha = a + 0.16f),
        surfaceContainerHigh = surfaceContainerHigh.copy(alpha = a + 0.24f),
        surfaceContainerHighest = surfaceContainerHighest.copy(alpha = a + 0.30f),
    )
}

/**
 * The blurred wallpaper with a frost toned for the theme, under [content]. [scheme] is the solid
 * (unfrosted) scheme. In the light theme a wash of the theme colour goes over the frost as well,
 * so even a white wallpaper leaves the near-white frosted cards something to stand out from.
 */
@Composable
internal fun WallpaperBackdrop(dark: Boolean, scheme: ColorScheme, content: @Composable () -> Unit) {
    val context = androidx.compose.ui.platform.LocalContext.current
    var image by remember { mutableStateOf<ImageBitmap?>(null) }
    LaunchedEffect(Unit) { image = withContext(Dispatchers.IO) { WallpaperBackground.load(context)?.asImageBitmap() } }
    Box(Modifier.fillMaxSize().background(scheme.surface)) {
        image?.let {
            Image(
                it, contentDescription = null, contentScale = ContentScale.Crop,
                // Android 12+ blurs here; older ones get the 480-px copy, which is soft already.
                modifier = Modifier.fillMaxSize().then(if (Build.VERSION.SDK_INT >= 31) Modifier.blur(20.dp) else Modifier),
            )
            // A light frost toned for the theme: the wallpaper's colours stay, text stays readable.
            Box(
                Modifier.fillMaxSize()
                    .background(scheme.surface.copy(alpha = if (dark) 0.40f else 0.30f))
                    .then(if (dark) Modifier else Modifier.background(scheme.primaryContainer.copy(alpha = 0.35f))),
            )
        }
        content()
    }
}

// ---- Overlays over the wallpaper ---------------------------------------------------------------

/**
 * What overlays need to know about the wallpaper background: whether it is [on], the theme, the
 * [solid] scheme (the frosted one in MaterialTheme has see-through containers), and whether the
 * system blurs behind windows right now ([windowBlur]: Android 12+, and not switched off by
 * battery saver, the developer option or the device).
 */
@Immutable
class Frost(val on: Boolean, val dark: Boolean, val solid: ColorScheme, val windowBlur: Boolean)

/** Provided by LincTheme; null (outside it) reads as off. */
val LocalFrost = compositionLocalOf<Frost?> { null }

/** Which overlay [overlayContainerColor] is for: with the wallpaper off each keeps its stock colour. */
enum class OverlayKind { Sheet, Dialog, Menu, Panel }

/**
 * The container colour of anything that comes above a page — bottom sheets, dialogs, menus, the
 * Tools side panel. With the wallpaper off it is exactly Material's default for that overlay.
 * With it on it is frosted glass tinted with the theme (the solid surfaceContainerHigh), never as
 * see-through as the page's cards: sheets and dialogs, whose windows blur what is behind them on
 * Android 12+, at 80 % (light) / 72 % (dark); without that blur, and for the in-window side panel,
 * 88 % / 82 %; menus, which have no scrim, 94 % / 90 %.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun overlayContainerColor(kind: OverlayKind): Color {
    val frost = LocalFrost.current
    if (frost == null || !frost.on) return when (kind) {
        OverlayKind.Sheet -> BottomSheetDefaults.ContainerColor
        OverlayKind.Dialog -> AlertDialogDefaults.containerColor
        OverlayKind.Menu -> MenuDefaults.containerColor
        OverlayKind.Panel -> MaterialTheme.colorScheme.surfaceContainer
    }
    val blurred = frost.windowBlur && (kind == OverlayKind.Sheet || kind == OverlayKind.Dialog)
    val alpha = when {
        kind == OverlayKind.Menu -> if (frost.dark) 0.90f else 0.94f
        blurred -> if (frost.dark) 0.72f else 0.80f
        else -> if (frost.dark) 0.82f else 0.88f
    }
    return frost.solid.surfaceContainerHigh.copy(alpha = alpha)
}

/** A 1 dp edge for an overlay over the wallpaper (outlineVariant at 30 %); null with it off. */
@Composable
fun overlayBorder(): BorderStroke? {
    val frost = LocalFrost.current ?: return null
    if (!frost.on) return null
    return remember(frost) { BorderStroke(1.dp, frost.solid.outlineVariant.copy(alpha = 0.30f)) }
}

/**
 * A hairline (outlineVariant at 25 %) for frosted cards in the light theme over the wallpaper, so
 * they still stand out on a white one; null otherwise (the dark theme is unchanged).
 */
@Composable
fun frostedCardBorder(): BorderStroke? {
    val frost = LocalFrost.current ?: return null
    if (!frost.on || frost.dark) return null
    return remember(frost) { BorderStroke(1.dp, frost.solid.outlineVariant.copy(alpha = 0.25f)) }
}

/**
 * Call inside a dialog's or bottom sheet's content: with the wallpaper on and cross-window blur
 * available, the dialog window blurs everything behind it (the compositor does it, so it costs
 * the app nothing per frame). Elsewhere, or with the wallpaper off, it does nothing.
 */
@Composable
fun OverlayWindowBlur() {
    val frost = LocalFrost.current ?: return
    if (!frost.on || !frost.windowBlur || Build.VERSION.SDK_INT < 31) return
    val window = (LocalView.current.parent as? DialogWindowProvider)?.window ?: return
    val radius = with(LocalDensity.current) { OVERLAY_BLUR.roundToPx() }
    DisposableEffect(window, radius) {
        if (Build.VERSION.SDK_INT >= 31) blurBehind(window, radius)
        onDispose { }
    }
}

private val OVERLAY_BLUR = 20.dp

@RequiresApi(31)
private fun blurBehind(window: Window, radius: Int) {
    runCatching {
        window.addFlags(WindowManager.LayoutParams.FLAG_BLUR_BEHIND)
        window.attributes = window.attributes.apply { blurBehindRadius = radius }
    }
}

/** Whether the system blurs behind windows now; follows battery saver and the like while [active]. */
@Composable
internal fun rememberWindowBlur(active: Boolean): Boolean =
    if (Build.VERSION.SDK_INT >= 31) rememberWindowBlur31(active) else false

@RequiresApi(31)
@Composable
private fun rememberWindowBlur31(active: Boolean): Boolean {
    val context = androidx.compose.ui.platform.LocalContext.current
    val wm = remember(context) { context.getSystemService(WindowManager::class.java) }
    var enabled by remember(wm) { mutableStateOf(wm?.isCrossWindowBlurEnabled == true) }
    DisposableEffect(wm, active) {
        if (wm == null || !active) return@DisposableEffect onDispose { }
        // Called at once with the current value, then on every change, on the main thread.
        val listener = java.util.function.Consumer<Boolean> { enabled = it }
        wm.addCrossWindowBlurEnabledListener(listener)
        onDispose { wm.removeCrossWindowBlurEnabledListener(listener) }
    }
    return enabled
}
