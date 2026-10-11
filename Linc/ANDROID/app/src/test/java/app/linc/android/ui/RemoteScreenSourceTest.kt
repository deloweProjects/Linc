package app.linc.android.ui

import java.io.File
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Guards the Remote page's **"disable, never hide"** rule (M4b, carried from Tools into Remote in
 * 1.0.0.11): a control the PC cannot honour renders disabled with a plain-language reason, never
 * enabled-but-inert and never missing.
 *
 * A source-text check, as ToolsScreenSourceTest was: the thing under test is a `@Composable`'s
 * `enabled =` argument, unreachable from this module's plain-JUnit setup (no Robolectric, no
 * Compose test rule). It proves the gate is written, not that Compose honours it.
 */
class RemoteScreenSourceTest {

    private val source: String by lazy { read("src/main/java/app/linc/android/ui/RemoteScreen.kt") }

    private fun requireExact(what: String, needle: String) {
        assertTrue(
            "RemoteScreen.kt no longer contains $what.\n  expected: $needle\n" +
                "  A control the PC cannot honour must be DISABLED with a reason, never hidden.",
            source.contains(needle),
        )
    }

    @Test
    fun `the pc-state capability flags are what power and brightness are gated on`() {
        requireExact("canSleep from pc.state", "val canSleep = state?.canSleep ?: true")
        requireExact("canShutdown from pc.state", "val canShutdown = state?.canShutdown ?: true")
        requireExact("canBrightness from pc.state", "val canBrightness = state?.canBrightness ?: true")
        requireExact("Sleep gated", "enabled = ready && canSleep")
        requireExact("Restart and Shut down gated", "enabled = ready && canShutdown")
        requireExact("brightness gated", "enabled = ready && canBrightness")
        requireExact("brightness reason", "This PC's screen brightness can't be controlled remotely.")
    }

    @Test
    fun `restart and shut down confirm first`() {
        requireExact("restart goes through the confirm", "onClick = { confirm = \"restart\" }")
        requireExact("shut down goes through the confirm", "onClick = { confirm = \"shutdown\" }")
    }

    @Test
    fun `the media transport is gated on the PC having a player`() {
        requireExact("present flag", "val playing = media?.present == true")
        requireExact("transport gate", "val canTransport = transportReason == null && playing")
        requireExact("previous gated", "PcMediaControl.send(\"prev\") }, enabled = canTransport")
        requireExact("next gated", "PcMediaControl.send(\"next\") }, enabled = canTransport")
        requireExact("idle PC says so", "Nothing is playing on the PC")
    }

    @Test
    fun `toolbar items are disabled by their own gate, never removed`() {
        requireExact("keyboard", "ToolbarItem(LincSymbols.Keyboard, \"Keyboard\", inputReady)")
        requireExact("media", "ToolbarItem(LincSymbols.MusicNote, \"Media\", mediaReady)")
        requireExact("present", "ToolbarItem(LincSymbols.Slideshow, \"Present\", inputReady)")
        requireExact("power", "ToolbarItem(LincSymbols.PowerOff, \"Power\", controlsReady)")
    }

    @Test
    fun `the presentation sheet says it is only key presses`() {
        requireExact("no slideshow detection", "there's no slideshow app detection")
    }

    @Test
    fun `the trackpad explains itself when blocked and never sits in a scrolling page`() {
        requireExact("blocked trackpad", "title = \"Trackpad unavailable\"")
        assertTrue("Remote must not scroll the page under the trackpad", !source.contains("Modifier.verticalScroll(rememberScrollState()).padding(horizontal = Kit.gutter)"))
    }

    private fun read(relative: String): String {
        var dir: File? = File(System.getProperty("user.dir") ?: ".").absoluteFile
        while (dir != null) {
            listOf(File(dir, relative), File(dir, "app/$relative")).firstOrNull { it.isFile }?.let { return it.readText() }
            dir = dir.parentFile
        }
        throw AssertionError("Could not find $relative — this check cannot run, which is a FAILURE, not a skip.")
    }
}
