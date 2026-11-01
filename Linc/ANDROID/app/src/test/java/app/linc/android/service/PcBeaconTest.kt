package app.linc.android.service

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class PcBeaconTest {
    private val cert = ByteArray(64) { it.toByte() }
    private val slot = 5_900_000L

    private fun pcPayload(slot: Long, flags: Int, cert: ByteArray = this.cert) =
        BlePresenceAdvertiser.PC_MAGIC + BlePresenceAdvertiser.rotatingId(cert, slot) + byteArrayOf(flags.toByte())

    @Test fun the_paired_pcs_beacon_gives_its_status() =
        assertEquals(BlePresenceAdvertiser.PC_HOTSPOT_ON or BlePresenceAdvertiser.PC_LOOKING,
            BlePresenceAdvertiser.matchPc(pcPayload(slot, 6), cert, slot))

    @Test fun a_neighbouring_slot_still_matches() =
        assertEquals(1, BlePresenceAdvertiser.matchPc(pcPayload(slot - 1, 1), cert, slot))

    @Test fun another_pairing_is_ignored() =
        assertNull(BlePresenceAdvertiser.matchPc(pcPayload(slot, 1, ByteArray(64) { 7 }), cert, slot))

    @Test fun the_phones_own_beacon_is_not_the_pcs() =
        assertNull(BlePresenceAdvertiser.matchPc(BlePresenceAdvertiser.MAGIC + BlePresenceAdvertiser.rotatingId(cert, slot), cert, slot))

    @Test fun an_old_slot_is_ignored() =
        assertNull(BlePresenceAdvertiser.matchPc(pcPayload(slot - 3, 1), cert, slot))

    @Test fun no_status_byte_reads_as_zero() =
        assertEquals(0, BlePresenceAdvertiser.matchPc(BlePresenceAdvertiser.PC_MAGIC + BlePresenceAdvertiser.rotatingId(cert, slot), cert, slot))

    @Test fun the_pc_status_reads_plainly() =
        assertEquals("has no link, its hotspot is on", BlePresenceAdvertiser.describePc(BlePresenceAdvertiser.PC_HOTSPOT_ON))
}
