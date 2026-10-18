package app.linc.android.service

import app.linc.android.service.Explanations.Kind
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** "Show explanations" (D-084): only helper text ever hides. */
class ExplanationsTest {

    @Test
    fun `with explanations on, everything shows`() {
        Kind.entries.forEach { assertTrue(it.name, Explanations.shows(it, enabled = true)) }
    }

    @Test
    fun `with explanations off, helper text hides`() {
        assertFalse(Explanations.shows(Kind.Helper, enabled = false))
    }

    @Test
    fun `warnings, errors and blocked reasons never hide`() {
        assertTrue(Explanations.shows(Kind.Warning, enabled = false))
        assertTrue(Explanations.shows(Kind.Error, enabled = false))
        assertTrue(Explanations.shows(Kind.Blocked, enabled = false))
    }
}
