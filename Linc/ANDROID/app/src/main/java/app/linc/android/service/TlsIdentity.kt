package app.linc.android.service

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509TrustManager

/**
 * The phone's TLS identity for the Direct TLS transport (PROTOCOL.md v9, D-022).
 * An Android-KeyStore EC key is generated once; the self-signed certificate the
 * KeyStore attaches to it is what the desktop pins (exact bytes — no chains, no
 * hostnames). The desktop's certificate is pinned the same way via TransportStore.
 */
object TlsIdentity {

    private const val ALIAS = "linc-tls"
    private const val ANDROID_KEY_STORE = "AndroidKeyStore"

    /** This phone's certificate, base64 DER — the value sent in the `tls.exchange` reply. */
    fun certificateBase64(): String? = runCatching {
        Base64.encodeToString(ensureCertificate().encoded, Base64.NO_WRAP)
    }.getOrNull()

    /**
     * The same certificate as raw DER bytes. The BLE presence beacon (M04) derives its rotating
     * id from these, which is what makes the beacon recognisable only to a PC that has pinned
     * this exact certificate.
     */
    fun certificateDer(): ByteArray? = runCatching { ensureCertificate().encoded }.getOrNull()

    /** Opens a mutual-TLS connection to [host]:[port], verifying the pinned desktop cert. */
    fun connect(host: String, port: Int, pinnedDesktopCertB64: String, timeoutMs: Int): SSLSocket {
        ensureCertificate()
        val keyStore = KeyStore.getInstance(ANDROID_KEY_STORE).apply { load(null) }
        val keyManagers = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm())
            .apply { init(keyStore, null) }.keyManagers

        val pinned = decodeCert(pinnedDesktopCertB64)
        val trustPinned = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) =
                throw java.security.cert.CertificateException("client role only")
            override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) {
                if (chain.isEmpty() || !chain[0].encoded.contentEquals(pinned.encoded)) {
                    throw java.security.cert.CertificateException("server is not the paired Linc desktop")
                }
            }
            override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf(pinned)
        }

        val context = SSLContext.getInstance("TLS")
        context.init(keyManagers, arrayOf(trustPinned), null)
        val socket = context.socketFactory.createSocket() as SSLSocket
        socket.connect(java.net.InetSocketAddress(host, port), timeoutMs)
        socket.soTimeout = timeoutMs
        socket.startHandshake() // throws unless the desktop presented the pinned cert
        socket.soTimeout = 0
        return socket
    }

    private fun ensureCertificate(): X509Certificate {
        val keyStore = KeyStore.getInstance(ANDROID_KEY_STORE).apply { load(null) }
        (keyStore.getCertificate(ALIAS) as? X509Certificate)?.let { return it }
        val generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, ANDROID_KEY_STORE)
        generator.initialize(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY)
                // DIGEST_NONE lets the TLS stack do its own hashing during the handshake.
                .setDigests(KeyProperties.DIGEST_NONE, KeyProperties.DIGEST_SHA256,
                    KeyProperties.DIGEST_SHA384, KeyProperties.DIGEST_SHA512)
                .build())
        generator.generateKeyPair()
        return keyStore.getCertificate(ALIAS) as X509Certificate
    }

    private fun decodeCert(base64: String): X509Certificate =
        CertificateFactory.getInstance("X.509")
            .generateCertificate(Base64.decode(base64, Base64.NO_WRAP).inputStream()) as X509Certificate
}
