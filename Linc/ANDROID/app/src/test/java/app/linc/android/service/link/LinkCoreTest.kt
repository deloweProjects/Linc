package app.linc.android.service.link

import kotlin.random.Random
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** LincLink's pure core on the phone; the vectors are the ones tools/linclinksim pins for the PC. */
class LinkCoreTest {

    private fun hex(bytes: ByteArray) = bytes.joinToString("") { "%02x".format(it) }

    @Test
    fun `the beacon tag matches the PC's vector byte for byte`() {
        val secret = ByteArray(32) { (it + 1).toByte() }
        val nonce = ByteArray(8) { (0xA0 + it).toByte() }
        val now = 1_790_000_000L
        assertEquals(59_666_666L, LinkBeacon.slot(now))
        assertEquals("a59b781eddb9a453", hex(LinkBeacon.tag(secret, LinkBeacon.ROLE_REPLY, LinkBeacon.slot(now), nonce)))
    }

    @Test
    fun `a probe is recognised only by its own pair, within a slot`() {
        val secretA = ByteArray(32) { (it + 1).toByte() }
        val secretB = ByteArray(32) { (it + 101).toByte() }
        val now = 1_790_000_000L
        val nonce = LinkBeacon.newNonce()
        val probe = LinkBeacon.build(LinkBeacon.ROLE_PROBE, secretA, now, nonce, 46001)
        assertEquals(LinkBeacon.LENGTH, probe.size)
        val parsed = LinkBeacon.parse(probe)!!
        assertEquals(46001, parsed.port)
        assertArrayEquals(nonce, parsed.nonce)
        assertTrue(LinkBeacon.matches(parsed, secretA, now))
        assertTrue(LinkBeacon.matches(parsed, secretA, now + 30))
        assertFalse(LinkBeacon.matches(parsed, secretB, now))
        assertFalse(LinkBeacon.matches(parsed, secretA, now + 90))
        assertNull(LinkBeacon.parse("not a beacon at all, honestly".toByteArray()))
    }

    @Test
    fun `a high port survives the round trip`() {
        val packet = LinkBeacon.build(LinkBeacon.ROLE_REPLY, ByteArray(32), 0, ByteArray(8), 65000)
        assertEquals(65000, LinkBeacon.parse(packet)!!.port)
    }

    @Test
    fun `the pairing proof matches the PC's vector`() {
        val token = ByteArray(16) { it.toByte() }
        assertEquals(
            "cbce564e937d049f011335088802fa418666cca718547c7254329903f95d8978",
            hex(LinkPairing.proof(token, "phone".toByteArray(), "pc".toByteArray())),
        )
    }

    @Test
    fun `a PC's QR code parses, and anything else is refused`() {
        val token = ByteArray(16) { (it * 7).toByte() }
        val k = "ab".repeat(32)
        val uri = "linc://pair?v=1&k=$k&a=192.168.1.10%2C10.0.0.5&p=46001&t=${LinkPairing.toBase64Url(token)}&n=DESK%20%26%20co"
        val invite = LinkPairing.parse(uri)!!
        assertEquals(k, invite.pcCertSha256)
        assertEquals(listOf("192.168.1.10", "10.0.0.5"), invite.addresses)
        assertEquals(46001, invite.port)
        assertArrayEquals(token, invite.token)
        assertEquals("DESK & co", invite.name)
        assertNull(LinkPairing.parse("https://example.com"))
        assertNull(LinkPairing.parse(uri.replace("v=1", "v=2")))
        assertNull(LinkPairing.parse(uri.replace("&t=", "&t=AA")))
        assertNull(LinkPairing.parse(uri.replace("k=$k", "k=zz")))
    }

    @Test
    fun `a session replays exactly what the PC lacks, and drops replays it already handled`() {
        val s = LinkSession("abc")
        repeat(5) { val seq = s.stamp(); s.keep(seq, "f$seq") }
        s.onAck(3)
        assertEquals(2, s.pending)
        assertEquals(listOf("f4", "f5"), s.replayAfter(3))
        // The PC acked 3 on the old route, so a lower `recv` still resumes: it has 1..3.
        assertEquals(listOf("f4", "f5"), s.replayAfter(1))
        assertNull(s.replayAfter(6))

        val r = LinkSession("abc")
        assertEquals(FrameArrival.NEW, r.accept(1, 0))
        assertEquals(FrameArrival.DUPLICATE, r.accept(1, 0))
        assertEquals(FrameArrival.GAP, r.accept(4, 0))
        assertTrue(r.ackDue(LinkSession.ACK_EVERY_MS))
        r.ackSent(r.received)
        assertFalse(r.ackDue(100_000))
    }

