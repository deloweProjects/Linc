package app.linc.android.service

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Build
import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * The phone half of "instant ADB link-up over a hotspot" (PROTOCOL.md v18, M13b): work out which
 * port `adbd` is accepting on, PROVE it is accepting, and tell the desktop.
 *
 * Everything here needs `android.*` or a live socket; the rules it applies live in
 * [AdbLinkRules.kt] so they are unit-tested without a device.
 *
 * **The liveness gate is the point of the whole class.** The L3 link comes up *before* adbd has
 * re-bound to the new interface, so "the hotspot connected" is not "adbd is ready". Announcing an
 * unverified port makes the desktop discover that by timing out, which is the slow, remote,
 * expensive way to learn something the phone can learn locally in 200 ms.
 */
object AdbAnnouncer {

    /** Protocol version these messages require on the negotiated link. */
    private const val REQUIRED_VERSION = 18

    /** PROTOCOL.md v18 / M13a: the port `adb tcpip 5555` opens. */
    private const val DEFAULT_TCPIP_PORT = 5555

    private const val LIVENESS_TIMEOUT_MS = 200
    private const val NSD_BUDGET_MS = 400L

    /**
     * Detect and announce once. Safe to call from any thread that may block — it does socket and
     * NSD work — so callers hand it a background dispatcher.
     */
    fun announceNow(context: Context, reason: String) {
        if (!CompanionOutbox.canSend(REQUIRED_VERSION)) {
            return // a v≤17 desktop never sees these; nothing to do and nothing to log
        }
        val addrs = AdbAddresses.collect()
        val detected = detect(context)
        val gen = AdbGeneration.next()

        if (detected == null) {
            LogStore.log(LogLevel.INFO, "Wireless debugging isn't on, so there's no ADB port to share.")
            CompanionOutbox.trySend(
                Envelope(type = MessageType.ADB_DOWN, payload = AdbPayloads.down(gen, reason)),
                REQUIRED_VERSION,
            )
            return
        }

        // The gate: never announce a port this phone has not just connected to.
        val verified = isAccepting(detected.port)
        CompanionOutbox.trySend(
            Envelope(
                type = MessageType.ADB_ANNOUNCE,
                payload = AdbPayloads.announce(
                    gen = gen,
                    serial = serial(),
                    mode = detected.mode,
                    port = detected.port,
                    verified = verified,
                    addrs = addrs,
                ),
            ),
            REQUIRED_VERSION,
        )
        LogStore.log(
            LogLevel.INFO,
            if (verified) {
                "Told the PC where wireless debugging is listening (port ${detected.port}, ${detected.source})."
            } else {
                "Found a wireless-debugging port (${detected.port}) but it isn't accepting yet — told the PC not to try."
            },
        )
    }

    /** Records the desktop's outcome so the phone's own log says what happened (v18 `adb.ack`). */
    fun onAck(ok: Boolean, endpoint: String) {
        LogStore.log(
            if (ok) LogLevel.INFO else LogLevel.WARN,
            if (ok) {
                "The PC connected to this phone over wireless debugging at $endpoint."
            } else {
                "The PC couldn't connect to this phone at ${endpoint.ifEmpty { "any address it was given" }}."
            },
        )
    }

    /**
     * The three methods in cost order, stopping at the first success. **A miss is "try the next
     * method", never "adbd is down"** — method 1's properties are not populated on every OEM
     * build, and method 2 needs an NSD stack that may be busy.
     */
    private fun detect(context: Context): AdbDetectedPort? {
        // 1. System properties (~1 ms).
        val properties = AdbPortRules.PropertyOrder.associateWith { systemProperty(it) }
        AdbPortRules.pickPort(properties)?.let { return it }

        // 2. NSD self-discovery (~100-300 ms), matched against this phone's own addresses so a
        //    second device on the same hotspot is never mistaken for this one.
        discoverOwnAdbService(context)?.let { return it }

        // 3. Last resort (~200 ms): adbd binds 0.0.0.0, so it answers on loopback. Only the
        //    tcpip default is probed here — see the class doc in AdbLinkRules and the M13b
        //    report for why the ephemeral sweep is deferred rather than shipped janky.
        if (isAccepting(DEFAULT_TCPIP_PORT)) {
            return AdbDetectedPort(DEFAULT_TCPIP_PORT, AdbMode.TCPIP, "loopback probe of $DEFAULT_TCPIP_PORT")
        }
        return null
    }

    /** The liveness gate (PROTOCOL.md v18 `verified`): a real loopback connect, bounded. */
    private fun isAccepting(port: Int): Boolean = try {
        Socket().use { socket ->
            socket.connect(InetSocketAddress(InetAddress.getByName("127.0.0.1"), port), LIVENESS_TIMEOUT_MS)
            true
        }
    } catch (_: Exception) {
        false
    }

