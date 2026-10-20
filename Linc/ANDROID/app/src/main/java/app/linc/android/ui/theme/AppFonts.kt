@file:OptIn(ExperimentalTextApi::class)

package app.linc.android.ui.theme

import android.content.Context
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.Typography
import androidx.compose.ui.text.ExperimentalTextApi
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontVariation
import androidx.compose.ui.text.font.FontWeight
import app.linc.android.R
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/**
 * The owner's "fonts choice for apps … nice and slick, classy feel, keep current as default": the
 * app's typeface, Settings › Appearance › Font. [Default] is the phone's own font with the stock
 * Material type scale, exactly as before; the others are bundled SIL OFL fonts (res/font, licences
 * in assets/licenses), applied to every text style of the theme.
 */
enum class AppFont(val label: String, val blurb: String) {
    Default("Default", "This phone's own font"),
    Manrope("Manrope", "Modern and geometric"),
    DmSans("DM Sans", "Clean and compact"),
    Outfit("Outfit", "Soft and rounded"),
    Classic("Playfair Display", "Serif headings, DM Sans text"),
}

object AppFontSetting {
    private const val PREFS = "linc"
    private const val KEY = "app_font"

    private val _font = MutableStateFlow<AppFont?>(null)

    fun flow(context: Context): StateFlow<AppFont?> {
        if (_font.value == null) _font.value = get(context)
        return _font
    }

    fun get(context: Context): AppFont {
        val saved = prefs(context).getString(KEY, null)
        return AppFont.entries.firstOrNull { it.name == saved } ?: AppFont.Default
    }

    fun set(context: Context, font: AppFont) {
        prefs(context).edit().putString(KEY, font.name).apply()
        _font.value = font
    }

    private fun prefs(context: Context) = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
}

/**
 * The bundled fonts are variable (one file per family, every weight inside), so each weight the
 * type scale uses is its own [Font] with the weight axis set; without that Android draws the
 * file's default instance for every weight.
 */
private fun variableFamily(resId: Int): FontFamily = FontFamily(
    listOf(FontWeight.Normal, FontWeight.Medium, FontWeight.SemiBold, FontWeight.Bold).map { w ->
        Font(resId = resId, weight = w, variationSettings = FontVariation.Settings(FontVariation.weight(w.weight)))
    },
)

private object BundledFonts {
    val manrope by lazy { variableFamily(R.font.manrope) }
    val dmSans by lazy { variableFamily(R.font.dm_sans) }
    val outfit by lazy { variableFamily(R.font.outfit) }
    val playfair by lazy { variableFamily(R.font.playfair_display) }
}

/** The family for body, title and label text, or null for [AppFont.Default] (the theme's own). */
fun AppFont.textFamily(): FontFamily? = when (this) {
    AppFont.Default -> null
    AppFont.Manrope -> BundledFonts.manrope
    AppFont.DmSans -> BundledFonts.dmSans
    AppFont.Outfit -> BundledFonts.outfit
    AppFont.Classic -> BundledFonts.dmSans
}

/** The family for display and headline text (the big screen titles). */
fun AppFont.headingFamily(): FontFamily? = if (this == AppFont.Classic) BundledFonts.playfair else textFamily()

/**
 * The type scale for [font]: Material's own sizes and weights with the family swapped, or null
 * for [AppFont.Default] so the theme keeps its stock typography untouched.
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
internal fun lincTypography(font: AppFont): Typography? {
    val text = font.textFamily() ?: return null
    val heading = font.headingFamily() ?: text
    val t = Typography()
    fun TextStyle.h() = copy(fontFamily = heading)
    fun TextStyle.b() = copy(fontFamily = text)
    return t.copy(
        displayLarge = t.displayLarge.h(),
        displayMedium = t.displayMedium.h(),
        displaySmall = t.displaySmall.h(),
        headlineLarge = t.headlineLarge.h(),
        headlineMedium = t.headlineMedium.h(),
        headlineSmall = t.headlineSmall.h(),
        titleLarge = t.titleLarge.b(),
        titleMedium = t.titleMedium.b(),
        titleSmall = t.titleSmall.b(),
        bodyLarge = t.bodyLarge.b(),
        bodyMedium = t.bodyMedium.b(),
        bodySmall = t.bodySmall.b(),
        labelLarge = t.labelLarge.b(),
        labelMedium = t.labelMedium.b(),
        labelSmall = t.labelSmall.b(),
        displayLargeEmphasized = t.displayLargeEmphasized.h(),
        displayMediumEmphasized = t.displayMediumEmphasized.h(),
        displaySmallEmphasized = t.displaySmallEmphasized.h(),
        headlineLargeEmphasized = t.headlineLargeEmphasized.h(),
        headlineMediumEmphasized = t.headlineMediumEmphasized.h(),
        headlineSmallEmphasized = t.headlineSmallEmphasized.h(),
        titleLargeEmphasized = t.titleLargeEmphasized.b(),
        titleMediumEmphasized = t.titleMediumEmphasized.b(),
        titleSmallEmphasized = t.titleSmallEmphasized.b(),
        bodyLargeEmphasized = t.bodyLargeEmphasized.b(),
        bodyMediumEmphasized = t.bodyMediumEmphasized.b(),
        bodySmallEmphasized = t.bodySmallEmphasized.b(),
        labelLargeEmphasized = t.labelLargeEmphasized.b(),
        labelMediumEmphasized = t.labelMediumEmphasized.b(),
        labelSmallEmphasized = t.labelSmallEmphasized.b(),
    )
}
