package app.linc.android.service

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** The phone shows exactly the link state the PC sends (v32 `link.state`), or nothing. */
class PcLinkTest {
    private fun from(route: String?, adb: Boolean?, via: String?) = CompanionStateHolder.PcLink.from(route, adb, via)

    @Test fun usbWithAdbOverTheCable() {
        val link = from("usb", true, "cable")!!
        assertEquals("USB cable", link.label)
        assertEquals(true, link.adb)
        assertEquals("cable", link.adbVia)
    }

    @Test fun directAndWirelessDebuggingBothReadWiFi() {
        assertEquals("Wi-Fi", from("direct", false, null)!!.label)
        assertEquals("Wi-Fi", from("wifi", true, "wireless")!!.label)
    }

    @Test fun anythingElseIsNotShown() {
        assertNull(from("bluetooth", true, null))
        assertNull(from(null, true, null))
        assertNull(from("usb", null, null))
    }

    @Test fun noAdbMeansNoAdbRoute() {
        assertNull(from("direct", false, "wireless")!!.adbVia)
        assertNull(from("usb", true, "pigeon")!!.adbVia)
    }

    @Test fun clearedStateShowsNothing() {
        CompanionStateHolder.setPcLink(from("usb", true, "cable"))
        CompanionStateHolder.setPcLink(null)
        assertNull(CompanionStateHolder.pcLink.value)
    }

    @Test fun turningOnOnlyWhileAdbIsDown() {
        assertEquals(true, CompanionStateHolder.PcLink.from("direct", false, null, true)!!.arming)
        assertEquals(false, CompanionStateHolder.PcLink.from("usb", true, "cable", true)!!.arming)
        assertEquals(false, from("direct", false, null)!!.arming)
    }

    @Test fun subnetIsTheNetworkNotThePhone() {
        val a = app.linc.android.service.link.LinkBrain.subnetOf(byteArrayOf(10, 5, 48, 24), 24)
        val b = app.linc.android.service.link.LinkBrain.subnetOf(byteArrayOf(10, 5, 48, 77), 24)
        assertEquals("10.5.48.0/24", a)
        assertEquals(a, b) // a new DHCP lease is the same network
        assertEquals("192.168.0.0/16", app.linc.android.service.link.LinkBrain.subnetOf(byteArrayOf(192.toByte(), 168.toByte(), 7, 9), 16))
    }
}
