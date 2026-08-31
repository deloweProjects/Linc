package app.linc.android.service

import android.content.Context
import android.provider.Settings

/**
 * v15 display control from the PC (M5c, D-055): the one place the phone touches
 * [Settings.System] for the screen orientation and brightness widgets. Together with
 * the two new [app.linc.android.protocol.MessageType.DISPLAY_ROTATION_SET] /
 * [DISPLAY_BRIGHTNESS_SET] branches in [SocketServer] this replaces the rejected
 * `adb shell settings put` approach (D-001: ADB is the transport, never the interface).
 *
 * The brightness level crosses the wire as a 0–100 percentage; this class scales it to
 * the device's real range. The rotation is `auto`/`portrait`/`landscape` mapped to
 * ACCELEROMETER_ROTATION and USER_ROTATION.
 *
 * **`WRITE_SETTINGS` is an appop, not a runtime permission** — the manifest declares it
 * but a dialog cannot grant it; the user toggles it under *Settings › Apps › Special app
 * access › Modify system settings*. [canWrite] is checked before every write so the
 * [SocketServer] branch can answer `error` `not-granted` rather than letting a
 * [SecurityException] escape (D-055). Reads need no permission.
 *
 * The scaling arithmetic and rotation mapping are kept in pure companion-object functions
 * so unit tests can reach them without a [Context] (the test classpath has no Robolectric
 * and cannot fake Android services). The instance methods that touch [Settings.System]
 * catch [SecurityException] and return `false` — they never let it escape the app.
 */
class DisplayControl(private val context: Context) {

    /** True when the user has granted Linc the Modify-system-settings appop. */
    fun canWrite(): Boolean = Settings.System.canWrite(context)

    /**
     * Applies a rotation mode:
     * - `"auto"` → ACCELEROMETER_ROTATION = 1 only; USER_ROTATION is **left untouched** so
     *   flipping back to a forced mode lands where the user last left it.
     * - `"portrait"` → ACCELEROMETER_ROTATION = 0 + USER_ROTATION = 0.
     * - `"landscape"` → ACCELEROMETER_ROTATION = 0 + USER_ROTATION = 1.
     *
     * Returns `false` for an unknown mode (no write attempted) or if [canWrite] is false
     * or the platform rejects the write; never throws [SecurityException].
     */
    fun setRotation(mode: String): Boolean {
        if (!canWrite()) return false
        val (accel, user) = rotationValues(mode) ?: return false
        return runCatching {
            Settings.System.putInt(
                context.contentResolver,
                Settings.System.ACCELEROMETER_ROTATION,
                accel,
            )
            // USER_ROTATION is only meaningful when the accelerometer is off. Writing it for
            // "auto" would destroy the user's last forced orientation instead of preserving it,
            // so skip it when accelerometer is being turned on.
            if (accel == 0) {
                Settings.System.putInt(
                    context.contentResolver,
                    Settings.System.USER_ROTATION,
                    user,
                )
            }
            true
        }.getOrElse { false }
    }

    /**
     * Applies a brightness setting:
     * - `auto = true` → SCREEN_BRIGHTNESS_MODE = AUTOMATIC (level ignored, may be null).
     * - `auto = false` → mode MANUAL and SCREEN_BRIGHTNESS scaled from [level].
     *
     * [level] is a 0–100 percentage; this scales it to the device's real range via
     * [scaleBrightnessUp]. Out-of-range [level] returns `false` without writing — the
     * desktop's clamp is not trusted. Returns `false` if [canWrite] is false or the
     * platform rejects the write; never throws [SecurityException].
     */
    fun setBrightness(auto: Boolean, level: Int?): Boolean {
        if (!canWrite()) return false
        if (auto) {
            return runCatching {
                Settings.System.putInt(
                    context.contentResolver,
                    Settings.System.SCREEN_BRIGHTNESS_MODE,
                    Settings.System.SCREEN_BRIGHTNESS_MODE_AUTOMATIC,
                )
                true
            }.getOrElse { false }
        }
        // Manual: level required and must be in 0..100.
        if (level == null || !isLevelInRange(level)) return false
        val scaled = scaleBrightnessUp(level)
        return runCatching {
            Settings.System.putInt(
                context.contentResolver,
                Settings.System.SCREEN_BRIGHTNESS_MODE,
                Settings.System.SCREEN_BRIGHTNESS_MODE_MANUAL,
            )
            Settings.System.putInt(
                context.contentResolver,
                Settings.System.SCREEN_BRIGHTNESS,
                scaled,
            )
            true
        }.getOrElse { false }
    }

    /** Current display state. Reads need no permission; entries are null only if unreadable. */
    data class DisplayState(
        val rotationMode: String,
        val brightnessAuto: Boolean,
        val brightnessLevel: Int,
    )

