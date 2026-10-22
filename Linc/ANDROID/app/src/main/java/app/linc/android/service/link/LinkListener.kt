package app.linc.android.service.link

import android.content.Context
import android.util.Base64
import app.linc.android.service.LogLevel
import app.linc.android.service.LogStore
import app.linc.android.service.SocketServer
import app.linc.android.service.TlsIdentity
import app.linc.android.service.TransportStore
import java.io.IOException
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.SSLSocket
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/**
 * The phone's LincLink listener (v32, port 46002, linclink.md §3): the PC dials the phone here,
 * which no Windows firewall can block (it is outbound for the PC). Mutual TLS: only a PC this
 * phone has paired with — by its exact certificate — gets past the handshake, plus, while a QR
 * code is being paired, the PC in that code. A control connection or a typed channel is then
 * served exactly as a link the phone dialled.
 */
class LinkListener(
    private val context: Context,
    private val scope: CoroutineScope,
    private val server: SocketServer,
) {
    private var job: Job? = null

    @Volatile
    private var socket: SSLServerSocket? = null

    val running: Boolean get() = socket != null

    fun start() {
        if (job != null) return
        job = scope.launch(Dispatchers.IO) {
            var failures = 0
            while (isActive) {
                val listening = try {
                    TlsIdentity.serverSocket(LinkBrain.LISTEN_PORT) { cert -> trusted(cert.encoded) }
                } catch (e: Exception) {
                    if (failures++ == 0) LogStore.log(LogLevel.WARN, "LincLink: couldn't listen for the PC on port ${LinkBrain.LISTEN_PORT} (${e.javaClass.simpleName}); retrying")
                    delay(minOf(30_000L, 2_000L * failures))
                    continue
                }
                socket = listening
                failures = 0
                LogStore.log(LogLevel.INFO, "LincLink: waiting for the PC to dial this phone (port ${LinkBrain.LISTEN_PORT})")
                try {
                    while (isActive) {
                        val client = listening.accept() as SSLSocket
                        launch(Dispatchers.IO) { handle(client) }
                    }
                } catch (_: IOException) {
                    // closed by stop(), or the network went away: reopen unless stopped
                } finally {
                    socket = null
                    runCatching { listening.close() }
                }
            }
        }
    }

    fun stop() {
        job?.cancel()
        job = null
        runCatching { socket?.close() }
        socket = null
    }

    private fun trusted(der: ByteArray): Boolean {
        val b64 = Base64.encodeToString(der, Base64.NO_WRAP)
        return TransportStore.pcs(context).any { it.cert == b64 } || LinkPairingClient.pendingHash() == LinkPairing.certHash(der)
    }

    private fun handle(client: SSLSocket) {
        try {
            runCatching { client.tcpNoDelay = true }
            client.soTimeout = 5_000
            client.startHandshake() // the trust manager above refuses everyone else
            client.soTimeout = 0
            val der = client.session.peerCertificates.firstOrNull()?.encoded ?: return
            val b64 = Base64.encodeToString(der, Base64.NO_WRAP)
            if (TransportStore.pcs(context).none { it.cert == b64 }) {
                // Only the PC in the code being paired gets here (see trusted).
                LinkPairingClient.completeIncoming(context, client, der)
                return
            }
            val why = server.serveListened(client) // blocks until the PC is done with it
            if (why != null) LogStore.log(LogLevel.INFO, "A link the PC dialled to this phone ended ($why)")
        } catch (_: Exception) {
            // a stranger, or a PC that hung up mid-handshake: nothing to say
        } finally {
            runCatching { client.close() }
        }
    }
}
