package app.linc.android.service

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The Remote trackpad's gesture engine (1.0.0.11): taps survive jitter, two-finger scroll
 * accumulates instead of rounding to nothing, slow drags still move, tap-and-drag drags.
 */
class TrackpadGesturesTest {

    private class Recorder : TrackpadGestures.Output {
        val events = mutableListOf<String>()
        var moveX = 0; var moveY = 0
        var wheelX = 0; var wheelY = 0
        override fun move(dx: Int, dy: Int) { moveX += dx; moveY += dy; events += "move" }
        override fun button(button: String, down: Boolean) { events += "$button ${if (down) "down" else "up"}" }
        override fun scroll(dx: Int, dy: Int) { wheelX += dx; wheelY += dy; events += "scroll" }
        fun clicks() = events.filter { it != "move" && it != "scroll" }
    }

    private val slop = 8f           // px
    private val density = 2f        // px per dp
    private fun engine(r: Recorder, unit: Int = 1) =
        TrackpadGestures(r, touchSlopPx = slop, density = density, wheelUnit = { unit })

    @Test
    fun `a tap with a little jitter is still a click, sent after the double-tap window`() {
        val r = Recorder(); val g = engine(r)
        g.press(0, 100f, 100f)
        g.update(10, 1, 101f, 100f)    // 1 px wobble — under the slop
        g.update(20, 1, 103f, 102f)
        g.release(60)
        assertTrue("no click before the window passes", r.clicks().isEmpty())
        g.flush(60 + TrackpadGestures.DOUBLE_TAP_MS)
        assertEquals(listOf("left down", "left up"), r.clicks())
        assertEquals(0, r.moveX); assertEquals(0, r.moveY)
        assertNull(g.pendingTapAt)
    }

    @Test
    fun `a drag past the slop moves and does not click`() {
        val r = Recorder(); val g = engine(r)
        g.press(0, 0f, 0f)
        var t = 0L
        for (i in 1..20) { t += 16; g.update(t, 1, i * 4f, 0f) }
        g.release(t + 16)
        g.flush(t + 1000)
        assertTrue(r.clicks().isEmpty())
        assertTrue("moved right: ${r.moveX}", r.moveX > 0)
        assertEquals(0, r.moveY)
    }

    @Test
    fun `slow drags accumulate fractions instead of rounding them away`() {
        val r = Recorder(); val g = engine(r)
        g.press(0, 0f, 0f)
        g.update(16, 1, 10f, 0f)        // past the slop
        val before = r.moveX
        var x = 10f; var t = 16L
        // 0.1 px per event: every single event alone is far under one PC pixel.
        repeat(400) { x += 0.1f; t += 16; g.update(t, 1, x, 0f) }
        val travelledDp = 40f / density
        val expected = travelledDp * TrackpadGestures.GAIN_SLOW
        assertEquals(expected.toDouble(), (r.moveX - before).toDouble(), 1.5)
    }

    @Test
    fun `fast swipes go further than slow ones over the same distance`() {
        fun travel(stepMs: Long): Int {
            val r = Recorder(); val g = engine(r)
            g.press(0, 0f, 0f)
            g.update(1, 1, 10f, 0f)
            var t = 1L
            for (i in 1..20) { t += stepMs; g.update(t, 1, 10f + i * 10f, 0f) }
            return r.moveX
        }
        val slow = travel(100)  // 5 px/100 ms = 0.05 dp/ms
        val fast = travel(4)    // 1.25 dp/ms
        assertTrue("fast $fast should beat slow $slow", fast > slow * 2)
    }

    @Test
    fun `acceleration is flat when slow and capped when fast`() {
        assertEquals(TrackpadGestures.GAIN_SLOW, TrackpadGestures.gain(0f))
        assertEquals(TrackpadGestures.GAIN_SLOW, TrackpadGestures.gain(0.1f))
        assertEquals(TrackpadGestures.GAIN_FAST, TrackpadGestures.gain(10f))
        assertTrue(TrackpadGestures.gain(0.8f) > TrackpadGestures.gain(0.4f))
    }

