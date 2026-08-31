package app.linc.android.service

import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.InetAddress

/**
 * v18 (M13b) phone-side rules. Everything asserted here is called directly — no re-implementation
 * of the rule inside the test, which is the whole reason AdbLinkRules.kt is free of android.*.
 */
class AdbLinkRulesTest {

    // --- method 1: system properties, in cost order -------------------------------------

    @Test
    fun `pickPort prefers the TLS port over the tcpip ones`() {
        val detected = AdbPortRules.pickPort(
            mapOf(
                "service.adb.tls.port" to "37421",
                "service.adb.tcp.port" to "5555",
                "persist.adb.tcp.port" to "5555",
            ),
        )
        assertNotNull(detected)
        assertEquals(37421, detected!!.port)
        assertEquals(AdbMode.TLS, detected.mode)
    }

    @Test
    fun `pickPort falls through to the tcpip port when TLS is absent`() {
        val detected = AdbPortRules.pickPort(
            mapOf("service.adb.tls.port" to null, "service.adb.tcp.port" to "5555"),
        )
        assertNotNull(detected)
        assertEquals(5555, detected!!.port)
        assertEquals(AdbMode.TCPIP, detected.mode)
    }

    @Test
    fun `pickPort falls through to the persisted port last`() {
        val detected = AdbPortRules.pickPort(mapOf("persist.adb.tcp.port" to "5555"))
        assertNotNull(detected)
        assertEquals(5555, detected!!.port)
        assertEquals(AdbMode.TCPIP, detected.mode)
    }

    @Test
    fun `an empty or unset property set means try the next method, not adbd is down`() {
        // The distinction matters: these properties are not populated on every OEM build, so a
        // miss here must never be reported as "wireless debugging is off".
        assertNull(AdbPortRules.pickPort(emptyMap()))
        assertNull(AdbPortRules.pickPort(mapOf("service.adb.tls.port" to "")))
        assertNull(AdbPortRules.pickPort(mapOf("service.adb.tls.port" to null)))
    }

    @Test
    fun `adbd's not-listening sentinels are not ports`() {
        assertNull(AdbPortRules.parsePort("-1"))
        assertNull(AdbPortRules.parsePort("0"))
        assertNull(AdbPortRules.parsePort("65536"))
        assertNull(AdbPortRules.parsePort("not a number"))
        assertNull(AdbPortRules.parsePort(null))
        assertEquals(5555, AdbPortRules.parsePort(" 5555 "))
    }

    // --- method 2: NSD self-discovery ----------------------------------------------------

    @Test
    fun `service types map to the right mode`() {
        assertEquals(AdbMode.TLS, AdbPortRules.modeForServiceType("_adb-tls-connect._tcp"))
        assertEquals(AdbMode.TCPIP, AdbPortRules.modeForServiceType("_adb._tcp"))
        assertEquals(AdbMode.PAIRING, AdbPortRules.modeForServiceType("_adb-tls-pairing._tcp"))
        assertNull(AdbPortRules.modeForServiceType("_http._tcp"))
        assertNull(AdbPortRules.modeForServiceType(null))
        // NSD hands back a trailing dot on some builds.
        assertEquals(AdbMode.TLS, AdbPortRules.modeForServiceType("_adb-tls-connect._tcp."))
    }

    @Test
    fun `the pairing service is never announced as a connectable port`() {
        // Pairing being open means the desktop is NOT paired. Announcing that port as
        // connectable buys an `unauthorized` and a retry loop that can never succeed.
        assertFalse(AdbPortRules.isConnectableMode(AdbMode.PAIRING))
        assertTrue(AdbPortRules.isConnectableMode(AdbMode.TLS))
        assertTrue(AdbPortRules.isConnectableMode(AdbMode.TCPIP))
        assertFalse(AdbPortRules.isConnectableMode(null))
    }

    @Test
    fun `a resolved service is only ours when it matches one of our own addresses`() {
        val own = listOf("192.168.43.100", "fe80::1122:3344:5566:7788")
        assertTrue(AdbPortRules.isOwnAddress("192.168.43.100", own))
        // A second phone on the same hotspot publishes the same service type.
        assertFalse(AdbPortRules.isOwnAddress("192.168.43.55", own))
        assertFalse(AdbPortRules.isOwnAddress(null, own))
        assertFalse(AdbPortRules.isOwnAddress("192.168.43.100", emptyList()))
    }

    @Test
    fun `address comparison ignores the IPv6 zone and the leading slash InetAddress prints`() {
        val own = listOf("fe80::1122:3344:5566:7788%wlan0")
        assertTrue(AdbPortRules.isOwnAddress("fe80::1122:3344:5566:7788", own))
        assertTrue(AdbPortRules.isOwnAddress("/192.168.43.100", listOf("192.168.43.100")))
    }

    // --- the addresses we announce -------------------------------------------------------

    @Test
    fun `loopback and wildcard addresses are never announced`() {
        assertFalse(AdbAddresses.keep(InetAddress.getByName("127.0.0.1")))
        assertFalse(AdbAddresses.keep(InetAddress.getByName("::1")))
        assertFalse(AdbAddresses.keep(InetAddress.getByName("0.0.0.0")))
        assertTrue(AdbAddresses.keep(InetAddress.getByName("192.168.43.100")))
    }

