package app.linc.android.service

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * M17b Part A — the Kotlin twin of the desktop's UpdateDecision, held to the same table that
 * `tools\updatesim` holds the C# side to. The two must not drift: a phone that forces an update
 * the desktop calls optional (or vice versa) is a support problem with no error message.
 *
 * The load-bearing case is `a skip does not defeat a forced update` — everything else is
 * ordinary version arithmetic.
 */
class UpdateDecisionTest {

    // ---- the ten rows from the task file, in order ----

    @Test
    fun `equal versions all round means nothing to do`() {
        assertEquals(UpdateAction.None, UpdateDecision.decide("1.0.0", "1.0.0", "1.0.0", null))
    }

    @Test
    fun `a latest older than installed is not an update`() {
        assertEquals(UpdateAction.None, UpdateDecision.decide("1.2.0", "1.1.0", "1.0.0", null))
    }

    @Test
    fun `a newer latest is an optional update`() {
        assertEquals(UpdateAction.Optional, UpdateDecision.decide("1.0.0", "1.1.0", "1.0.0", null))
    }

    @Test
    fun `skipping a version suppresses that optional update`() {
        assertEquals(UpdateAction.None, UpdateDecision.decide("1.0.0", "1.1.0", "1.0.0", "1.1.0"))
    }

    @Test
    fun `a skip does not defeat a forced update`() {
        // THE load-bearing case: the user skipped 1.1.0, but the backend has declared everything
        // below 1.1.0 unsupported. Forced must win, or a skip becomes a permanent opt-out of
        // security updates.
        assertEquals(UpdateAction.Forced, UpdateDecision.decide("1.0.0", "1.1.0", "1.1.0", "1.1.0"))
    }

    @Test
    fun `versions compare numerically, so 1_10_0 is newer than 1_9_0`() {
        // A string compare gets this backwards ("1.10.0" < "1.9.0") and would silently stop
        // offering every update after the ninth minor release.
        assertEquals(UpdateAction.Optional, UpdateDecision.decide("1.9.0", "1.10.0", "1.0.0", null))
    }

    @Test
    fun `an empty manifest does nothing`() {
        assertEquals(UpdateAction.None, UpdateDecision.decide("1.0.0", null, null, null))
    }

    @Test
    fun `garbage in the manifest never forces an update`() {
        // The safety rule: a backend typo must not be able to brick every installed copy.
        assertEquals(UpdateAction.None, UpdateDecision.decide("1.0.0", "x.y.z", "x.y.z", null))
    }

    @Test
    fun `an unknown installed version does nothing`() {
        assertEquals(UpdateAction.None, UpdateDecision.decide(null, "1.1.0", "1.1.0", null))
    }

    @Test
    fun `a missing minimum is not a floor`() {
        assertEquals(UpdateAction.Optional, UpdateDecision.decide("1.0.0", "1.1.0", "", null))
    }

    // ---- parsing ----

    @Test
    fun `a v prefix and a pre-release suffix are both tolerated`() {
        assertEquals(UpdateAction.Optional, UpdateDecision.decide("v1.0.0", "v1.1.0-beta.2", "1.0.0", null))
    }

    @Test
    fun `a partly numeric version is malformed, not a truncation`() {
        // "1.x.0" must NOT read as "1.0.0" — that would silently mis-order releases.
        assertNull(UpdateDecision.parse("1.x.0"))
        assertNull(UpdateDecision.parse(""))
        assertNull(UpdateDecision.parse(null))
        assertNull(UpdateDecision.parse("1.2.3.4.5"))
    }

    @Test
    fun `a shorter version is padded, so 1_1 equals 1_1_0`() {
        assertEquals(0, UpdateDecision.compare(UpdateDecision.parse("1.1")!!, UpdateDecision.parse("1.1.0")!!))
    }

    @Test
    fun `compare orders numerically in both directions`() {
        assertEquals(-1, UpdateDecision.compare(UpdateDecision.parse("1.9.0")!!, UpdateDecision.parse("1.10.0")!!))
        assertEquals(1, UpdateDecision.compare(UpdateDecision.parse("1.10.0")!!, UpdateDecision.parse("1.9.0")!!))
    }

    @Test
    fun `the phone agrees with the desktop on every row of the table`() {
        // Same table, asserted as data, so a future edit to either side shows up as a diff here
        // rather than as two apps that disagree in the field.
        val table = listOf(
            listOf("1.0.0", "1.0.0", "1.0.0", null) to UpdateAction.None,
            listOf("1.2.0", "1.1.0", "1.0.0", null) to UpdateAction.None,
            listOf("1.0.0", "1.1.0", "1.0.0", null) to UpdateAction.Optional,
            listOf("1.0.0", "1.1.0", "1.0.0", "1.1.0") to UpdateAction.None,
            listOf("1.0.0", "1.1.0", "1.1.0", "1.1.0") to UpdateAction.Forced,
            listOf("1.9.0", "1.10.0", "1.0.0", null) to UpdateAction.Optional,
            listOf("1.0.0", null, null, null) to UpdateAction.None,
            listOf("1.0.0", "x.y.z", "x.y.z", null) to UpdateAction.None,
            listOf(null, "1.1.0", "1.1.0", null) to UpdateAction.None,
            listOf("1.0.0", "1.1.0", "", null) to UpdateAction.Optional,
        )
        table.forEach { (row, expected) ->
            assertEquals(
                "row $row",
                expected,
                UpdateDecision.decide(row[0], row[1], row[2], row[3]),
            )
        }
    }
}
