package app.linc.android.service

import kotlin.math.abs
import kotlin.math.hypot

/**
 * Turns finger gestures on the PC Remote view into mouse actions, with Windows App's two
 * pointer modes (v20, D-067). Pure Kotlin — no Android types — so every gesture is unit-tested.
 *
 * Positions arrive in view pixels; [toStream] maps them onto the PC's pixels through the
 * current zoom, and [viewPerStream] says how many view pixels one PC pixel covers right now.
 *
 * **Touch mode** (the pointer goes where you touch):
 *  - tap → left click there; drag → left-button drag from where the finger landed;
 *  - press and hold, release without moving → right click.
 *
 * **Mouse mode** (a trackpad; the pointer stays where it is until you drag it):
 *  - drag → moves the pointer, faster for faster swipes; tap → left click at the pointer;
 *  - press and hold, then drag → left-button drag (window moves, text selection);
 *  - press and hold, release without moving → right click.
 *
 * **Both modes:** a two-finger tap is a right click; two-finger movement is reported through
 * [Output.twoFinger] for the screen to scroll or pan with.
 *
 * Hold-then-drag rather than tap-then-drag for mouse-mode dragging: tap-then-drag needs every
 * tap's click held back for a double-tap window (laggy) or the PC sees click-then-press, which
 * is a double-click-drag — that maximises a window grabbed by its title bar.
 */
class PointerGestures(
    private val out: Output,
    var mode: MirrorSettings.PointerMode,
    private val toStream: (Float, Float) -> Pair<Float, Float>?,
    private val viewPerStream: () -> Float,
    private val streamSize: () -> Pair<Int, Int>,
) {
    interface Output {
        /** Put the pointer at a PC pixel — drawn locally at once and sent to the PC. */
        fun pointerTo(x: Float, y: Float)
        fun button(button: String, down: Boolean)
        /** Two fingers moved by this many view pixels (centroid). */
        fun twoFinger(dx: Float, dy: Float)
        /** A hold was recognised — a good moment for a haptic tick. */
        fun held()
    }

    private enum class State { IDLE, PENDING, HELD, DRAGGING, MOVING, TWO_FINGER, DONE }

    private var state = State.IDLE
    private var downX = 0f
    private var downY = 0f
    private var lastX = 0f
    private var lastY = 0f
    private var lastT = 0L
    private var downT = 0L
    private var twoDownT = 0L
    private var twoMoved = 0f
    private var lastCx = 0f
    private var lastCy = 0f

    /** The pointer in PC pixels, as this phone believes it to be. */
    var cursorX = 0f
        private set
    var cursorY = 0f
        private set

    /** Adopt the PC's report of where its pointer is (it moved for some other reason). */
    fun syncCursor(x: Float, y: Float) {
        if (state == State.IDLE || state == State.DONE) { cursorX = x; cursorY = y }
    }

    /** True while a finger is down: the PC's own cursor reports lag behind and are ignored. */
    val active: Boolean get() = state != State.IDLE && state != State.DONE

    fun down(x: Float, y: Float, t: Long) {
        state = State.PENDING
        downX = x; downY = y; lastX = x; lastY = y; lastT = t; downT = t
        if (mode == MirrorSettings.PointerMode.TOUCH) {
            // The pointer jumps under the finger immediately, so hover effects start and the
            // eventual click lands exactly here. A touch on the black letterbox is not on the
            // PC at all, and must not click wherever the pointer last was.
            val (sx, sy) = toStream(x, y) ?: run { state = State.DONE; return }
            moveCursor(sx, sy)
        }
    }

    /** The hold timer fired for the press that began at [startedAt]. */
    fun holdTimeout(startedAt: Long) {
        if (state != State.PENDING || startedAt != downT) return
        state = State.HELD
        out.held()
    }

    fun move(x: Float, y: Float, t: Long) {
        when (state) {
            State.PENDING -> if (hypot(x - downX, y - downY) > SLOP_PX) {
                if (mode == MirrorSettings.PointerMode.TOUCH) {
                    out.button("left", true)     // touch mode: a drag is a left drag at once
                    state = State.DRAGGING
                } else {
                    state = State.MOVING
                }
                track(x, y, t)
            }
            State.HELD -> if (hypot(x - downX, y - downY) > SLOP_PX) {
                out.button("left", true)         // hold, then drag: left-button drag
                state = State.DRAGGING
                track(x, y, t)
            }
            State.DRAGGING, State.MOVING -> track(x, y, t)
            else -> Unit
        }
    }

    fun up(x: Float, y: Float, t: Long) {
        when (state) {
            State.PENDING -> {
                // A tap. In touch mode the pointer is already under the finger.
                out.button("left", true)
                out.button("left", false)
            }
            State.HELD -> {
                out.button("right", true)
                out.button("right", false)
            }
            State.DRAGGING -> {
                track(x, y, t)
                out.button("left", false)
            }
            else -> Unit
        }
        state = State.IDLE
    }

    /** A second finger landed: whatever one finger was doing is over. */
    fun secondFingerDown(cx: Float, cy: Float, t: Long) {
        if (state == State.DRAGGING) out.button("left", false)
        state = State.TWO_FINGER
        twoDownT = t
        twoMoved = 0f
        lastCx = cx; lastCy = cy
    }

    fun twoFingerMove(cx: Float, cy: Float, pinching: Boolean) {
        if (state != State.TWO_FINGER) return
        val dx = cx - lastCx
        val dy = cy - lastCy
        lastCx = cx; lastCy = cy
        twoMoved += abs(dx) + abs(dy)
        if (pinching) { twoMoved += SLOP_PX; return }   // a pinch is never a tap
        out.twoFinger(dx, dy)
    }

    /** A finger lifted while two were down. The rest of the gesture is ignored until all lift. */
    fun secondFingerUp(t: Long) {
        if (state != State.TWO_FINGER) return
        if (t - twoDownT < TWO_FINGER_TAP_MS && twoMoved < SLOP_PX) {
            out.button("right", true)
            out.button("right", false)
        }
        state = State.DONE
    }

    fun cancel() {
        if (state == State.DRAGGING) out.button("left", false)
        state = State.IDLE
    }

    private fun track(x: Float, y: Float, t: Long) {
        if (mode == MirrorSettings.PointerMode.TOUCH) {
            toStream(x, y)?.let { (sx, sy) -> moveCursor(sx, sy) }
        } else {
            val dt = (t - lastT).coerceAtLeast(1)
            val dx = x - lastX
            val dy = y - lastY
            val gain = gain(hypot(dx, dy) / dt) / viewPerStream().coerceAtLeast(0.01f)
            moveCursor(cursorX + dx * gain, cursorY + dy * gain)
        }
        lastX = x; lastY = y; lastT = t
    }

    private fun moveCursor(x: Float, y: Float) {
        val (w, h) = streamSize()
        cursorX = x.coerceIn(0f, (w - 1).coerceAtLeast(0).toFloat())
        cursorY = y.coerceIn(0f, (h - 1).coerceAtLeast(0).toFloat())
        out.pointerTo(cursorX, cursorY)
    }

    companion object {
        /** Movement (view px) under which a press is still a tap/hold, not a drag. */
        const val SLOP_PX = 24f
        const val HOLD_MS = 450L
        const val TWO_FINGER_TAP_MS = 250L

        /**
         * Pointer acceleration for mouse mode, by finger speed in view px/ms: slow movement is
         * 1:1 for precision, a flick crosses the screen. Shaped like a touchpad's curve.
         */
        fun gain(speedPxPerMs: Float): Float = 1f + (speedPxPerMs * 0.9f).coerceAtMost(2.5f)
    }
}
