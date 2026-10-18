package app.linc.android.service

import android.content.Context
import android.content.SharedPreferences
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.callbackFlow
import kotlinx.coroutines.flow.distinctUntilChanged

/**
 * Settings → Appearance → "Show explanations" (1.0.0.11, D-084). Default on. When off, the
 * explanatory lines under titles go away everywhere; titles, values, warnings, errors and the
 * reason a control is blocked always stay — those are information, not explanation.
 *
 * Per app: the phone and the PC each have their own switch, and they don't sync.
 */
object Explanations {
    private const val PREFS = "linc"
    private const val KEY = "show_explanations"

    /** What a piece of supporting text is, which decides whether the switch may hide it. */
    enum class Kind { Helper, Warning, Error, Blocked }

    /** Pure: is text of [kind] shown when the switch is [enabled]? Only helpers ever hide. */
    fun shows(kind: Kind, enabled: Boolean): Boolean = enabled || kind != Kind.Helper

    fun get(context: Context): Boolean = prefs(context).getBoolean(KEY, true)

    fun set(context: Context, on: Boolean) {
        prefs(context).edit().putBoolean(KEY, on).apply()
    }

    /** The switch as a flow: every screen re-reads it the moment it changes. */
    fun flow(context: Context): Flow<Boolean> = callbackFlow {
        val prefs = prefs(context)
        val listener = SharedPreferences.OnSharedPreferenceChangeListener { p, key ->
            if (key == KEY) trySend(p.getBoolean(KEY, true))
        }
        prefs.registerOnSharedPreferenceChangeListener(listener)
        trySend(prefs.getBoolean(KEY, true))
        awaitClose { prefs.unregisterOnSharedPreferenceChangeListener(listener) }
    }.distinctUntilChanged()

    private fun prefs(context: Context) =
        context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
}
