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
