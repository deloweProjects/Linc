package app.linc.android.service.link

import java.nio.ByteBuffer
import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/** A parsed LincLink UDP packet (see [LinkBeacon]). */
class BeaconPacket(val role: Byte, val tag: ByteArray, val nonce: ByteArray, val port: Int, val flags: Byte)

/**
 * The LAN "I'm here" signal (v32, linclink.md §6), byte-identical to the PC's `LinkBeacon.cs`.
 * The PC broadcasts a probe for each phone it holds a link secret for; this phone answers a probe
 * from a PC it knows, unicast to the probe's source, and announces itself by broadcast on its own
 * strong events (boot, Wi-Fi joined).
 *
 * `"LK" ‖ version ‖ role ‖ tag (8) ‖ nonce (8) ‖ port (2, big-endian) ‖ flags`, 23 bytes, with
 * `tag = HMAC-SHA256(linkSecret, "linc-beacon" ‖ role ‖ slot (8, big-endian) ‖ nonce)[..8]` and
 * `slot = unixSeconds / 30`, matched at ±1. A reply carries the probe's nonce.
 */
object LinkBeacon {
    const val PORT = 46003
    const val SLOT_SECONDS = 30L
    const val VERSION: Byte = 1
    const val LENGTH = 23

    const val ROLE_PROBE = 'P'.code.toByte()
    const val ROLE_REPLY = 'R'.code.toByte()
    const val ROLE_ANNOUNCE = 'A'.code.toByte()

    private val LABEL = "linc-beacon".toByteArray(Charsets.US_ASCII)
    private val random = SecureRandom()

    fun slot(unixSeconds: Long): Long = unixSeconds / SLOT_SECONDS

    fun tag(secret: ByteArray, role: Byte, slot: Long, nonce: ByteArray): ByteArray {
        val message = ByteBuffer.allocate(LABEL.size + 1 + 8 + nonce.size)
            .put(LABEL).put(role).putLong(slot).put(nonce).array()
        val mac = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(secret, "HmacSHA256")) }
        return mac.doFinal(message).copyOf(8)
    }

    fun build(role: Byte, secret: ByteArray, unixSeconds: Long, nonce: ByteArray, port: Int, flags: Byte = 0): ByteArray {
        require(nonce.size == 8) { "A beacon nonce is 8 bytes." }
        return ByteBuffer.allocate(LENGTH)
            .put('L'.code.toByte()).put('K'.code.toByte()).put(VERSION).put(role)
            .put(tag(secret, role, slot(unixSeconds), nonce))
            .put(nonce)
            .putShort(port.toShort())
            .put(flags)
            .array()
    }

    fun newNonce(): ByteArray = ByteArray(8).also { random.nextBytes(it) }

    /** A well-formed packet of this version, or null: anything else on the port is ignored. */
    fun parse(data: ByteArray, length: Int = data.size): BeaconPacket? {
        if (length < LENGTH || data[0] != 'L'.code.toByte() || data[1] != 'K'.code.toByte() || data[2] != VERSION) return null
        val role = data[3]
        if (role != ROLE_PROBE && role != ROLE_REPLY && role != ROLE_ANNOUNCE) return null
        val port = ((data[20].toInt() and 0xFF) shl 8) or (data[21].toInt() and 0xFF)
        return BeaconPacket(role, data.copyOfRange(4, 12), data.copyOfRange(12, 20), port, data[22])
    }

    fun matches(packet: BeaconPacket, secret: ByteArray, unixSeconds: Long): Boolean {
        val now = slot(unixSeconds)
        return (now - 1..now + 1).any { MessageDigest.isEqual(tag(secret, packet.role, it, packet.nonce), packet.tag) }
    }
}
