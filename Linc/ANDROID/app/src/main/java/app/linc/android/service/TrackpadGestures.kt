package app.linc.android.service

import kotlin.math.roundToInt

/**
 * Pure gesture -> `pc.input` mapping for the Tools trackpad (M4a). Takes raw finger deltas
 * (view pixels) and returns what to send, free of any Android view/pointer types so a plain
 * unit test can call it directly. The Tools screen's pointer-input loop does the gesture
 * recognition (one vs. two fingers, tap-vs-drag) and forwards the results to [MirrorControl].
 */
object TrackpadGestures {

    /**
     * Finger-pixel -> PC-pixel multiplier for a one-finger drag. Raw 1:1 feels sluggish on a
     * high-DPI phone driving a 1920x1080 desktop over short thumb strokes; 2x is closer to a
     * laptop trackpad's feel. One line to retune.
     */
    const val MOVE_SENSITIVITY = 2f

    /**
     * Two-finger drag pixels per wheel notch. The desktop applies `WHEEL_DELTA` (120) per notch
     * it receives (`PcInputService.Scroll`), so this constant is the phone-side half of that
     * unit conversion — how far a two-finger drag must travel for one notch of scroll.
     */
    const val SCROLL_PIXELS_PER_NOTCH = 24f

    sealed interface Action {
        data class Move(val dx: Int, val dy: Int) : Action
        data class Scroll(val dx: Int, val dy: Int) : Action
    }

    /** One-finger drag delta -> a relative cursor move. */
    fun drag(rawDx: Float, rawDy: Float): Action.Move =
        Action.Move(
            (rawDx * MOVE_SENSITIVITY).roundToInt(),
            (rawDy * MOVE_SENSITIVITY).roundToInt(),
        )

    /** Two-finger drag delta -> a wheel scroll, in notches. */
    fun twoFingerDrag(rawDx: Float, rawDy: Float): Action.Scroll =
        Action.Scroll(
            (rawDx / SCROLL_PIXELS_PER_NOTCH).roundToInt(),
            (rawDy / SCROLL_PIXELS_PER_NOTCH).roundToInt(),
        )
}
