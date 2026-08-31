package app.linc.android.ui.theme

import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.platform.LocalContext

// M19 E1 (owner's ruling): the phone follows the device's own colours. Material You dynamic
// color drives this theme on Android 12+ (API 31), where the platform derives it from the user's
// wallpaper; below that, and any time the platform cannot supply one, the app's own neutral
// monochrome ramp below is used instead. M17a B1 had removed the dynamic path so the phone
// matched the desktop's black-and-white; the owner has since ruled the other way — the phone
// should look like the phone, and the DESKTOP follows it via UsePhoneColours (M18 B1), which
// stays exactly as M18 built it.
//
// The neutral ramp is the same one the desktop shipped in M14 (Color.kt is a verbatim
// transcription of MaterialExpressive.xaml), so with no dynamic colour the two apps still look
// like one product.
//
// The theme payload the phone SENDS to the desktop is unaffected either way: that is built from
// dynamicLightColorScheme/dynamicDarkColorScheme inside DeviceStatusReporter.themePayload() and
// has never read this file (docs/DECISIONS.md D-011). The desktop's contrast clamp
// (DynamicPalette, M18 B3) therefore still governs everything the desktop renders.

internal val LincLightColorScheme = lightColorScheme(
    primary = MdPrimaryLight,
    onPrimary = MdOnPrimaryLight,
    primaryContainer = MdPrimaryContainerLight,
    onPrimaryContainer = MdOnPrimaryContainerLight,
    secondaryContainer = MdSecondaryContainerLight,
    onSecondaryContainer = MdOnSecondaryContainerLight,
    tertiaryContainer = MdTertiaryContainerLight,
    onTertiaryContainer = MdOnTertiaryContainerLight,
    surface = MdSurfaceLight,
    onSurface = MdOnSurfaceLight,
    surfaceContainer = MdSurfaceContainerLight,
    surfaceContainerHigh = MdSurfaceContainerHighLight,
    surfaceVariant = MdSurfaceContainerLight,
    onSurfaceVariant = MdOnSurfaceVariantLight,
    outline = MdOutlineLight,
    outlineVariant = MdOutlineVariantLight,
    error = MdErrorLight,
    errorContainer = MdErrorContainerLight,
    onErrorContainer = MdOnErrorContainerLight,
)

internal val LincDarkColorScheme = darkColorScheme(
    primary = MdPrimaryDark,
    onPrimary = MdOnPrimaryDark,
    primaryContainer = MdPrimaryContainerDark,
    onPrimaryContainer = MdOnPrimaryContainerDark,
    secondaryContainer = MdSecondaryContainerDark,
    onSecondaryContainer = MdOnSecondaryContainerDark,
    tertiaryContainer = MdTertiaryContainerDark,
    onTertiaryContainer = MdOnTertiaryContainerDark,
    surface = MdSurfaceDark,
    onSurface = MdOnSurfaceDark,
    surfaceContainer = MdSurfaceContainerDark,
    surfaceContainerHigh = MdSurfaceContainerHighDark,
    surfaceVariant = MdSurfaceContainerDark,
    onSurfaceVariant = MdOnSurfaceVariantDark,
    outline = MdOutlineDark,
    outlineVariant = MdOutlineVariantDark,
    error = MdErrorDark,
    errorContainer = MdErrorContainerDark,
    onErrorContainer = MdOnErrorContainerDark,
)

// M17a B1: one radius scale. Cards 20 dp, buttons/chips 12 dp — see Dimens.
private val LincShapes = Shapes(
    extraSmall = RoundedCornerShape(Dimens.radiusControl),
    small = RoundedCornerShape(Dimens.radiusControl),
    medium = RoundedCornerShape(Dimens.radiusCard),
    large = RoundedCornerShape(Dimens.radiusCard),
    extraLarge = RoundedCornerShape(Dimens.radiusCard),
)

/** True on Android 12+ (API 31), the first release that can derive a scheme from the wallpaper. */
internal val dynamicColorAvailable: Boolean
    get() = Build.VERSION.SDK_INT >= Build.VERSION_CODES.S

@Composable
fun LincTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    content: @Composable () -> Unit,
) {
    val context = LocalContext.current
    val fallback = if (darkTheme) LincDarkColorScheme else LincLightColorScheme
    val scheme: ColorScheme = if (dynamicColorAvailable) {
        // A device can refuse to produce a scheme (no wallpaper colours yet, a stripped OEM
        // build). Falling through to the Linc ramp is the correct outcome and must never be a
        // crash, so this catches rather than assuming the platform call always succeeds.
        try {
            if (darkTheme) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
        } catch (_: Exception) {
            fallback
        }
    } else {
        fallback // API 30: the platform has no dynamic colour at all.
    }

    MaterialTheme(
        colorScheme = scheme,
        shapes = LincShapes,
        content = content,
    )
}
