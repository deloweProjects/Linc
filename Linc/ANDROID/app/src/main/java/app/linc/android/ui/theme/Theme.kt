package app.linc.android.ui.theme

import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.material3.MotionScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.platform.LocalContext
import app.linc.android.service.Explanations
import app.linc.android.ui.kit.LocalShowExplanations

// 1.0.0.11 (D-082): Material 3 Expressive everywhere — MaterialExpressiveTheme with the expressive
// motion scheme (springs) and the stock Expressive shapes. Colour stays M19 E1's ruling: the
// phone's own Material You colours on Android 12+; otherwise the scheme generated from Linc's
// indigo seed (SeedScheme.kt), which replaced the grey ramp the app used to fall back to.
//
// The theme payload the phone SENDS to the desktop is unaffected: DeviceStatusReporter builds it
// from dynamicLightColorScheme/dynamicDarkColorScheme and never reads this file (D-011).

/** True on Android 12+ (API 31), the first release that can derive a scheme from the wallpaper. */
internal val dynamicColorAvailable: Boolean
    get() = Build.VERSION.SDK_INT >= Build.VERSION_CODES.S

/** The scheme for [darkTheme]: wallpaper colours when the platform has them, else the seed's. */
@Composable
fun lincColorScheme(darkTheme: Boolean = isSystemInDarkTheme()): ColorScheme {
    val context = LocalContext.current
    val fallback = if (darkTheme) SeedDarkColorScheme else SeedLightColorScheme
    if (!dynamicColorAvailable) return fallback
    // A device can refuse to produce a scheme (no wallpaper colours yet, a stripped OEM build);
    // the seed scheme is the right outcome then, never a crash.
    return try {
        if (darkTheme) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
    } catch (_: Exception) {
        fallback
    }
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun LincTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    content: @Composable () -> Unit,
) {
    val context = LocalContext.current
    val showExplanations by Explanations.flow(context).collectAsState(initial = Explanations.get(context))
    val wallpaper by WallpaperBackground.flow(context).collectAsState()
    val scheme = lincColorScheme(darkTheme)
    MaterialExpressiveTheme(
        colorScheme = if (wallpaper == true) scheme.frosted(darkTheme) else scheme,
        motionScheme = MotionScheme.expressive(),
    ) {
        CompositionLocalProvider(LocalShowExplanations provides showExplanations) {
            if (wallpaper == true) WallpaperBackdrop(darkTheme, scheme.surface, content) else content()
        }
    }
}
