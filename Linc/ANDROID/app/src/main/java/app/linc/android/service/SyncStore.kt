package app.linc.android.service

import android.content.Context

/** Persists which Sync-page lanes are enabled (PROTOCOL.md v11 `sync.config`, D-025). */
object SyncStore {

    private const val PREFS = "linc"

    fun set(context: Context, lane: String, enabled: Boolean) =
        prefs(context).edit().putBoolean("sync_$lane", enabled).apply()

    fun isOn(context: Context, lane: String): Boolean =
        prefs(context).getBoolean("sync_$lane", false)

    private fun prefs(context: Context) = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
}
