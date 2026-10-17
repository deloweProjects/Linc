package app.linc.android.service

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** D-075: only the system's denial for exactly this package starts a background clipboard read. */
class ClipboardWatcherTest {

    private val pkg = "app.linc.android"

    @Test
    fun `the denial for this package triggers`() {
        assertTrue(ClipboardWatcher.isDenialFor(
            "E/ClipboardService( 1234): Denying clipboard access to app.linc.android, application is not in focus nor is it a system service for user 0",
            pkg))
    }

    @Test
    fun `threadtime format triggers too`() {
        assertTrue(ClipboardWatcher.isDenialFor(
            "10-17 22:40:01.123  1234  1300 E ClipboardService: Denying clipboard access to app.linc.android, application is not in focus",
            pkg))
    }

    @Test
    fun `another package does not`() {
        assertFalse(ClipboardWatcher.isDenialFor(
            "E/ClipboardService( 1234): Denying clipboard access to com.example.notes, application is not in focus", pkg))
        assertFalse(ClipboardWatcher.isDenialFor(
            "E/ClipboardService( 1234): Denying clipboard access to app.linc.android.debug, application is not in focus", pkg))
    }

    @Test
    fun `unrelated clipboard lines do not`() {
        assertFalse(ClipboardWatcher.isDenialFor("E/ClipboardService( 1234): Something else about app.linc.android", pkg))
        assertFalse(ClipboardWatcher.isDenialFor("--------- beginning of system", pkg))
    }
}