    /**
     * Reads the live rotation mode, brightness-mode flag and brightness level. Reads of
     * [Settings.System] need no permission, so this always returns a value (defaults are
     * used when a key is genuinely absent — which never happens on a real device).
     */
    fun readState(): DisplayState {
        val accel = readInt(Settings.System.ACCELEROMETER_ROTATION, 1)
        val user = readInt(Settings.System.USER_ROTATION, 0)
        val modeAuto = readInt(
            Settings.System.SCREEN_BRIGHTNESS_MODE,
            Settings.System.SCREEN_BRIGHTNESS_MODE_AUTOMATIC,
        ) == Settings.System.SCREEN_BRIGHTNESS_MODE_AUTOMATIC
        val raw = readInt(Settings.System.SCREEN_BRIGHTNESS, scaleBrightnessUp(50))
        val level = scaleBrightnessDown(raw)
        return DisplayState(
            rotationMode = rotationMode(accel, user),
            brightnessAuto = modeAuto,
            brightnessLevel = level,
        )
    }

    private fun readInt(key: String, default: Int): Int = runCatching {
        Settings.System.getInt(context.contentResolver, key, default)
    }.getOrDefault(default)

    companion object {
        /**
         * The platform maximum for [Settings.System.SCREEN_BRIGHTNESS]. The documented
         * stable range is 0–255 (the value is an int clamped to that range by the
         * platform; the open-source AOSP constant `BRIGHTNESS_MAX` is 255). There is no
         * public-API way to read the device's true per-panel ceiling — `PowerManager`'s
         * `BRIGHTNESS_MAX`/`BRIGHTNESS_CONSTRAINT` are `@SystemApi @hide` and not on the
         * SDK surface at compileSdk 35, so referencing them risks breaking the build.
         * 255 is the canonical documented clamp, is what every AOSP build uses, and is
         * what the platform reduces any out-of-range write to anyway.
         */
        internal const val PLATFORM_BRIGHTNESS_MAX = 255

        /**
         * Maps a wire rotation mode to the (ACCELEROMETER_ROTATION, USER_ROTATION) pair.
         * Returns `null` for an unknown mode so the caller can reject without writing.
         * Pure: unit-testable without a Context.
         */
        internal fun rotationValues(mode: String): Pair<Int, Int>? = when (mode) {
            "auto" -> 1 to 0
            "portrait" -> 0 to 0
            "landscape" -> 0 to 1
            else -> null
        }

        /**
         * Maps the platform (ACCELEROMETER_ROTATION, USER_ROTATION) pair back to the wire
         * rotation-mode string. USER_ROTATION has four legal values: 0 = portrait,
         * 1 = landscape, 2 = reversed portrait, 3 = reversed landscape. The reversed
         * orientations are wire-equivalent to their non-reversed counterparts (the wire
         * vocabulary is only portrait / landscape / auto), so 0/2 → "portrait" and 1/3 →
         * "landscape". Anything genuinely outside 0–3 falls back to "portrait" — a phone
         * stuck in an unexpected value shouldn't report a mode that doesn't exist on the
         * wire. Pure: unit-testable without a Context.
         */
        internal fun rotationMode(accel: Int, user: Int): String =
            if (accel == 1) "auto"
            else when (user) {
                1, 3 -> "landscape"
                else -> "portrait" // 0, 2 (reversed portrait), or anything else out of range
            }

        /** True when [level] is a valid 0–100 wire percentage. Pure. */
        internal fun isLevelInRange(level: Int): Boolean = level in 0..100

        /**
         * Scales a 0–100 wire percentage up to the platform's 0–[PLATFORM_BRIGHTNESS_MAX]
         * range. Rounded to the nearest integer so the full-scale round-trip is exact.
         * Pure: unit-testable without a Context.
         */
        internal fun scaleBrightnessUp(level: Int): Int {
            require(level in 0..100) { "level out of range: $level" }
            // Round to nearest so 100 -> max exactly and 0 -> 0 exactly.
            return ((level.toLong() * PLATFORM_BRIGHTNESS_MAX + 50) / 100).toInt()
        }

        /**
         * Scales a raw platform brightness value back down to the 0–100 wire percentage.
         * Coerces out-of-range reads into 0..100 (some OEMs persist values slightly
         * beyond 255). Inverse of [scaleBrightnessUp]. Pure.
         */
        internal fun scaleBrightnessDown(platformValue: Int): Int {
            // Round to nearest; clamp the [0,100] wire range.
            val pct = ((platformValue.toLong() * 100 + PLATFORM_BRIGHTNESS_MAX / 2) /
                PLATFORM_BRIGHTNESS_MAX).toInt()
            return pct.coerceIn(0, 100)
        }
    }
}