    @Test
    fun `a mobile-data address is still announced — the DESKTOP owns the same-subnet filter`() {
        // Deliberate: the phone cannot tell which of its interfaces the desktop is on, and the
        // desktop can (PROTOCOL.md v18). Pre-filtering here would guess with less information.
        assertTrue(AdbAddresses.keep(InetAddress.getByName("10.116.24.9")))
    }

    // --- the generation counter ----------------------------------------------------------

    @Test
    fun `the generation counter is monotonic and never starts at zero`() {
        val first = AdbGeneration.next()
        val second = AdbGeneration.next()
        assertTrue("the first generation of a process must be >= 1, was $first", first >= 1)
        assertTrue("$second must be greater than $first", second > first)
        assertEquals(second, AdbGeneration.current())
    }

    // --- self-arming (M13c §2.2) ----------------------------------------------------------

    @Test
    fun `arming is refused outright without the one-time grant`() {
        // Checked BEFORE anything is written, so a phone that never got the grant reports
        // denied instead of letting a SecurityException escape into the control loop.
        assertEquals(AdbArmOutcome.DENIED, AdbArmRules.plan(hasPermission = false, currentlyEnabled = false))
        assertEquals(AdbArmOutcome.DENIED, AdbArmRules.plan(hasPermission = false, currentlyEnabled = true))
        assertFalse(AdbArmRules.writesSetting(AdbArmOutcome.DENIED))
    }

    @Test
    fun `already-on writes nothing`() {
        assertEquals(AdbArmOutcome.ALREADY_ON, AdbArmRules.plan(hasPermission = true, currentlyEnabled = true))
        assertFalse(AdbArmRules.writesSetting(AdbArmOutcome.ALREADY_ON))
    }

    @Test
    fun `with the grant and adbd off, the setting is written`() {
        assertEquals(AdbArmOutcome.ARMED, AdbArmRules.plan(hasPermission = true, currentlyEnabled = false))
        assertTrue(AdbArmRules.writesSetting(AdbArmOutcome.ARMED))
        assertEquals("adb_wifi_enabled", AdbArmRules.SETTING_NAME)
        assertEquals(1, AdbArmRules.ENABLED_VALUE)
    }

    @Test
    fun `there is no way to turn wireless debugging back off`() {
        // Arming is a visible change to the user's device; un-arming it behind their back would
        // be a change they neither asked for nor saw. No outcome writes anything but 1, and the
        // rules object exposes no disarm at all.
        for (outcome in AdbArmOutcome.values()) {
            if (AdbArmRules.writesSetting(outcome)) {
                assertEquals(AdbArmOutcome.ARMED, outcome)
            }
        }
        val names = AdbArmRules::class.java.methods.map { it.name }
        assertFalse(names.any { it.contains("disarm", ignoreCase = true) })
        assertFalse(names.any { it.contains("disable", ignoreCase = true) })
    }

    @Test
    fun `every arming outcome explains itself in plain language`() {
        for (outcome in AdbArmOutcome.values()) {
            val text = AdbArmRules.describe(outcome)
            assertTrue("$outcome has no description", text.isNotBlank())
            // "disable, don't hide": a refusal must say what the user can do instead.
            if (outcome == AdbArmOutcome.DENIED || outcome == AdbArmOutcome.UNAVAILABLE) {
                assertTrue("$outcome must point at Developer options", text.contains("Developer options"))
            }
        }
    }

    // --- payload shapes ------------------------------------------------------------------

    @Test
    fun `announce carries every field the desktop reads`() {
        val payload = AdbPayloads.announce(
            gen = 7,
            serial = "<your-device-serial>",
            mode = AdbMode.TCPIP,
            port = 5555,
            verified = true,
            addrs = listOf(AdbAddr("192.168.43.100", "wlan0", 24), AdbAddr("10.116.24.9", "rmnet0", 30)),
        )
        assertEquals(7, (payload["gen"] as JsonPrimitive).intOrNull)
        assertEquals("<your-device-serial>", (payload["serial"] as JsonPrimitive).contentOrNull)
        assertEquals("tcpip", (payload["mode"] as JsonPrimitive).contentOrNull)
        assertEquals(5555, (payload["port"] as JsonPrimitive).intOrNull)
        assertEquals(true, (payload["verified"] as JsonPrimitive).booleanOrNull)

        val addrs = payload["addrs"] as JsonArray
        assertEquals(2, addrs.size)
        val first = addrs[0].jsonObject
        assertEquals("192.168.43.100", (first["ip"] as JsonPrimitive).contentOrNull)
        assertEquals("wlan0", (first["iface"] as JsonPrimitive).contentOrNull)
        assertEquals(24, (first["prefix"] as JsonPrimitive).intOrNull)
    }

    @Test
    fun `verified false is emitted honestly rather than omitted`() {
        // The desktop refuses to act on verified:false; dropping the field would make an
        // unverified announcement indistinguishable from a verified one on a lenient parser.
        val payload: JsonObject = AdbPayloads.announce(1, "S", AdbMode.TLS, 37421, false, emptyList())
        assertEquals(false, (payload["verified"] as JsonPrimitive).booleanOrNull)
        assertEquals(0, (payload["addrs"] as JsonArray).size)
    }

    @Test
    fun `down carries the generation and a reason`() {
        val payload = AdbPayloads.down(9, "wireless debugging turned off")
        assertEquals(9, (payload["gen"] as JsonPrimitive).intOrNull)
        assertEquals("wireless debugging turned off", (payload["reason"] as JsonPrimitive).contentOrNull)
    }
}