    /**
     * Browses the `_adb*._tcp` service types adbd publishes and keeps only a service resolving to
     * one of THIS phone's addresses. Bounded by [NSD_BUDGET_MS] in total: this runs on a link-up
     * path, and an unbounded browse there is a hang the user reads as "the app is broken".
     */
    private fun discoverOwnAdbService(context: Context): AdbDetectedPort? {
        val nsd = try {
            context.getSystemService(Context.NSD_SERVICE) as? NsdManager
        } catch (_: Exception) {
            null
        } ?: return null

        val ownAddresses = AdbAddresses.collect().map { it.ip }
        if (ownAddresses.isEmpty()) return null

        for (serviceType in listOf("_adb-tls-connect._tcp", "_adb._tcp")) {
            val mode = AdbPortRules.modeForServiceType(serviceType) ?: continue
            if (!AdbPortRules.isConnectableMode(mode)) continue
            val found = browseOnce(nsd, serviceType, ownAddresses) ?: continue
            return AdbDetectedPort(found, mode, "NSD $serviceType")
        }
        return null
    }

    private fun browseOnce(nsd: NsdManager, serviceType: String, ownAddresses: List<String>): Int? {
        val resolved = java.util.concurrent.atomic.AtomicInteger(0)
        val done = CountDownLatch(1)
        val pending = java.util.concurrent.ConcurrentLinkedQueue<NsdServiceInfo>()

        val discoveryListener = object : NsdManager.DiscoveryListener {
            override fun onStartDiscoveryFailed(type: String?, errorCode: Int) = done.countDown()
            override fun onStopDiscoveryFailed(type: String?, errorCode: Int) = Unit
            override fun onDiscoveryStarted(type: String?) = Unit
            override fun onDiscoveryStopped(type: String?) = Unit
            override fun onServiceFound(info: NsdServiceInfo?) {
                if (info != null) pending.add(info)
            }
            override fun onServiceLost(info: NsdServiceInfo?) = Unit
        }

        try {
            nsd.discoverServices(serviceType, NsdManager.PROTOCOL_DNS_SD, discoveryListener)
        } catch (_: Exception) {
            return null
        }

        try {
            // Give the browse part of the budget, then resolve what turned up with the rest.
            done.await(NSD_BUDGET_MS / 2, TimeUnit.MILLISECONDS)
            val deadline = System.currentTimeMillis() + NSD_BUDGET_MS / 2
            while (System.currentTimeMillis() < deadline) {
                val candidate = pending.poll() ?: break
                val resolveDone = CountDownLatch(1)
                try {
                    nsd.resolveService(candidate, object : NsdManager.ResolveListener {
                        override fun onResolveFailed(info: NsdServiceInfo?, errorCode: Int) = resolveDone.countDown()
                        override fun onServiceResolved(info: NsdServiceInfo?) {
                            val host = info?.host?.hostAddress
                            val port = info?.port ?: 0
                            if (port in 1..65535 && AdbPortRules.isOwnAddress(host, ownAddresses)) {
                                resolved.set(port)
                            }
                            resolveDone.countDown()
                        }
                    })
                } catch (_: Exception) {
                    resolveDone.countDown()
                }
                resolveDone.await(NSD_BUDGET_MS / 2, TimeUnit.MILLISECONDS)
                if (resolved.get() != 0) break
            }
        } catch (_: InterruptedException) {
            Thread.currentThread().interrupt()
        } finally {
            try {
                nsd.stopServiceDiscovery(discoveryListener)
            } catch (_: Exception) {
                // Already stopped or never started; nothing to unwind.
            }
        }
        return resolved.get().takeIf { it != 0 }
    }

    /**
     * `ro.serialno` via reflection, falling back to `Build.SERIAL`. **Both usually come back
     * empty on a modern Android for an unprivileged app**, which is why the desktop verifies
     * `get-serialno` against the serial it is already PAIRED with and treats this field as a
     * cross-check rather than the source of truth. See the M13b report.
     */
    private fun serial(): String {
        val fromProperty = systemProperty("ro.serialno").orEmpty()
        val raw = if (fromProperty.isNotEmpty()) {
            fromProperty
        } else {
            try {
                @Suppress("DEPRECATION")
                Build.SERIAL.orEmpty()
            } catch (_: Exception) {
                ""
            }
        }
        return if (raw.equals("unknown", ignoreCase = true)) "" else raw
    }

    /** `android.os.SystemProperties.get` is hidden API, so it is read by reflection. */
    private fun systemProperty(name: String): String? = try {
        val clazz = Class.forName("android.os.SystemProperties")
        val get = clazz.getMethod("get", String::class.java)
        (get.invoke(null, name) as? String)?.trim()?.takeIf { it.isNotEmpty() }
    } catch (_: Exception) {
        null
    }
}
