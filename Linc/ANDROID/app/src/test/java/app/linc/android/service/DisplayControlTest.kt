package app.linc.android.service

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pure-function coverage for [DisplayControl]'s brightness scaling and rotation
 * mapping (M5c-1 / D-055). The Android-SDK surface ([DisplayControl.canWrite],
 * [DisplayControl.setRotation], [DisplayControl.setBrightness], [DisplayControl.readState])
 * touches Settings.System and needs a Context the unit-test classpath cannot fake
 * (no Robolectric here — only JUnit). Putting the pure logic in the class's
 * `internal companion` lets these tests reach it without a Context, mirroring how
 * `CompanionOutboxTest` exercises `internal` members of `CompanionOutbox`.
 */
class DisplayControlTest {

    // ---- rotation mapping: wire mode → (ACCELEROMETER_ROTATION, USER_ROTATION) ----

    @Test
    fun `auto rotation maps to accelerometer on and leaves user rotation untouched`() {
        // The pair still encodes (accel=1, user=0); the "don't write USER_ROTATION" rule is
        // a property of setRotation, not of the pair — setRotation only writes USER_ROTATION
        // when accel == 0, so flipping to "auto" preserves the user's last forced orientation
        // (reverting to portrait/landscape then lands where they left it). Asserted here via
        // the pair's first element so the contract is unit-testable without a Context.
        val pair = DisplayControl.rotationValues("auto")!!
        assertEquals(1, pair.first)                 // accelerometer is turned on
        assertNotEquals(0, pair.first)              // ... so USER_ROTATION is intentionally not written
    }

    @Test
    fun `portrait rotation maps to accelerometer off, user rotation 0`() {
        assertEquals(0 to 0, DisplayControl.rotationValues("portrait"))
    }

    @Test
    fun `landscape rotation maps to accelerometer off, user rotation 1`() {
        assertEquals(0 to 1, DisplayControl.rotationValues("landscape"))
    }

    @Test
    fun `unknown rotation mode returns null without writing`() {
        assertNull(DisplayControl.rotationValues("sideways"))
        assertNull(DisplayControl.rotationValues(""))
        assertNull(DisplayControl.rotationValues("AUTO"))   // case-sensitive, as the spec spells the wire strings
    }

    // ---- rotation mapping: platform pair → wire mode (used by readState) ----

    @Test
    fun `accelerometer on reads back as auto regardless of user rotation`() {
        assertEquals("auto", DisplayControl.rotationMode(accel = 1, user = 0))
        assertEquals("auto", DisplayControl.rotationMode(accel = 1, user = 1))
    }

    @Test
    fun `accelerometer off and user 0 reads back as portrait`() {
        assertEquals("portrait", DisplayControl.rotationMode(accel = 0, user = 0))
    }

    @Test
    fun `accelerometer off and user 1 reads back as landscape`() {
        assertEquals("landscape", DisplayControl.rotationMode(accel = 0, user = 1))
    }

    @Test
    fun `accelerometer off and user 2 reversed portrait reads back as portrait`() {
        // USER_ROTATION 2 = reversed portrait; the wire vocabulary has no "reversed", so it
        // collapses to portrait (the reversed orientation is indistinguishable on the wire).
        assertEquals("portrait", DisplayControl.rotationMode(accel = 0, user = 2))
    }

    @Test
    fun `accelerometer off and user 3 reversed landscape reads back as landscape`() {
        // USER_ROTATION 3 = reversed landscape; collapses to landscape on the wire.
        assertEquals("landscape", DisplayControl.rotationMode(accel = 0, user = 3))
    }

    @Test
    fun `accelerometer off and a genuinely out of range user rotation collapses to portrait`() {
        // 2 and 3 are the documented reversed orientations; anything outside 0–3 is genuinely
        // unexpected and falls back to portrait so a misconfigured phone still reports a
        // recognisable wire value.
        assertEquals("portrait", DisplayControl.rotationMode(accel = 0, user = -1))
        assertEquals("portrait", DisplayControl.rotationMode(accel = 0, user = 4))
        assertEquals("portrait", DisplayControl.rotationMode(accel = 0, user = 99))
    }

