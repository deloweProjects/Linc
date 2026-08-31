package app.linc.android.service

import android.content.Context

/**
 * User-chosen reverse-mirror quality, persisted on the phone and sent to the PC in
 * `pc.mirror.start`. Lower settings ease the load the video puts on the shared link and the
 * PC's GPU (capture + hardware encode + the desktop compositor all share it), which is the
 * lever for smoothness vs sharpness.
 */
object MirrorSettings {

    enum class Quality(val label: String, val blurb: String, val fps: Int, val bitrate: Int) {
        SMOOTH("Smooth", "Lower detail, lightest on the link and PC", 24, 4_000_000),
        BALANCED("Balanced", "A good middle ground (recommended)", 30, 6_000_000),
        SHARP("Sharp", "Full detail, heaviest", 30, 8_000_000),
    }

    private const val PREFS = "linc"
    private const val KEY = "mirror_quality"

    fun get(context: Context): Quality {
        val name = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(KEY, null)
        return Quality.entries.firstOrNull { it.name == name } ?: Quality.BALANCED
    }

    fun set(context: Context, quality: Quality) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit().putString(KEY, quality.name).apply()
    }
}