    @Test
    fun `frames the PC acknowledged on the old route aren't counted as lost`() {
        val s = LinkSession("ack")
        repeat(10) { val seq = s.stamp(); s.keep(seq, "f$seq") }
        s.onAck(8) // trims 1..8: the PC proved it holds them
        assertEquals(9L, s.firstKept)
        // The resume's `recv` lags (its hello raced the ack): still resumable, replaying only 9 and 10.
        assertEquals(listOf("f9", "f10"), s.replayAfter(5))
    }

    @Test
    fun `the pairing check code matches the PC's vector`() {
        val token = ByteArray(16) { it.toByte() }
        assertEquals("764 252", LinkPairing.checkCode("ab".repeat(32), token))
        assertEquals(LinkPairing.checkCode("ab".repeat(32), token), LinkPairing.checkCode("AB".repeat(32).lowercase(), token))
        assertNotEquals("764 252", LinkPairing.checkCode("ab".repeat(32), ByteArray(16) { (it + 1).toByte() }))
    }

    @Test
    fun `a pairing code only makes the phone dial local network addresses`() {
        for (lan in listOf("10.0.0.7", "172.16.4.1", "172.31.255.254", "192.168.1.23", "169.254.10.2", "100.64.0.1", "100.127.3.4")) {
            assertTrue(lan, LinkPairing.isLanAddress(lan))
        }
        for (other in listOf("8.8.8.8", "172.15.0.1", "172.32.0.1", "192.169.0.1", "100.128.0.1", "1.2.3", "1.2.3.4.5",
            "256.1.1.1", "010.0.0.1", "pc.example.com", "fe80::1", "::ffff:10.0.0.1", "", " 10.0.0.1")) {
            assertFalse(other, LinkPairing.isLanAddress(other))
        }
    }

    @Test
    fun `an overflowed buffer can't resume a PC that missed the dropped frames`() {
        val s = LinkSession("x")
        repeat(LinkSession.MAX_FRAMES + 10) { val seq = s.stamp(); s.keep(seq, "y") }
        assertEquals(LinkSession.MAX_FRAMES, s.pending)
        assertNull(s.replayAfter(0))
        assertNotNull(s.replayAfter(10))
    }

    @Test
    fun `backoff stays between half a second and 30 s and isn't in lockstep`() {
        var wait = 0L
        val random = Random(7)
        repeat(40) {
            wait = LinkBrain.nextBackoffMs(wait, random)
            assertTrue(wait in LinkBrain.BACKOFF_BASE_MS..LinkBrain.BACKOFF_CAP_MS)
        }
        assertNotEquals(LinkBrain.nextBackoffMs(10_000, Random(1)), LinkBrain.nextBackoffMs(10_000, Random(2)))
    }

    @Test
    fun `network memory keeps one endpoint per PC per network and forgets dead ones`() {
        val home = LinkBrain.fingerprint("192.168.1.1", "192.168.1.0/24")
        val office = LinkBrain.fingerprint("10.0.0.1", "10.0.0.0/24")
        assertNotEquals(home, office)
        val memory = NetMemory()
        memory.remember(home, "pc1", "192.168.1.10:46001", 1)
        memory.remember(office, "pc1", "10.0.0.7:46001", 2)
        assertEquals("192.168.1.10:46001", memory.best(home, "pc1"))
        assertEquals("10.0.0.7:46001", memory.best(office, "pc1"))
        repeat(NetMemory.FORGET_AFTER) { memory.failed(home, "pc1", "192.168.1.10:46001") }
        assertNull(memory.best(home, "pc1"))
        val back = NetMemory.load(memory.save())
        assertEquals("10.0.0.7:46001", back.best(office, "pc1"))
        assertNull(NetMemory.load("garbage").best(office, "pc1"))
    }
}
