package app.linc.android.service

import kotlin.math.abs
import kotlin.math.hypot
import kotlin.math.max

/**
 * The Remote page's trackpad (M4a, reworked 1.0.0.11): finger events in, mouse actions out.
 * Pure Kotlin — no Android types — so every gesture is unit-tested; the Compose loop only feeds
 * it pointer counts and positions.
 *
 *  - **one finger drags** → relative pointer moves, with mild acceleration (slow = precise,
 *    fast = far) and fractional remainders carried between events, so slow drags still move;
 *  - **tap** → left click (held back for [DOUBLE_TAP_MS] so a second tap can turn it into a
 *    double click or a drag — what a Windows precision touchpad does);
 *  - **tap, then touch again and drag** → left-button drag (windows, text selection);
 *  - **two-finger tap** → right click;
 *  - **two fingers moving** → smooth scroll in wheel units (120 = one notch), remainders kept.
 *
 * Movement under the touch slop is jitter, not a drag: before 1.0.0.11 any 1 px wobble cancelled
 * the click, and two-finger scroll rounded each event's few pixels to zero notches, so it never
 * scrolled at all.
 *
 * Positions are view pixels; [density] converts them to dp so the feel is the same on every
 * phone. Time is passed in (milliseconds, any monotonic clock).
 */
class TrackpadGestures(
    private val out: Output,
    private val touchSlopPx: Float,
    private val density: Float,
    /** 1 when the PC takes raw wheel units (v20); 120 when it only understands whole notches. */
    private val wheelUnit: () -> Int = { 1 },
) {
    interface Output {
        fun move(dx: Int, dy: Int)
        fun button(button: String, down: Boolean)
        /** Wheel units, 120 = one notch; positive dy scrolls up, positive dx scrolls right. */
        fun scroll(dx: Int, dy: Int)
    }

    private var active = false
    private var maxFingers = 0
    private var fingers = 0
    private var downT = 0L
    private var startX = 0f
    private var startY = 0f
    private var lastX = 0f
    private var lastY = 0f
    private var lastT = 0L
    private var moved = false        // past the touch slop at some point in this gesture
    private var secondTap = false    // this touch began inside the previous tap's window
    private var dragging = false     // left button held for a tap-and-drag
    private var moveAccX = 0f
    private var moveAccY = 0f
    private var wheelAccX = 0f
    private var wheelAccY = 0f

    /** When a single tap's click is due; null when none is waiting. */
    var pendingTapAt: Long? = null
        private set

    /** The first finger touched the pad. */
    fun press(t: Long, x: Float, y: Float) {
        val pending = pendingTapAt
        secondTap = pending != null && t <= pending
        if (pending != null && !secondTap) flush(t)
        pendingTapAt = null
        active = true
        maxFingers = 1; fingers = 1
        downT = t; lastT = t
        startX = x; startY = y; lastX = x; lastY = y
        moved = false; dragging = false
        moveAccX = 0f; moveAccY = 0f; wheelAccX = 0f; wheelAccY = 0f
    }

    /**
     * Fingers moved. [count] is how many are down now, ([x], [y]) the centroid of up to two of
     * them. A change of finger count re-bases the position so the centroid's jump isn't a move.
     */
    fun update(t: Long, count: Int, x: Float, y: Float) {
        if (!active || count <= 0) return
        if (count != fingers) {
            fingers = count
            maxFingers = max(maxFingers, count)
            lastX = x; lastY = y; lastT = t
            if (count >= 2) {
                startX = x; startY = y
                // A second finger on a would-be second tap: the first tap was just a click.
                if (secondTap && !dragging) { secondTap = false; click("left") }
            }
            return
        }
        if (!moved && hypot(x - startX, y - startY) > touchSlopPx) {
            moved = true
            // The jump past the slop isn't lost: measure the first move from where it started.
            lastX = startX; lastY = startY
        }
        if (!moved) return

        val dx = x - lastX
        val dy = y - lastY
        val dt = max(1L, t - lastT)
        lastX = x; lastY = y; lastT = t
        if (dx == 0f && dy == 0f) return

        if (maxFingers >= 2) {
            scrollBy(dx, dy)
        } else {
            if (secondTap && !dragging) {
                dragging = true
                out.button("left", true)
            }
            moveBy(dx, dy, dt)
        }
    }

    /** Every finger lifted. */
    fun release(t: Long) {
        if (!active) return
        active = false
        val quick = t - downT <= TAP_MAX_MS
        when {
            dragging -> out.button("left", false)
            moved -> Unit
            maxFingers >= 2 -> if (quick) click("right")
            secondTap -> {
                // Two quick taps are a double click, sent at once; a slow second touch that
                // never moved still owes the first tap its click.
                click("left")
                if (quick) click("left")
            }
            quick -> pendingTapAt = t + DOUBLE_TAP_MS
        }
        dragging = false
        secondTap = false
    }

    /** Send a waiting tap's click once its double-tap window has passed. */
    fun flush(t: Long) {
        val due = pendingTapAt ?: return
        if (t < due && active) return
        pendingTapAt = null
        click("left")
    }

    /** The pad lost the gesture (the screen went away mid-touch): let go of anything held. */
    fun cancel() {
        if (dragging) out.button("left", false)
        active = false; dragging = false; secondTap = false; pendingTapAt = null
    }

    private fun click(button: String) {
        out.button(button, true)
        out.button(button, false)
    }

    private fun moveBy(dx: Float, dy: Float, dt: Long) {
        val dxDp = dx / density
        val dyDp = dy / density
        val gain = gain(hypot(dxDp, dyDp) / dt)
        moveAccX += dxDp * gain
        moveAccY += dyDp * gain
        val ix = moveAccX.toInt()
        val iy = moveAccY.toInt()
        if (ix == 0 && iy == 0) return
        moveAccX -= ix; moveAccY -= iy
        out.move(ix, iy)
    }

    private fun scrollBy(dx: Float, dy: Float) {
        // Same direction as Mirror PC's two-finger scroll: content follows the fingers.
        wheelAccX += -dx / density * WHEEL_PER_DP
        wheelAccY += dy / density * WHEEL_PER_DP
        val unit = max(1, wheelUnit())
        val qx = wheelAccX.toInt() / unit * unit
        val qy = wheelAccY.toInt() / unit * unit
        if (qx == 0 && qy == 0) return
        wheelAccX -= qx; wheelAccY -= qy
        out.scroll(qx, qy)
    }

    companion object {
        /** A touch longer than this isn't a tap. */
        const val TAP_MAX_MS = 250L

        /** How long a tap waits for a second touch before it clicks. */
        const val DOUBLE_TAP_MS = 220L

        /** Wheel units per dp of two-finger travel (a notch every ~20 dp). */
        const val WHEEL_PER_DP = 6f

        /** PC pixels per dp at a slow, careful speed. */
        const val GAIN_SLOW = 2.5f

        /** PC pixels per dp at a fast flick. */
        const val GAIN_FAST = 8f

        /**
         * Pointer acceleration: PC pixels per dp of finger travel at [speed] dp/ms. Flat at
         * [GAIN_SLOW] for slow drags (precise), rising linearly to [GAIN_FAST] by ~1.5 dp/ms.
         */
        fun gain(speed: Float): Float {
            val over = (abs(speed) - 0.15f).coerceIn(0f, 1.4f)
            return GAIN_SLOW + (GAIN_FAST - GAIN_SLOW) * over / 1.4f
        }
    }
}
