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

    /**
     * How a finger drives the PC's pointer (v20, D-067), after Windows App's two modes:
     * [TOUCH] puts the pointer where you touch; [MOUSE] is a trackpad — the pointer stays put
     * and a drag moves it, which is far more precise on a small screen.
     */
    enum class PointerMode { TOUCH, MOUSE }

    fun pointerMode(context: Context): PointerMode {
        val name = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(KEY_POINTER, null)
        return PointerMode.entries.firstOrNull { it.name == name } ?: PointerMode.TOUCH
    }

    fun setPointerMode(context: Context, mode: PointerMode) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit().putString(KEY_POINTER, mode.name).apply()
    }

    /** Play the PC's sound on the phone while PC Remote is open (v20). On by default. */
    fun audio(context: Context): Boolean =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_AUDIO, true)

    fun setAudio(context: Context, on: Boolean) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit().putBoolean(KEY_AUDIO, on).apply()
    }

    /**
     * Mute the PC while PC Remote plays its sound here: the PC's sound moves onto the phone
     * (speaker mode, v25) instead of playing on both. Off by default; needs the PC's virtual cable.
     */
    fun mutePc(context: Context): Boolean =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_MUTE_PC, false)

    fun setMutePc(context: Context, on: Boolean) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit().putBoolean(KEY_MUTE_PC, on).apply()
    }

    /** Raise a keyboard by itself when a text field is focused on the PC. On by default. */
    fun autoKeyboard(context: Context): Boolean =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_AUTO_KB, true)

    /** The circle drawn where a click lands in PC Remote. Off unless turned on (the owner's call). */
    fun clickRing(context: Context): Boolean =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_CLICK_RING, false)

    fun setClickRing(context: Context, on: Boolean) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit().putBoolean(KEY_CLICK_RING, on).apply()
    }

    fun setAutoKeyboard(context: Context, on: Boolean) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit().putBoolean(KEY_AUTO_KB, on).apply()
    }

    /** Voice Access hears through this phone's microphone (when the PC has a virtual cable). */
    fun voicePhoneMic(context: Context): Boolean =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_VOICE_PHONE_MIC, true)

    fun setVoicePhoneMic(context: Context, on: Boolean) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit().putBoolean(KEY_VOICE_PHONE_MIC, on).apply()
    }

    /** Which keyboard PC Remote raises: the floating PC keyboard (default) or the phone's own. */
    fun floatingKeyboard(context: Context): Boolean =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_FLOATING_KB, true)

    fun setFloatingKeyboard(context: Context, on: Boolean) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit().putBoolean(KEY_FLOATING_KB, on).apply()
    }

    private const val KEY_AUTO_KB = "mirror_auto_keyboard"
    private const val KEY_CLICK_RING = "mirror_click_ring"
    private const val KEY_VOICE_PHONE_MIC = "voice_access_phone_mic"
    private const val KEY_FLOATING_KB = "mirror_floating_keyboard"
    private const val KEY_POINTER = "mirror_pointer_mode"
    private const val KEY_AUDIO = "mirror_audio"
    private const val KEY_MUTE_PC = "mirror_mute_pc"
}
