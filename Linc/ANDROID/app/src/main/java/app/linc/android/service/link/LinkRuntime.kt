package app.linc.android.service.link

import android.content.Context
import app.linc.android.protocol.PROTOCOL_VERSION
import app.linc.android.service.SocketServer
import app.linc.android.service.TlsIdentity
import app.linc.android.service.TransportStore
import kotlinx.coroutines.CoroutineScope
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
    }

    fun stop() {
        listener?.stop()
        listener = null
        beacon?.stop()
        beacon = null
    }

    /** A strong "the PC may be reachable now" moment on the phone's side (Wi-Fi joined, screen on…). */
    fun announce(why: String) {
        beacon?.announce(why)
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
