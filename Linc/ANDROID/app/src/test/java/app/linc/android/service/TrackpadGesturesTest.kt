package app.linc.android.service

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Pure-function coverage for [TrackpadGestures] (M4a): the drag -> cursor-move and
 * two-finger-drag -> scroll unit conversions, independent of any Android pointer/view types.
 */
class TrackpadGesturesTest {

    // ---- drag: one-finger delta -> a relative cursor move, scaled by MOVE_SENSITIVITY ----

    @Test
    fun `sensitivity is currently 2x (raw finger pixels would feel sluggish 1_1)`() {
        // Pins the constant's value so a change to it is a deliberate, visible diff here,
        // not just an incidental pass/fail shift in the tests below.
        assertEquals(2f, TrackpadGestures.MOVE_SENSITIVITY)
    }

    @Test
    fun `a rightward-down drag scales by the sensitivity constant`() {
        val move = TrackpadGestures.drag(10f, 4f)
        assertEquals(TrackpadGestures.Action.Move(20, 8), move)
    }

    @Test
    fun `a leftward-up drag produces negative deltas`() {
        val move = TrackpadGestures.drag(-6f, -3f)
        assertEquals(TrackpadGestures.Action.Move(-12, -6), move)
    }

    @Test
    fun `zero movement produces a zero move`() {
        assertEquals(TrackpadGestures.Action.Move(0, 0), TrackpadGestures.drag(0f, 0f))
    }

    // ---- twoFingerDrag: two-finger delta -> scroll notches, by SCROLL_PIXELS_PER_NOTCH ----

    @Test
    fun `pixels-per-notch is currently 24`() {
        assertEquals(24f, TrackpadGestures.SCROLL_PIXELS_PER_NOTCH)
    }

    @Test
    fun `a two-finger drag of exactly one notch's worth of pixels scrolls one notch`() {
        val notch = TrackpadGestures.SCROLL_PIXELS_PER_NOTCH
        assertEquals(TrackpadGestures.Action.Scroll(0, 1), TrackpadGestures.twoFingerDrag(0f, notch))
    }

    @Test
    fun `a two-finger drag the other way scrolls the other way`() {
        val notch = TrackpadGestures.SCROLL_PIXELS_PER_NOTCH
        assertEquals(TrackpadGestures.Action.Scroll(0, -2), TrackpadGestures.twoFingerDrag(0f, -2 * notch))
    }

    @Test
    fun `a two-finger drag under one notch's worth of pixels rounds down to zero`() {
        val notch = TrackpadGestures.SCROLL_PIXELS_PER_NOTCH
        assertEquals(TrackpadGestures.Action.Scroll(0, 0), TrackpadGestures.twoFingerDrag(0f, notch * 0.4f))
    }

    @Test
    fun `horizontal two-finger drag scrolls dx independent of dy`() {
        val notch = TrackpadGestures.SCROLL_PIXELS_PER_NOTCH
        assertEquals(TrackpadGestures.Action.Scroll(1, 0), TrackpadGestures.twoFingerDrag(notch, 0f))
    }
}
