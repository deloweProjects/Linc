package app.linc.android.service

import android.content.pm.ApplicationInfo
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pure-function coverage for the v16 app inventory (M6c / D-058). [AppInventory.launchable]
 * needs a real [android.content.pm.PackageManager], which the unit-test classpath cannot fake
 * (no Robolectric here — only JUnit), so the mapping and payload shaping live in pure
 * functions and are asserted directly, exactly as `DisplayControlTest` does for v15.
 *
 * The "no launcher intent → excluded" rule is a property of the launcher-intent *query*, not
 * of the mapping, so it is asserted the only way it can be without a PackageManager: the
 * payload built from a list that never contained the app doesn't mention it. The query itself
 * is a single `queryIntentActivities(ACTION_MAIN + CATEGORY_LAUNCHER)` call — see the note in
 * the harness report about what could not be machine-verified off-device.
 */
class AppInventoryTest {

    private fun appsOf(payload: JsonObject) = payload["apps"]!!.jsonArray

    @Test
    fun `a launchable app maps to all four fields`() {
        val entry = AppInventory.mapEntry("com.example.notes", "Notes", "2.1.0", 0)!!
        assertEquals("com.example.notes", entry.packageName)
        assertEquals("Notes", entry.label)
        assertEquals("2.1.0", entry.versionName)
        assertFalse(entry.system)

        val app = appsOf(AppInventory.toPayload(listOf(entry))).single().jsonObject
        assertEquals("com.example.notes", app["package"]!!.jsonPrimitive.content)
        assertEquals("Notes", app["label"]!!.jsonPrimitive.content)
        assertEquals("2.1.0", app["versionName"]!!.jsonPrimitive.content)
        assertEquals(false, app["system"]!!.jsonPrimitive.content.toBoolean())
    }

    @Test
    fun `a missing or empty versionName is tolerated and omitted from the wire`() {
        val absent = AppInventory.mapEntry("com.example.a", "A", null, 0)!!
        val empty = AppInventory.mapEntry("com.example.b", "B", "   ", 0)!!
        assertNull(absent.versionName)
        assertNull(empty.versionName)

        // Optional and cosmetic: better absent than present as null or "" (PROTOCOL.md v16).
        for (app in appsOf(AppInventory.toPayload(listOf(absent, empty)))) {
            assertFalse(app.jsonObject.containsKey("versionName"))
            assertTrue(app.jsonObject.containsKey("label"))
        }
    }

    @Test
    fun `the system flag is derived from FLAG_SYSTEM and system apps are included`() {
        val user = AppInventory.mapEntry("com.example.user", "User App", "1.0", 0)!!
        val system = AppInventory.mapEntry(
            "com.android.settings", "Settings", "14", ApplicationInfo.FLAG_SYSTEM)!!
        val updatedSystem = AppInventory.mapEntry(
            "com.android.camera", "Camera", "3.0",
            ApplicationInfo.FLAG_SYSTEM or ApplicationInfo.FLAG_UPDATED_SYSTEM_APP)!!
        // An unrelated flag must not be mistaken for FLAG_SYSTEM.
        val debuggable = AppInventory.mapEntry(
            "com.example.dbg", "Debug Build", "1.0", ApplicationInfo.FLAG_DEBUGGABLE)!!

        assertFalse(user.system)
        assertTrue(system.system)
        assertTrue(updatedSystem.system)
        assertFalse(debuggable.system)

        // Flagged, never filtered — Settings and Camera are exactly what users reach for (D-058).
        val packages = appsOf(AppInventory.toPayload(listOf(user, system, updatedSystem)))
            .map { it.jsonObject["package"]!!.jsonPrimitive.content }
        assertEquals(listOf("com.example.user", "com.android.settings", "com.android.camera"), packages)
    }

    @Test
    fun `an app with no usable label is excluded rather than named from its package id`() {
        // The desktop must never derive a name from the package id (PROTOCOL.md v16), so an
        // entry we cannot label honestly is dropped instead.
        assertNull(AppInventory.mapEntry("com.example.nolabel", null, "1.0", 0))
        assertNull(AppInventory.mapEntry("com.example.blank", "   ", "1.0", 0))
        assertNull(AppInventory.mapEntry("", "Something", "1.0", 0))
        assertNull(AppInventory.mapEntry(null, "Something", "1.0", 0))
    }

    @Test
    fun `an app with no launcher intent never reaches the payload`() {
        // launchable() only ever sees CATEGORY_LAUNCHER resolutions, so a background-only
        // package is absent from the list it maps — and therefore from the reply.
        val launchable = listOf(AppInventory.mapEntry("com.example.notes", "Notes", "1.0", 0)!!)
        val packages = appsOf(AppInventory.toPayload(launchable))
            .map { it.jsonObject["package"]!!.jsonPrimitive.content }
        assertEquals(listOf("com.example.notes"), packages)
        assertFalse(packages.contains("com.example.headlessservice"))
    }

    @Test
    fun `an empty inventory is a well-formed empty list, not a missing field`() {
        val payload = AppInventory.toPayload(emptyList())
        assertTrue(payload.containsKey("apps"))
        assertEquals(0, appsOf(payload).size)
    }
}
