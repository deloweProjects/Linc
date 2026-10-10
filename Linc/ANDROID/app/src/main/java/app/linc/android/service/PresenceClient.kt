package app.linc.android.service

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import app.linc.android.protocol.Framing
import app.linc.android.service.CompanionStateHolder.ServiceState
import java.io.BufferedInputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.InetSocketAddress
import java.net.Socket
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

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

    /** Misses in a row for the remembered last-good address ([ReconnectPolicy.FORGET_AFTER]). */
    private var lastGoodFailures = 0

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
            var missStreak = 0
            var missesSincePoke: Int? = null
            while (isActive) {
                if (shouldDial()) {
                    val connected = dialControl()
                    if (connected) {
                        missStreak = 0
                        missesSincePoke = null
                    } else {
                        missStreak++
                        missesSincePoke = missesSincePoke?.plus(1)?.takeUnless { ReconnectPolicy.burstOver(it) }
                    }
                } else {
                    missStreak = 0
                    missesSincePoke = null
                }
                // Sleep out the backoff, but wake at once on a strong "the PC is right here"
                // signal (NFC tap, Wi-Fi joined, screen on, Linc opened, the PC link ending) and
                // then try a short burst before settling back to the normal pace (D-081).
                val wait = ReconnectPolicy.delayMs(missStreak, missesSincePoke)
                val poked = withTimeoutOrNull(wait) { ReconnectSignal.await() }
                if (poked != null && shouldDial()) {
                    LogStore.log(LogLevel.INFO, "Looking for the PC now ($poked)")
                    missesSincePoke = 0
                    restartDiscovery()
                }
            }
        }
    }

    /** A fresh mDNS browse: after a network change the old one can sit on a stale answer. */
    private fun restartDiscovery() {
        if (discovering) {
            runCatching { nsd.stopServiceDiscovery(discoveryListener) }
            discovering = false
        }
        runCatching {
            nsd.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, discoveryListener)
            discovering = true
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

    /**
     * Dials the USB reverse tunnel, then the address that worked last time, then whatever mDNS
     * advertised ([ReconnectPolicy.order]). Blocks while connected.
     */
    private fun dialControl(): Boolean {
        val cert = TransportStore.desktopCert(context) ?: return false
        val lastGood = ReconnectPolicy.parse(TransportStore.lastEndpoint(context))
        val candidates = candidateEndpoints(lastGood)
        if (candidates.isEmpty()) return false
        // Race plain TCP probes to every candidate and do TLS once, to the first that answers
        // (D-072's channel rule, now for control too): a dead remembered address or an
        // unreachable advertised one no longer costs a whole 4 s timeout each, in turn.
        val reachable = EndpointRace.first(
            candidates,
            timeoutMs = DIAL_TIMEOUT_MS + 500L,
            connect = { endpoint ->
                Socket().use { it.connect(InetSocketAddress(endpoint.first, endpoint.second), DIAL_TIMEOUT_MS) }
                endpoint
            },
            close = {},
        )
        if (lastGood != null && reachable != lastGood) {
            lastGoodFailures++
            if (ReconnectPolicy.shouldForget(lastGoodFailures)) {
                TransportStore.saveLastEndpoint(context, null)
                lastGoodFailures = 0
                LogStore.log(LogLevel.INFO, "Forgot ${ReconnectPolicy.format(lastGood)}: it failed ${ReconnectPolicy.FORGET_AFTER} times in a row")
            }
        }
        for (endpoint in listOfNotNull(reachable)) {
            val socket = runCatching {
                TlsIdentity.connect(endpoint.first, endpoint.second, cert, DIAL_TIMEOUT_MS)
            }.getOrNull() ?: continue
            if (endpoint.first != "127.0.0.1") {
                TransportStore.saveLastEndpoint(context, ReconnectPolicy.format(endpoint))
            }
            if (endpoint == lastGood) lastGoodFailures = 0
            activeEndpoint = endpoint
            LogStore.log(LogLevel.INFO, "Connected to the PC directly (no ADB) at ${endpoint.first}:${endpoint.second}")
            val since = android.os.SystemClock.elapsedRealtime()
            val why = server.serveExternal(socket) // blocks until the desktop disconnects
            activeEndpoint = null
            val lasted = android.os.SystemClock.elapsedRealtime() - since
            val stillLinked = CompanionStateHolder.state.value is ServiceState.Connected
            LogStore.log(
                if (stillLinked) LogLevel.INFO else LogLevel.WARN,
                "Direct connection to the PC ended after ${lasted / 1000} s (${why ?: "closed"})" +
                    if (stillLinked) " — the PC moved the link to another connection" else "",
            )
            if (why?.contains("connection abort", ignoreCase = true) == true && !ConnectionWatch.isBatteryExempt(context)) {
                // ECONNABORTED: Android destroyed the socket itself — what it does to an app it
                // has cut off in the background. The fix is the exemption, so say so plainly.
                LogStore.log(LogLevel.WARN, "Android cut Linc off from the network in the background. Allow " +
                    "\"Stay connected in the background\" (Home or Settings) so it stops doing that.")
            }
            // The PC turning a dial away at once (it's already linked over ADB, or busy) is not a
            // connection: count it as a miss so the redial backs off instead of every 3 s.
            return lasted >= 3_000
        }
        return false
    }

    private fun candidateEndpoints(lastGood: Pair<String, Int>? = null): List<Pair<String, Int>> {
        // adb-reverse (USB cable) path first: loopback is always reachable when it exists.
        val reversePort = TransportStore.reversePort(context)
        val reverse = if (reversePort > 0) "127.0.0.1" to reversePort else null
        return ReconnectPolicy.order(reverse, lastGood, advertised) // + every LAN address advertised
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

        // Find the reachable route by racing plain TCP probes, then do TLS once, to it: a dead
        // advertised address used to cost a whole connect timeout per channel, and PC Remote
        // opens two channels back to back. Only probes race — a second TLS connection that
        // closed without its header would look to the PC like a new control link.
        val started = android.os.SystemClock.elapsedRealtime()
        val reachable = EndpointRace.first(
            endpoints,
            timeoutMs = DIAL_TIMEOUT_MS + 500L,
            connect = { endpoint ->
                Socket().use { it.connect(InetSocketAddress(endpoint.first, endpoint.second), DIAL_TIMEOUT_MS) }
                endpoint
            },
            close = {},
        )
        val ordered = listOfNotNull(reachable) + (endpoints - setOfNotNull(reachable))
        val socket = ordered.firstNotNullOfOrNull { (host, port) ->
            runCatching { TlsIdentity.connect(host, port, cert, DIAL_TIMEOUT_MS) }.getOrNull()
        } ?: return false
        LogStore.log(LogLevel.INFO, "Channel $channel: dialled back to the PC in ${android.os.SystemClock.elapsedRealtime() - started} ms")

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
