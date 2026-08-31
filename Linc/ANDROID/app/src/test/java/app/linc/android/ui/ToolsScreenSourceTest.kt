package app.linc.android.ui

import java.io.File
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Guards the Tools page's **"disable, never hide"** rule (M4b, carried into M4c): a control the PC
 * cannot honour must render disabled with a plain-language reason, never enabled-but-inert and
 * never missing.
 *
 * This asserts against the PRODUCTION SOURCE TEXT of `ToolsScreen.kt` rather than calling it,
 * because the thing under test is a `@Composable`'s `enabled =` argument — unreachable from this
 * module's plain-JUnit setup (no Robolectric, no Compose test rule). GUIDE.md §4.1 sanctions
 * exactly this shape when the harness genuinely cannot call the production code, and §4.4 asks
 * that it be tight enough to fail: every assertion below pins a control's `enabled` expression to
 * that control's own handler in one string, so it cannot be satisfied by the words appearing
 * somewhere else in the file.
 *
 * **Known weakness, stated rather than hidden:** this is a text match. It proves the gate is
 * written; it cannot prove Compose honours it. A rename that changed both the handler and the flag
 * consistently would need this file updated too — that is the intended cost.
 */
class ToolsScreenSourceTest {

    private val source: String by lazy { readToolsScreenSource() }

    private fun requireExact(what: String, needle: String) {
        assertTrue(
            "ToolsScreen.kt no longer contains $what.\n" +
                "  expected to find: $needle\n" +
                "  A control the PC cannot honour must be DISABLED with a reason, never rendered " +
                "enabled-but-inert and never hidden.",
            source.contains(needle),
        )
    }

    // ---- the capability flags are still read from the pc.state reply ----

    @Test
    fun `the pc-state capability flags are still what the buttons are gated on`() {
        requireExact("canSleep read from pc.state", "val canSleep = state?.canSleep ?: true")
        requireExact("canShutdown read from pc.state", "val canShutdown = state?.canShutdown ?: true")
        requireExact("canBrightness read from pc.state", "val canBrightness = state?.canBrightness ?: true")
    }

    // ---- quick controls: each button pinned to its own capability gate ----

    @Test
    fun `sleep is disabled when the PC reports it cannot sleep`() {
        requireExact(
            "the Sleep button gated on canSleep",
            "OutlinedButton(onClick = onSleep, enabled = ready && canSleep)",
        )
    }

    @Test
    fun `shutdown and restart are disabled when the PC reports it cannot shut down`() {
        requireExact(
            "the Shut down button gated on canShutdown",
            "OutlinedButton(onClick = onShutdown, enabled = ready && canShutdown)",
        )
        requireExact(
            "the Restart button gated on canShutdown",
            "OutlinedButton(onClick = onRestart, enabled = ready && canShutdown)",
        )
    }

    @Test
    fun `both brightness buttons are disabled when the PC reports no brightness control`() {
        requireExact(
            "the brightness − button gated on canBrightness",
            "onClick = { onDimmer(level) }, enabled = ready && canBrightness",
        )
        requireExact(
            "the brightness + button gated on canBrightness",
            "onClick = { onBrighter(level) }, enabled = ready && canBrightness",
        )
    }

    @Test
    fun `an unsupported control still explains itself in plain language`() {
        requireExact(
            "the plain-language reason for brightness",
            "This PC's screen brightness can't be controlled remotely.",
        )
    }

    // ---- M4c: the media transport is gated on the PC actually having a player ----

    @Test
    fun `the media transport is gated on the PC having something to control`() {
        // pc.media.state with `none: true` means PcMediaService.OnCompanionMessage reaches
        // neither its SMTC branch nor the Winamp-API fallback, so the press would be dropped.
        requireExact("the present flag read from pc.media.state", "val playing = media?.present == true")
        requireExact(
            "the transport gate combining the version gate and the present flag",
            "val canTransport = transportReady && playing",
        )
    }

    @Test
    fun `all three transport buttons are gated on canTransport`() {
        requireExact("the Previous button gated on canTransport", "OutlinedButton(onClick = onPrevious, enabled = canTransport)")
        requireExact("the Play/Pause button gated on canTransport", "Button(onClick = onPlayPause, enabled = canTransport)")
        requireExact("the Next button gated on canTransport", "OutlinedButton(onClick = onNext, enabled = canTransport)")
    }

    @Test
    fun `the media block says why there is nothing to control`() {
        requireExact("the plain-language reason for an idle PC", "Nothing is playing on the PC right now.")
    }

    @Test
    fun `the volume row is gated on the v17 quick-controls connection`() {
        requireExact("the volume − button gated on volumeReady", "OutlinedButton(onClick = onVolumeDown, enabled = volumeReady)")
        requireExact("the volume + button gated on volumeReady", "OutlinedButton(onClick = onVolumeUp, enabled = volumeReady)")
        requireExact("the mute button gated on volumeReady", "OutlinedButton(onClick = onMuteToggle, enabled = volumeReady)")
    }

    @Test
    fun `the presentation block says it is only key presses`() {
        requireExact(
            "the one-line statement that the clicker knows nothing about slides",
            "there's no slideshow app detection",
        )
    }

    // ---- M16 Part B: the page scrolls, and the trackpad has a floor ----

    @Test
    fun `the Tools page has a scroll host`() {
        // M5a's standing lesson: every page needs a scroll host from day one. Without it the
        // four blocks M4c left behind cannot all fit on a normal phone.
        requireExact(
            "the root Column's verticalScroll",
            ".verticalScroll(rememberScrollState())",
        )
    }

    @Test
    fun `the trackpad has a minimum height and no longer takes a weight`() {
        requireExact("the trackpad's minimum height", ".heightIn(min = 280.dp)")
        // weight(1f) inside a scrolling Column is a runtime error, and it was also the reason
        // the trackpad shrank every time a card was added — so it must be gone, not merely
        // accompanied by a floor.
        assertTrue(
            "ToolsScreen.kt still applies a .weight(1f) modifier. Inside the M16 scroll host that " +
                "is a runtime failure, and it is what let each added card eat the trackpad's height.",
            // The leading dot matters: it pins this to an applied modifier, so the prose above
            // the trackpad in ToolsScreen.kt can still name weight(1f) as the thing removed.
            !source.contains(".weight(1f)"),
        )
    }

    private fun readToolsScreenSource(): String {
        val relative = "src/main/java/app/linc/android/ui/ToolsScreen.kt"
        // Gradle runs unit tests with the module directory as the working directory, but walk up
        // anyway so this fails on "the file moved", never on "the runner chose another CWD".
        var dir: File? = File(System.getProperty("user.dir") ?: ".").absoluteFile
        val tried = mutableListOf<String>()
        while (dir != null) {
            val candidate = File(dir, relative)
            tried += candidate.path
            if (candidate.isFile) return candidate.readText()
            val nested = File(dir, "app/$relative")
            tried += nested.path
            if (nested.isFile) return nested.readText()
            dir = dir.parentFile
        }
        throw AssertionError(
            "Could not find ToolsScreen.kt — this check cannot run, which is a FAILURE, not a " +
                "skip. Looked at:\n" + tried.joinToString("\n"),
        )
    }
}
