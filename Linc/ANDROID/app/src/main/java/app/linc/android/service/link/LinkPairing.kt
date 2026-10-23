package app.linc.android.service.link

import java.net.URLDecoder
import java.security.MessageDigest
import java.util.Base64
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/** What the PC's pairing QR code carries (v32, linclink.md §2), as the PC's `LinkPairing.cs` writes it. */
data class PairingInvite(
    /** SHA-256 of the PC's TLS certificate (DER), lowercase hex: dial only a PC presenting exactly it. */
    val pcCertSha256: String,
    val addresses: List<String>,
    val port: Int,
    /** 16 one-use bytes proving this phone saw the PC's screen. */
    val token: ByteArray,
    val name: String,
)

/**
 * Pairing without ADB by QR code. The phone dials the PC, checks the PC's certificate hash against
 * the code, then sends `link.pair` with a proof: `HMAC-SHA256(token, "linc-pair" ‖ SHA-256(phone
 * cert) ‖ SHA-256(PC cert))`. Pure; the PC computes the same (tools/linclinksim pins the vector).
 */
object LinkPairing {
    const val PREFIX = "linc://pair?"
    const val TOKEN_BYTES = 16

    private val PROOF_LABEL = "linc-pair".toByteArray(Charsets.US_ASCII)
    private val CHECK_LABEL = "linc-check".toByteArray(Charsets.US_ASCII)

    /**
     * Six digits from the code's certificate hash and token, as the PC shows them under its QR code
     * ("123 456"). The phone's prompt shows them too: a link the owner didn't scan from their own
     * PC's screen shows digits their PC isn't showing.
     */
    fun checkCode(pcCertSha256Hex: String, token: ByteArray): String {
        val hash = pcCertSha256Hex.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
        val digest = MessageDigest.getInstance("SHA-256").digest(CHECK_LABEL + hash + token)
        val n = (((digest[0].toLong() and 0xFF) shl 24) or ((digest[1].toLong() and 0xFF) shl 16) or
            ((digest[2].toLong() and 0xFF) shl 8) or (digest[3].toLong() and 0xFF)) % 1_000_000
        val digits = n.toString().padStart(6, '0')
        return digits.substring(0, 3) + " " + digits.substring(3)
    }

    /**
     * True for an IPv4 literal on a local network (10/8, 172.16/12, 192.168/16, 169.254/16,
     * 100.64/10): the only addresses the phone dials from a pairing code, so a link can't point it
     * at a host on the internet.
     */
    fun isLanAddress(address: String): Boolean {
        val parts = address.split('.')
        if (parts.size != 4) return false
        val b = parts.map { p -> p.toIntOrNull()?.takeIf { it in 0..255 && p == it.toString() } ?: return false }
        return b[0] == 10 || (b[0] == 172 && b[1] in 16..31) || (b[0] == 192 && b[1] == 168) ||
            (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] in 64..127)
    }

    fun certHash(certDer: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(certDer).joinToString("") { "%02x".format(it) }

    fun parse(text: String?): PairingInvite? {
        if (text == null || !text.startsWith(PREFIX)) return null
        val fields = text.substring(PREFIX.length).split('&').mapNotNull { part ->
            val eq = part.indexOf('=')
            if (eq <= 0) null else part.substring(0, eq) to runCatching { decode(part.substring(eq + 1)) }.getOrNull()
        }.filter { it.second != null }.associate { it.first to it.second!! }
        if (fields["v"] != "1") return null
        val k = fields["k"]?.lowercase() ?: return null
        if (k.length != 64 || !k.all { it in '0'..'9' || it in 'a'..'f' }) return null
        val token = fields["t"]?.let { fromBase64Url(it) } ?: return null
        if (token.size != TOKEN_BYTES) return null
        val port = fields["p"]?.toIntOrNull()?.takeIf { it in 1..65535 } ?: return null
        val addresses = fields["a"].orEmpty().split(',').map { it.trim() }.filter { it.isNotEmpty() }
        return PairingInvite(k, addresses, port, token, fields["n"] ?: "PC")
    }

    fun proof(token: ByteArray, phoneCertDer: ByteArray, pcCertDer: ByteArray): ByteArray {
        val sha = { bytes: ByteArray -> MessageDigest.getInstance("SHA-256").digest(bytes) }
        val mac = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(token, "HmacSHA256")) }
        mac.update(PROOF_LABEL)
        mac.update(sha(phoneCertDer))
        mac.update(sha(pcCertDer))
        return mac.doFinal()
    }

    fun toBase64Url(bytes: ByteArray): String = Base64.getUrlEncoder().withoutPadding().encodeToString(bytes)

    fun fromBase64Url(text: String): ByteArray? = runCatching { Base64.getUrlDecoder().decode(text) }.getOrNull()

    // `+` stays a plus: the PC escapes with Uri.EscapeDataString (%20 for spaces), not form encoding.
    private fun decode(value: String): String = URLDecoder.decode(value.replace("+", "%2B"), "UTF-8")
}
