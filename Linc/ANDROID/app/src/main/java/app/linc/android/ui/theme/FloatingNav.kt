package app.linc.android.ui.theme

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/**
 * The owner's "nav bar of phone to be floating, as a setting": the tabs as a floating pill above
 * the gesture bar, like the PC's floating dock, instead of the full-width Material bar. Off by
 * default (Settings › Appearance); MainActivity draws whichever is chosen.
 */
object FloatingNav {
    private const val PREFS = "linc"
    private const val KEY = "floating_nav"

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
