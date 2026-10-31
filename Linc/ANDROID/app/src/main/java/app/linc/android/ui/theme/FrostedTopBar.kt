package app.linc.android.ui.theme

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/**
 * The owner's frosted top bar: the page slides behind the screen's top bar and shows through it,
 * blurred, once it scrolls under (Android 12+; older phones get a see-through tint, as they can't
 * blur). Off by default (Settings › Appearance), so the bar looks as it always has; LincScaffold
 * draws it.
 */
object FrostedTopBar {
    private const val PREFS = "linc"
    private const val KEY = "frosted_top_bar"

    private val _on = MutableStateFlow<Boolean?>(null)

    fun flow(context: Context): StateFlow<Boolean?> {
        if (_on.value == null) _on.value = prefs(context).getBoolean(KEY, false)
        return _on
    }

    fun set(context: Context, on: Boolean) {
        prefs(context).edit().putBoolean(KEY, on).apply()
        _on.value = on
    }

    private fun prefs(context: Context) = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
}
