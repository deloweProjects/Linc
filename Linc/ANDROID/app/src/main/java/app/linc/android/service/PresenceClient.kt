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
 * connected, watch mDNS for the desktops' `_linc._tcp` adverts (plus the adb-reverse
 * loopback port when one was exchanged) and dial out with mutual TLS. A successful
 * dial is handed to [SocketServer.serveExternal] — the same protocol code serves it.
 * The scan interval widens while the PC is absent (battery).
 *
 * D-088: the phone knows several PCs. It dials only the PC the owner picked, or, on automatic,
 * every PC it knows — each with that PC's own pinned certificate, so a dial can only ever land
 * on the PC it was meant for.
 */
class PresenceClient(
    private val context: Context,
    private val scope: CoroutineScope,
    private val server: SocketServer,
    private val gate: PcGate,
) {
    private var loopJob: Job? = null
    private var discovering = false

    /** One `_linc._tcp` advert: the PC's id (from its TXT record; null from older PCs) and addresses. */
    private data class Advert(val pcId: String?, val endpoints: List<Pair<String, Int>>)

    /** Live adverts by mDNS service name — one per PC on the network. */
    private val advertised = java.util.concurrent.ConcurrentHashMap<String, Advert>()

    /** Where the current control connection was dialed to — channel dial-backs reuse it. */
    @Volatile
    private var activeEndpoint: Pair<String, Int>? = null

    /** Misses in a row for each PC's remembered last-good address ([ReconnectPolicy.FORGET_AFTER]). */
    private val lastGoodFailures = mutableMapOf<String, Int>()

    private val discoveryListener = object : NsdManager.DiscoveryListener {
        override fun onDiscoveryStarted(serviceType: String) {}
        override fun onDiscoveryStopped(serviceType: String) {}
        override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {}
        override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {}
        override fun onServiceLost(info: NsdServiceInfo) {
            advertised.remove(info.serviceName)
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
                            val pcId = info.attributes["id"]?.let { String(it) }?.takeIf { it.isNotBlank() }
                            advertised[info.serviceName] = Advert(pcId, hosts.map { it to port })
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
            TransportStore.dialTargets(context).isNotEmpty() &&
            CompanionStateHolder.state.value is ServiceState.Listening

    /**
     * Dials, for every PC it may ([TransportStore.dialTargets]), the USB reverse tunnel, then the
     * address that worked last time, then whatever that PC advertised on mDNS
     * ([ReconnectPolicy.order]). Blocks while connected.
     */
    private fun dialControl(): Boolean {
        val targets = TransportStore.dialTargets(context)
        // Which PCs each address may belong to: an address is only ever TLS-dialled with the
        // certificates of the PCs that could be there.
        val owners = linkedMapOf<Pair<String, Int>, MutableList<TransportStore.KnownPc>>()
        for (pc in targets) {
            for (endpoint in candidateEndpoints(pc)) owners.getOrPut(endpoint) { mutableListOf() } += pc
        }
        val candidates = owners.keys.toList()
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
        for (pc in targets) {
            val lastGood = ReconnectPolicy.parse(pc.lastEndpoint) ?: continue
            if (reachable == lastGood) continue
            val failures = (lastGoodFailures[pc.id] ?: 0) + 1
            lastGoodFailures[pc.id] = failures
            if (ReconnectPolicy.shouldForget(failures)) {
                TransportStore.saveLastEndpoint(context, pc.id, null)
                lastGoodFailures.remove(pc.id)
                LogStore.log(LogLevel.INFO, "Forgot ${ReconnectPolicy.format(lastGood)} for ${pc.name}: it failed ${ReconnectPolicy.FORGET_AFTER} times in a row")
            }
        }
        for (endpoint in listOfNotNull(reachable)) {
            var dialled: TransportStore.KnownPc? = null
            val socket = owners[endpoint].orEmpty().firstNotNullOfOrNull { pc ->
                runCatching { TlsIdentity.connect(endpoint.first, endpoint.second, pc.cert, DIAL_TIMEOUT_MS) }
                    .getOrNull()?.also { dialled = pc }
            } ?: continue
            val pc = dialled!!
            if (endpoint.first != "127.0.0.1") {
                TransportStore.saveLastEndpoint(context, pc.id, ReconnectPolicy.format(endpoint))
            }
            if (endpoint == ReconnectPolicy.parse(pc.lastEndpoint)) lastGoodFailures.remove(pc.id)
            activeEndpoint = endpoint
            LogStore.log(LogLevel.INFO, "Connected to ${pc.name} directly (no ADB) at ${endpoint.first}:${endpoint.second}")
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

    /** Where [pc] might be: its USB reverse tunnel, its last good address, then its adverts. */
    private fun candidateEndpoints(pc: TransportStore.KnownPc): List<Pair<String, Int>> {
        // adb-reverse (USB cable) path first: loopback is always reachable when it exists.
        val reverse = if (pc.reversePort > 0) "127.0.0.1" to pc.reversePort else null
        // Its own advert, or one from an older PC that doesn't say which PC it is.
        val adverts = advertised.values.filter { it.pcId == null || it.pcId == pc.id }.flatMap { it.endpoints }
        return ReconnectPolicy.order(reverse, ReconnectPolicy.parse(pc.lastEndpoint), adverts)
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
        // The channel goes back to the PC that is connected now, pinned to that PC's certificate.
        val pc = TransportStore.pc(context, gate.currentPcId) ?: return false
        val cert = pc.cert
        val endpoints = buildList {
            activeEndpoint?.let { add(it) }
            addAll(candidateEndpoints(pc))
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
