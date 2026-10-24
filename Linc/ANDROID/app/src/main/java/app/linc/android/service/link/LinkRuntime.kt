package app.linc.android.service.link

import android.content.Context
import app.linc.android.protocol.PROTOCOL_VERSION
import app.linc.android.service.SocketServer
import app.linc.android.service.TlsIdentity
import app.linc.android.service.TransportStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import kotlinx.serialization.json.putJsonArray
import kotlinx.serialization.json.add

/**
 * LincLink on the phone, in one place (Vibe/agent/design/linclink.md): what the companion's
 * server asks of a v32 session ([SocketServer.LinkHost]) and the two network pieces, the TLS
 * listener the PC dials and the LAN beacon. With the engine switched off nothing here runs and
 * the server stays at v31.
 */
class LinkRuntime(private val context: Context, private val scope: CoroutineScope) : SocketServer.LinkHost {
    private var listener: LinkListener? = null
    private var beacon: LinkBeaconService? = null

    val engineOn: Boolean get() = LinkStore.engineOn(context)

    /** Start the listener and the beacon (once the server exists); a no-op with the engine off. */
    fun start(server: SocketServer) {
        if (!engineOn) return
        if (listener == null) listener = LinkListener(context, scope, server).also { it.start() }
        if (beacon == null) beacon = LinkBeaconService(context, scope).also { it.start() }
        announce("Linc started")
        // Remember the networks where the PC's ADB rode on wireless debugging (it says so in link.state).
        if (adbWatch == null) adbWatch = scope.launch {
            app.linc.android.service.CompanionStateHolder.pcLink.collect { pc ->
                if (pc?.adbVia == "wireless") currentNetwork()?.let { LinkStore.rememberAdbNetwork(context, it) }
            }
        }
        armIfKnown("Linc started")
    }

    private var adbWatch: kotlinx.coroutines.Job? = null
    private var grantMissingLogged = false

    /**
     * After a restart or on joining a Wi-Fi where the PC's ADB has run over wireless debugging
     * before, turn wireless debugging back on (Android turns it off; test build B: "connected to
     * Wi-Fi but no ADB, all pairing is done"). Only there: switching it on anywhere else would
     * open adb on a network the owner never used it on. Needs the one-time grant; says once if missing.
     */
    fun armIfKnown(why: String) {
        if (!engineOn || !TransportStore.hasAnyPc(context) || app.linc.android.service.AdbArming.isArmed(context)) return
        val network = currentNetwork() ?: return
        if (network !in LinkStore.adbNetworks(context)) return
        if (!app.linc.android.service.AdbArming.isPermissionHeld(context)) {
            if (!grantMissingLogged) {
                grantMissingLogged = true
                app.linc.android.service.LogStore.log(app.linc.android.service.LogLevel.INFO,
                    "Wireless debugging is off and Linc can't turn it on by itself yet: plug the phone into the PC once (it grants that)")
            }
            return
        }
        app.linc.android.service.LogStore.log(app.linc.android.service.LogLevel.INFO, "Turning wireless debugging back on ($why, on a network the PC used it on)")
        app.linc.android.service.AdbArming.arm(context)
    }

    /** The Wi-Fi network the phone is on now, as [LinkBrain.fingerprint] (gateway and subnet), or null off Wi-Fi. */
    private fun currentNetwork(): String? = runCatching {
        val cm = context.getSystemService(android.net.ConnectivityManager::class.java)
        val network = cm.activeNetwork ?: return null
        val caps = cm.getNetworkCapabilities(network) ?: return null
        if (!caps.hasTransport(android.net.NetworkCapabilities.TRANSPORT_WIFI)) return null
        val props = cm.getLinkProperties(network) ?: return null
        val gateway = props.routes.firstOrNull { it.isDefaultRoute && it.gateway is java.net.Inet4Address }?.gateway?.hostAddress
        val v4 = props.linkAddresses.firstOrNull { it.address is java.net.Inet4Address } ?: return null
        LinkBrain.fingerprint(gateway, LinkBrain.subnetOf(v4.address.address, v4.prefixLength))
    }.getOrNull()

    fun stop() {
        adbWatch?.cancel()
        adbWatch = null
        listener?.stop()
        listener = null
        beacon?.stop()
        beacon = null
    }

    /** A strong "the PC may be reachable now" moment on the phone's side (Wi-Fi joined, screen on…). */
    fun announce(why: String) {
        beacon?.announce(why)
        // A moment later: Android reports the network before it becomes the active one.
        if (why == "Wi-Fi connected") scope.launch(kotlinx.coroutines.Dispatchers.IO) {
            kotlinx.coroutines.delay(2_000)
            armIfKnown(why)
        }
    }

    override fun maxVersion(): Int = LinkStore.maxVersion(context, PROTOCOL_VERSION)

    override fun phoneId(): String? = TlsIdentity.certificateBase64()?.let { TransportStore.idOf(it) }

    override fun secretGen(pcId: String?): Int = LinkStore.secretGen(context, pcId)

    override fun saveSecret(pcId: String, secret: ByteArray, gen: Int): Boolean = LinkStore.saveSecret(context, pcId, secret, gen)

    override fun where(): JsonObject? {
        if (listener?.running != true) return null
        val addresses = LinkBeaconService.lanAddresses()
        if (addresses.isEmpty()) return null
        return buildJsonObject {
            putJsonArray("addrs") { addresses.forEach { add(it) } }
            put("port", LinkBrain.LISTEN_PORT)
        }
    }

    override fun sessionDetached() = announce("the PC link ended")
}