    @Test
    fun `two-finger scroll accumulates small moves into wheel units`() {
        val r = Recorder(); val g = engine(r)
        g.press(0, 100f, 100f)
        g.update(5, 2, 100f, 100f)      // second finger lands: re-base, no movement
        var y = 100f; var t = 5L
        repeat(60) { y += 1f; t += 8; g.update(t, 2, 100f, y) }   // 60 px down, 1 px at a time
        g.release(t + 8)
        // Moving the fingers down scrolls up (positive wheel), as Mirror PC does.
        val expected = (60f - slop) / density * TrackpadGestures.WHEEL_PER_DP
        assertEquals(expected.toDouble(), r.wheelY.toDouble(), 6.0 + slop / density * TrackpadGestures.WHEEL_PER_DP)
        assertTrue(r.wheelY > 100)
        assertTrue("no clicks from a scroll", r.clicks().isEmpty())
        assertEquals(0, r.moveX); assertEquals(0, r.moveY)
    }

    @Test
    fun `old PCs get whole notches and the remainder is kept`() {
        val r = Recorder(); val g = engine(r, unit = 120)
        g.press(0, 0f, 0f)
        g.update(1, 2, 0f, 0f)
        var y = 0f; var t = 1L
        repeat(200) { y += 1f; t += 8; g.update(t, 2, 0f, y) }
        assertEquals(0, r.wheelY % 120)
        assertTrue(r.wheelY >= 240)
    }

    @Test
    fun `a two-finger tap is a right click, at once`() {
        val r = Recorder(); val g = engine(r)
        g.press(0, 100f, 100f)
        g.update(20, 2, 120f, 100f)
        g.update(40, 2, 121f, 101f)
        g.release(90)
        assertEquals(listOf("right down", "right up"), r.clicks())
        assertNull(g.pendingTapAt)
    }

    @Test
    fun `tap then touch-and-drag is a left-button drag with no click first`() {
        val r = Recorder(); val g = engine(r)
        g.press(0, 50f, 50f); g.release(80)
        g.press(150, 50f, 50f)            // inside the double-tap window
        g.update(170, 1, 70f, 50f)        // past the slop
        g.update(190, 1, 90f, 50f)
        g.release(400)
        g.flush(2000)
        assertEquals(listOf("left down", "left up"), r.clicks())
        assertTrue(r.events.indexOf("left down") < r.events.indexOf("move"))
        assertTrue(r.moveX > 0)
    }

    @Test
    fun `two quick taps are a double click`() {
        val r = Recorder(); val g = engine(r)
        g.press(0, 50f, 50f); g.release(60)
        g.press(150, 50f, 50f); g.release(200)
        assertEquals(listOf("left down", "left up", "left down", "left up"), r.clicks())
        assertNull(g.pendingTapAt)
    }

    @Test
    fun `a second touch after the window is a fresh tap and the first one clicks`() {
        val r = Recorder(); val g = engine(r)
        g.press(0, 50f, 50f); g.release(60)
        g.press(60 + TrackpadGestures.DOUBLE_TAP_MS + 50, 50f, 50f)
        assertEquals(listOf("left down", "left up"), r.clicks())
    }

    @Test
    fun `a long still press is not a click`() {
        val r = Recorder(); val g = engine(r)
        g.press(0, 50f, 50f); g.release(TrackpadGestures.TAP_MAX_MS + 200)
        g.flush(5000)
        assertTrue(r.clicks().isEmpty())
    }

    @Test
    fun `cancel mid-drag releases the button`() {
        val r = Recorder(); val g = engine(r)
        g.press(0, 50f, 50f); g.release(50)
        g.press(100, 50f, 50f)
        g.update(120, 1, 90f, 50f)
        g.cancel()
        assertEquals(listOf("left down", "left up"), r.clicks())
    }

    @Test
    fun `a second finger joining does not jump the pointer`() {
        val r = Recorder(); val g = engine(r)
        g.press(0, 0f, 0f)
        g.update(16, 1, 20f, 0f)
        val x = r.moveX
        g.update(32, 2, 300f, 300f)       // centroid jumps when the second finger lands
        assertEquals(x, r.moveX)
        assertEquals(0, r.wheelY)
    }
}
