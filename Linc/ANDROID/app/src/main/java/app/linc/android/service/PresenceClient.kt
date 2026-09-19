package app.linc.android.service

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import app.linc.android.protocol.Framing
import app.linc.android.service.CompanionStateHolder.ServiceState
import java.io.BufferedInputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/**
 * Standing presence over Direct TLS (PIPELINE.md, D-014/D-022): while the companion
 * service runs, the "Background connection" setting is on, and no desktop is
 * connected, watch mDNS for the desktop's `_linc._tcp` advert (plus the adb-reverse
 * loopback port when one was exchanged) and dial out with mutual TLS. A successful
 * dial is handed to [SocketServer.serveExternal] — the same protocol code serves it.
 * The scan interval widens while the PC is absent (battery).
 */
class PresenceClient(
    private val context: Context,
    private val scope: CoroutineScope,
    private val server: SocketServer,
) {
    private var loopJob: Job? = null
    private var discovering = false

    @Volatile
    private var advertised: List<Pair<String, Int>> = emptyList() // candidate host:port from mDNS

    /** Where the current control connection was dialed to — channel dial-backs reuse it. */
    @Volatile
    private var activeEndpoint: Pair<String, Int>? = null

    private val discoveryListener = object : NsdManager.DiscoveryListener {
        override fun onDiscoveryStarted(serviceType: String) {}
        override fun onDiscoveryStopped(serviceType: String) {}
        override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {}
        override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {}
        override fun onServiceLost(info: NsdServiceInfo) {
            advertised = emptyList()
        }
        override fun onServiceFound(info: NsdServiceInfo) {
            if (info.serviceType.startsWith(SERVICE_TYPE)) {
                runCatching {
                    @Suppress("DEPRECATION")
                    nsd.resolveService(info, object : NsdManager.ResolveListener {
                        override fun onResolveFailed(info: NsdServiceInfo, errorCode: Int) {}
                        override fun onServiceResolved(info: NsdServiceInfo) {
                            val port = info.port
                            val hosts = linkedSetOf<String>()
                            info.host?.hostAddress?.let { hosts += it }
                            // The desktop lists every real LAN address in a TXT record; the
                            // single resolved host is often an unreachable virtual/link-local
                            // IP on multi-adapter PCs (D-022).
                            info.attributes["addrs"]?.let { bytes ->
                                String(bytes).split(",").forEach { if (it.isNotBlank()) hosts += it.trim() }
                            }
                            advertised = hosts.map { it to port }
                        }
                    })
                }
            }
        }
    }

    private val nsd: NsdManager by lazy { context.getSystemService(NsdManager::class.java) }

    fun start() {
        if (loopJob != null) return
        runCatching {
            nsd.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, discoveryListener)
            discovering = true
        }
        loopJob = scope.launch(Dispatchers.IO) {
            var idleDelaysMs = 3_000L
            while (isActive) {
                if (shouldDial()) {
                    val connected = dialControl()
                    idleDelaysMs = if (connected) 3_000L else (idleDelaysMs * 2).coerceAtMost(30_000L)
                } else {
                    idleDelaysMs = 3_000L
                }
                delay(idleDelaysMs)
            }
        }
    }

    fun stop() {
        loopJob?.cancel()
        loopJob = null
        if (discovering) {
            runCatching { nsd.stopServiceDiscovery(discoveryListener) }
            discovering = false
        }
    }

    private fun shouldDial(): Boolean =
        TransportStore.presenceEnabled(context) &&
            TransportStore.desktopCert(context) != null &&
            CompanionStateHolder.state.value is ServiceState.Listening

    /** Tries the advertised endpoint, then the adb-reverse loopback. Blocks while connected. */
    private fun dialControl(): Boolean {
        val cert = TransportStore.desktopCert(context) ?: return false
        for (endpoint in candidateEndpoints()) {
            val socket = runCatching {
                TlsIdentity.connect(endpoint.first, endpoint.second, cert, DIAL_TIMEOUT_MS)
            }.getOrNull() ?: continue
            activeEndpoint = endpoint
            LogStore.log(LogLevel.INFO, "Connected to the PC directly (no ADB)")
            server.serveExternal(socket) // blocks until the desktop disconnects
            activeEndpoint = null
            return true
        }
        return false
    }

    private fun candidateEndpoints(): List<Pair<String, Int>> {
        val endpoints = mutableListOf<Pair<String, Int>>()
        // adb-reverse (USB cable) path first: loopback is always reachable when it exists.
        val reversePort = TransportStore.reversePort(context)
        if (reversePort > 0) {
            endpoints += "127.0.0.1" to reversePort
        }
        endpoints += advertised // every real LAN address the desktop advertised
        return endpoints.distinct().filter { it.second > 0 }
    }

    /**
     * v9 `channel.open`: dial a new TLS connection back and serve the channel on it.
     *
     * The desktop asks the phone to dial back for every channel it serves, whatever the control
     * link is. When control is itself Direct TLS, [activeEndpoint] is the obvious route; when
     * control is ADB (the common case now that Direct TLS re-exchanges on every connect),
     * activeEndpoint is null but the phone can still reach the desktop's TLS listener over the
     * USB reverse tunnel or an advertised LAN address. So fall through to the same candidate
     * list the control dialer uses.
     */
    fun openChannel(channel: Int, token: String): Boolean {
        val cert = TransportStore.desktopCert(context) ?: return false
        val endpoints = buildList {
            activeEndpoint?.let { add(it) }
            addAll(candidateEndpoints())
        }.distinct()

        val socket = endpoints.firstNotNullOfOrNull { endpoint ->
            runCatching { TlsIdentity.connect(endpoint.first, endpoint.second, cert, DIAL_TIMEOUT_MS) }
                .getOrNull()
        } ?: return false

        scope.launch(Dispatchers.IO) {
            try {
                val output = DataOutputStream(socket.getOutputStream())
                Framing.write(output, """{"channel":$channel,"sessionToken":"$token"}""")
                // Buffered: the video channel reads a 12-byte header per frame, and an
                // unbuffered DataInputStream turns that into a syscall per field.
                server.serveDialedChannel(
                    channel,
                    DataInputStream(BufferedInputStream(socket.getInputStream(), CHANNEL_BUFFER_BYTES)),
                    output,
                )
            } catch (_: Exception) {
                // Channel died; the desktop will re-open if it still needs it.
            } finally {
                runCatching { socket.close() }
            }
        }
        return true
    }

    private companion object {
        const val SERVICE_TYPE = "_linc._tcp."
        const val DIAL_TIMEOUT_MS = 4_000
        const val CHANNEL_BUFFER_BYTES = 64 * 1024
    }
}
