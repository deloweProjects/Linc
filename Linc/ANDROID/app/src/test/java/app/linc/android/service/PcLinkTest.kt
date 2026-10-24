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
}