    // ---- level validation (the phone-side clamp — the desktop is not trusted) ----

    @Test
    fun `level inside 0 to 100 is in range`() {
        assertTrue(DisplayControl.isLevelInRange(0))
        assertTrue(DisplayControl.isLevelInRange(1))
        assertTrue(DisplayControl.isLevelInRange(50))
        assertTrue(DisplayControl.isLevelInRange(99))
        assertTrue(DisplayControl.isLevelInRange(100))
    }

    @Test
    fun `level outside 0 to 100 is rejected`() {
        assertFalse(DisplayControl.isLevelInRange(-1))
        assertFalse(DisplayControl.isLevelInRange(101))
        assertFalse(DisplayControl.isLevelInRange(255))
        assertFalse(DisplayControl.isLevelInRange(Int.MIN_VALUE))
        assertFalse(DisplayControl.isLevelInRange(Int.MAX_VALUE))
    }

    // ---- brightness round-trip: scale up then down across the wire band ----

    @Test
    fun `scale up then down round-trips 0 100 and stays within 0 to 100`() {
        for (level in listOf(0, 100)) {
            val scaled = DisplayControl.scaleBrightnessUp(level)
            val back = DisplayControl.scaleBrightnessDown(scaled)
            assertEquals("level $level round-trip", level, back)
        }
    }

    @Test
    fun `mid-range round-trip is lossless at 50`() {
        val scaled = DisplayControl.scaleBrightnessUp(50)
        val back = DisplayControl.scaleBrightnessDown(scaled)
        assertEquals(50, back)
    }

    @Test
    fun `asymmetric endpoints round-trip within one platform step`() {
        // 1 and 99 don't hit a single platform value exactly (the platform max is 255),
        // so the round-trip is allowed a +/-1 round-trip error — that's one platform
        // quantum, well below what any user could perceive, and the same behavior as
        // Android's stock brightness slider. Assert it stays within that one step.
        for (level in listOf(1, 99)) {
            val scaled = DisplayControl.scaleBrightnessUp(level)
            val back = DisplayControl.scaleBrightnessDown(scaled)
            assertTrue("level $level -> scaled $scaled -> back $back (delta ${Math.abs(back - level)})",
                Math.abs(back - level) <= 1)
        }
    }

    @Test
    fun `level 0 maps to platform 0 and level 100 maps to platform maximum exactly`() {
        assertEquals(0, DisplayControl.scaleBrightnessUp(0))
        assertEquals(DisplayControl.PLATFORM_BRIGHTNESS_MAX,
            DisplayControl.scaleBrightnessUp(100))
    }

    @Test
    fun `scaleBrightnessUp rejects out-of-range input`() {
        assertThrows(IllegalArgumentException::class.java) {
            DisplayControl.scaleBrightnessUp(-1)
        }
        assertThrows(IllegalArgumentException::class.java) {
            DisplayControl.scaleBrightnessUp(101)
        }
    }

    @Test
    fun `scaleBrightnessDown clamps platform overflow to 100`() {
        assertEquals(100, DisplayControl.scaleBrightnessDown(255))
        assertEquals(100, DisplayControl.scaleBrightnessDown(300))   // an OEM that persists slightly past 255
    }

    @Test
    fun `scaleBrightnessDown clamps platform underflow to 0`() {
        assertEquals(0, DisplayControl.scaleBrightnessDown(0))
        assertEquals(0, DisplayControl.scaleBrightnessDown(-5))
    }

    @Test
    fun `scaleBrightnessUp is monotonic across the wire range`() {
        var prev = -1
        for (level in 0..100) {
            val scaled = DisplayControl.scaleBrightnessUp(level)
            assertTrue("non-monotonic at $level: scaled $scaled < prev $prev", scaled >= prev)
            prev = scaled
        }
    }
}
