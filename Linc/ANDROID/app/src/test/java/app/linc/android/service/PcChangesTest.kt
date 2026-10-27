package app.linc.android.service

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** Settings › Undo changes from your PC: what counts as changed (PcChanges.detect). */
class PcChangesTest {

    private val normal = PcChanges.Snapshot(
        realW = 1080, realH = 2400, panelW = 1080, panelH = 2400, shapeActive = false,
        accelerometerRotation = 1, userRotation = 0, stayOn = 0, showTouches = 0, demoOn = 0,
        animationScales = listOf(1f, 1f, 1f),
    )

    private fun items(s: PcChanges.Snapshot) = PcChanges.detect(s).map { it.item }

    @Test
    fun `a phone as it came is not flagged`() {
        assertTrue(PcChanges.detect(normal).isEmpty())
        // Turned sideways, the real size swaps; the panel's doesn't: still normal.
        assertTrue(PcChanges.detect(normal.copy(realW = 2400, realH = 1080)).isEmpty())
    }

    @Test
    fun `fill this monitor's size is caught, with both sizes in words`() {
        val changes = PcChanges.detect(normal.copy(realW = 1080, realH = 1920))
        assertEquals(listOf(PcChanges.Item.SCREEN), changes.map { it.item })
        assertEquals("Now 1080×1920; its own is 1080×2400", changes.single().now)
        assertEquals(listOf(PcChanges.Item.SCREEN), items(normal.copy(shapeActive = true)))
        // Android wouldn't say the panel size: never guessed as changed.
        assertTrue(items(normal.copy(realW = 1080, realH = 1920, panelW = 0, panelH = 0)).isEmpty())
    }

    @Test
    fun `rotation counts only when locked sideways`() {
        assertEquals(listOf(PcChanges.Item.ROTATION), items(normal.copy(accelerometerRotation = 0, userRotation = 1)))
        assertEquals(listOf(PcChanges.Item.ROTATION), items(normal.copy(accelerometerRotation = 0, userRotation = 3)))
        assertTrue("locked upright is the owner's own choice", items(normal.copy(accelerometerRotation = 0, userRotation = 0)).isEmpty())
        assertTrue("auto-rotate on", items(normal.copy(accelerometerRotation = 1, userRotation = 1)).isEmpty())
    }

    @Test
    fun `stay awake, touches and demo mode`() {
        assertEquals(listOf(PcChanges.Item.STAY_AWAKE), items(normal.copy(stayOn = 3)))
        assertEquals(listOf(PcChanges.Item.SHOW_TOUCHES), items(normal.copy(showTouches = 1)))
        assertEquals(listOf(PcChanges.Item.DEMO_MODE), items(normal.copy(demoOn = 1)))
        assertTrue("unreadable values are not changes",
            items(normal.copy(stayOn = null, showTouches = null, demoOn = null, accelerometerRotation = null)).isEmpty())
    }

    @Test
    fun `animation speed in words`() {
        assertEquals("Animations at 0.5×", PcChanges.detect(normal.copy(animationScales = listOf(0.5f, 0.5f, 0.5f))).single().now)
        assertEquals("Animations are off", PcChanges.detect(normal.copy(animationScales = listOf(0f, 0f, 0f))).single().now)
        assertEquals("Animation speeds changed", PcChanges.detect(normal.copy(animationScales = listOf(1f, 0.5f, 1f))).single().now)
        assertEquals("Animations at 2×", PcChanges.detect(normal.copy(animationScales = listOf(2f, 2f, 2f))).single().now)
        assertTrue(items(normal.copy(animationScales = listOf(null, null, null))).isEmpty())
    }
}
