package app.linc.android.service

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class PcHotspotJoinTest {
    @Test fun first_network_is_new() = assertTrue(PcHotspotJoin.isNew(null, "DESKTOP-1 9758", "abcdefgh"))

    @Test fun the_same_network_again_is_not() = assertFalse(PcHotspotJoin.isNew("A" to "abcdefgh", "A", "abcdefgh"))

    @Test fun a_changed_password_is_new() = assertTrue(PcHotspotJoin.isNew("A" to "abcdefgh", "A", "zyxwvuts"))

    @Test fun nonsense_is_ignored() {
        assertFalse(PcHotspotJoin.isNew(null, "", "abcdefgh"))
        assertFalse(PcHotspotJoin.isNew(null, "A", "short"))
    }
}
