package app.linc.android.ui

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Guards for the PC Remote bubble and panel (D-069). The 2026-10-10 "tap the bubble and Linc
 * crashes" was an ANR: the bubble's elevation shadow followed its concave cookie outline, and
 * Skia's concave shadow tessellator kept the render thread busy for seconds per frame.
 */
class PcRemoteOverlayTest {

    @Test
    fun noShadowFollowsAConcaveExpressiveShape() {
        // Expressive shapes are for clipping, never for shadows or elevation.
        val offenders = java.io.File("src/main/java").walkTopDown()
            .filter { it.extension == "kt" }
            .flatMap { file ->
                file.readLines().mapIndexedNotNull { i, line ->
                    val code = line.substringBefore("//")
                    val shadowed = "shadow" in code || "Elevation" in code
                    if ("MaterialShapes" in code && shadowed) "${file.name}:${i + 1}" else null
                }
            }
            .toList()
        assertEquals(emptyList<String>(), offenders)
    }

    @Test
    fun theBubbleShadowIsConvex() {
        val source = java.io.File("src/main/java/app/linc/android/ui/PcRemoteOverlay.kt").readText()
        assertTrue(".shadow(8.dp, CircleShape)" in source)
    }

    @Test
    fun everySymbolParses() {
        // The icons are hand-inlined path data; a typo would only blow up on first draw.
        listOf(
            LincSymbols.WifiGood, LincSymbols.WifiFair, LincSymbols.WifiBad, LincSymbols.Mouse,
            LincSymbols.TouchApp, LincSymbols.Keyboard, LincSymbols.KeyboardHide, LincSymbols.Smartphone,
            LincSymbols.VolumeUp, LincSymbols.VolumeOff, LincSymbols.Display, LincSymbols.Tune,
            LincSymbols.FitScreen, LincSymbols.TaskView, LincSymbols.Home, LincSymbols.Power,
            LincSymbols.Mic, LincSymbols.VoiceAccess, LincSymbols.Close, LincSymbols.Listening,
            LincSymbols.KeyboardAlt,
        ).forEach { assertTrue(it.name, it.root.size > 0) }
    }
}
