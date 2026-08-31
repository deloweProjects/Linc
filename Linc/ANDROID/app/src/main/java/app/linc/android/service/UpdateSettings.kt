package app.linc.android.service

import android.content.Context

/**
 * M19 C1: where the phone keeps its auto-update settings.
 *
 * **The manifest address is no longer a setting.** It is a build-time constant pointing at the
 * project's own repository, so committing a change to `Releases/update.json` is the entire release
 * mechanism — there is no backend and nothing for a user to configure or mistype. (M17b B2 had
 * made the URL itself the on/off switch; M19 replaced that with [updatesEnabled] below.)
 *
 * The one remaining switch is **"Keep Linc up to date", which defaults to ON**. Turned off, the
 * update code path cannot execute at all — no request, no UI, nothing in the logs; the app just
 * points the user at the Releases page.
 *
 * Nothing about the user is stored or sent (M17b B5). The check is a plain GET of a static file;
 * the only things written here are the on/off flag and which version the user chose to skip.
 */
object UpdateSettings {

    private const val PREFS = "linc"
    private const val KEY_ENABLED = "update_auto_enabled"
    private const val KEY_SKIPPED = "update_skipped_version"

    /**
     * M19 C1: the update channel, baked in at build time. Both apps read this same file; the
     * desktop's twin is `UpdateChannel.ManifestUrl` in `Linc.Desktop/Services/UpdateChannel.cs`.
     * Raw githubusercontent, so no API token and no rate-limited API call is involved.
     */
    const val MANIFEST_URL =
        "https://raw.githubusercontent.com/deloweProjects/Linc/main/Releases/update.json"

    /** Where a user is sent when automatic updates are off, or when an install must be manual. */
    const val RELEASES_URL = "https://github.com/deloweProjects/Linc/releases"

    private fun prefs(context: Context) = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    /**
     * "Keep Linc up to date". **Default ON** — a missing key (a fresh install, or one upgraded
     * from M17b) reads as true, which is the whole point of shipping an update channel.
     */
    fun updatesEnabled(context: Context): Boolean = prefs(context).getBoolean(KEY_ENABLED, true)

    fun setUpdatesEnabled(context: Context, enabled: Boolean) {
        prefs(context).edit().putBoolean(KEY_ENABLED, enabled).apply()
    }

    /** The version the user skipped, or null. Applies to that one version only (M17b B4). */
    fun skippedVersion(context: Context): String? =
        prefs(context).getString(KEY_SKIPPED, null)?.trim()?.takeIf { it.isNotEmpty() }

    fun setSkippedVersion(context: Context, version: String?) {
        prefs(context).edit().putString(KEY_SKIPPED, version?.trim()?.takeIf { it.isNotEmpty() }).apply()
    }

    /**
     * The manifest URL to fetch, or null when updates are off. Null keeps the whole feature inert,
     * exactly as an unset URL did in M17b — every caller's existing null check still holds.
     */
    fun manifestUrl(context: Context): String? =
        if (updatesEnabled(context)) MANIFEST_URL else null

    /** True while the update path may run. While false, nothing in it may execute. */
    fun isEnabled(context: Context): Boolean = updatesEnabled(context)
}
