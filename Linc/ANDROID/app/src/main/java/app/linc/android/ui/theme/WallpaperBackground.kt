package app.linc.android.ui.theme

import android.app.WallpaperManager
import android.content.Context
import android.graphics.Bitmap
import android.os.Build
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

/** Surfaces see-through enough for the wallpaper to show, opaque enough to read on. */
internal fun ColorScheme.frosted(dark: Boolean): ColorScheme {
    val a = if (dark) 0.70f else 0.74f
    return copy(
        background = background.copy(alpha = a),
        surface = surface.copy(alpha = a),
        surfaceContainerLowest = surfaceContainerLowest.copy(alpha = a + 0.06f),
        surfaceContainerLow = surfaceContainerLow.copy(alpha = a + 0.08f),
        surfaceContainer = surfaceContainer.copy(alpha = a + 0.10f),
        surfaceContainerHigh = surfaceContainerHigh.copy(alpha = a + 0.14f),
        surfaceContainerHighest = surfaceContainerHighest.copy(alpha = a + 0.16f),
    )
}

/** The blurred wallpaper with a scrim toned for the theme, under [content]. */
@Composable
internal fun WallpaperBackdrop(dark: Boolean, scrim: Color, content: @Composable () -> Unit) {
    val context = androidx.compose.ui.platform.LocalContext.current
    var image by remember { mutableStateOf<ImageBitmap?>(null) }
    LaunchedEffect(Unit) { image = withContext(Dispatchers.IO) { WallpaperBackground.load(context)?.asImageBitmap() } }
    Box(Modifier.fillMaxSize().background(scrim)) {
        image?.let {
            Image(
                it, contentDescription = null, contentScale = ContentScale.Crop,
                // Android 12+ blurs here; older ones get the 480-px copy, which is soft already.
                modifier = Modifier.fillMaxSize().then(if (Build.VERSION.SDK_INT >= 31) Modifier.blur(36.dp) else Modifier),
            )
            Box(Modifier.fillMaxSize().background(scrim.copy(alpha = if (dark) 0.45f else 0.35f)))
        }
        content()
    }
}
