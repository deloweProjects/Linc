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
        // The icons are hand-inlined path data; a typo would only blow up on first draw. Every
        // ImageVector getter on LincSymbols is checked, so a newly added icon can't be missed.
        val getters = LincSymbols::class.java.methods.filter {
            it.parameterCount == 0 && it.returnType == androidx.compose.ui.graphics.vector.ImageVector::class.java
        }
        assertTrue("found ${getters.size} icons", getters.size > 90)
        getters.forEach { getter ->
            val icon = getter.invoke(LincSymbols) as androidx.compose.ui.graphics.vector.ImageVector
            assertTrue(icon.name, icon.root.size > 0)
        }
    }
}
