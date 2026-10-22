package app.linc.android.service.link

import android.content.Context
import android.os.Build
import android.util.Base64
import app.linc.android.protocol.Envelope
import app.linc.android.protocol.Framing
import app.linc.android.protocol.MessageType
import app.linc.android.protocol.parseEnvelope
import app.linc.android.protocol.toJson
import app.linc.android.service.LogLevel
import app.linc.android.service.LogStore
import app.linc.android.service.ReconnectSignal
import app.linc.android.service.TlsIdentity
import app.linc.android.service.TransportStore
import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.Socket
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.put

/**
 * The phone's half of pairing by QR code (v32, linclink.md §2), with no ADB and no shared Wi-Fi
 * setup. Two routes race, so a PC firewall can't stop it: the phone dials the PC at the code's
 * addresses, and meanwhile answers the PC's pairing probes so the PC can dial the phone's
 * listener. On whichever connection lands, the phone checks the PC's certificate against the
 * code's hash, sends its proof (`link.pair`), and on "ok" remembers the PC like any other.
 */
object LinkPairingClient {
    private class Pending(val invite: PairingInvite, val untilMs: Long, val done: CompletableDeferred<String>)

    private val pending = AtomicReference<Pending?>(null)

    /** Outlives the screen that asked: the pairing screen closes as soon as the owner says Pair. */
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private const val WINDOW_MS = 5 * 60_000L
    private const val DIAL_TIMEOUT_MS = 4_000

    private fun active(): Pending? = pending.get()?.takeIf { android.os.SystemClock.elapsedRealtime() < it.untilMs && !it.done.isCompleted }

    /** The PC certificate hash a connection to the listener may present while pairing, else null. */
    fun pendingHash(): String? = active()?.invite?.pcCertSha256

    /** The code's token, which keys the PC's pairing probes, while pairing; else null. */
    fun pendingToken(): ByteArray? = active()?.invite?.token

    /**
     * Pair with the PC in [invite]. Returns the PC's name, or null when nothing landed within a
     * minute (the PC closed its code, or neither side could reach the other).
     */
    suspend fun pair(context: Context, invite: PairingInvite): String? {
        val job = Pending(invite, android.os.SystemClock.elapsedRealtime() + WINDOW_MS, CompletableDeferred())
        // A newer code replaces one still pairing: that one ends quietly (its await is cancelled),
        // so only the newer one reports how it went.
        pending.getAndSet(job)?.done?.cancel()
        LogStore.log(LogLevel.INFO, "Pairing with ${invite.name} from its code: dialling it and listening for it")
        // Only addresses on a local network: a link from anywhere can't make the phone dial out.
        val (lan, elsewhere) = invite.addresses.partition { LinkPairing.isLanAddress(it) }
        if (elsewhere.isNotEmpty()) {
            LogStore.log(LogLevel.WARN, "Pairing: not dialling ${elsewhere.joinToString()} from the code (not on a local network)")
        }
        return try {
            withTimeoutOrNull(60_000) {
                coroutineScope {
                    // Route 1: dial the PC at every address in the code (all at once; the first wins).
                    val dials = lan.map { address ->
                        async(Dispatchers.IO) {
                            runCatching {
                                val (socket, pcCert) = TlsIdentity.connectByHash(address, invite.port, invite.pcCertSha256, DIAL_TIMEOUT_MS)
                                socket.use { complete(context, it, pcCert, dialled = "$address:${invite.port}") }
                            }.onFailure { LogStore.log(LogLevel.INFO, "Pairing: couldn't reach ${invite.name} at $address (${it.javaClass.simpleName})") }
                        }
                    }
                    // Route 2 (LinkListener / LinkBeaconService) completes job.done when the PC dials in.
                    val name = job.done.await()
                    dials.forEach { it.cancel() }
                    name
                }
            }
        } finally {
            pending.compareAndSet(job, null)
        }
    }

    /** The listener accepted a PC presenting the code's certificate: pair on that connection. */
    fun completeIncoming(context: Context, socket: Socket, pcCertDer: ByteArray): Boolean = complete(context, socket, pcCertDer, dialled = null)

    /**
     * On a TLS connection to the code's PC (either direction): send the proof, read the answer,
     * and remember the PC. The phone speaks first here (the PC waits for `link.pair`).
     */
    private fun complete(context: Context, socket: Socket, pcCertDer: ByteArray, dialled: String?): Boolean {
        val job = active() ?: return false
        if (LinkPairing.certHash(pcCertDer) != job.invite.pcCertSha256) return false
        val phoneCert = TlsIdentity.certificateDer() ?: return false
        val output = DataOutputStream(socket.getOutputStream())
        val input = DataInputStream(socket.getInputStream())
        socket.soTimeout = 5_000
        val request = Envelope(type = MessageType.LINK_PAIR, payload = buildJsonObject {
            put("proof", Base64.encodeToString(LinkPairing.proof(job.invite.token, phoneCert, pcCertDer), Base64.NO_WRAP))
            put("model", Build.MODEL)
        })
        Framing.write(output, request.toJson())
        val reply = Framing.read(input)?.let { runCatching { parseEnvelope(it) }.getOrNull() } ?: return false
        if (reply.type != MessageType.LINK_PAIR || (reply.payload["ok"] as? JsonPrimitive)?.booleanOrNull != true) {
            LogStore.log(LogLevel.WARN, "Pairing: ${job.invite.name} turned the code down (it may have expired); show a new one on the PC")
            return false
        }
        val name = (reply.payload["name"] as? JsonPrimitive)?.contentOrNull ?: job.invite.name
        val pc = TransportStore.saveExchange(context, name, Base64.encodeToString(pcCertDer, Base64.NO_WRAP), job.invite.port, 0)
        // The address that just worked is where to dial next, even where mDNS can't reach the phone.
        if (dialled != null) TransportStore.saveLastEndpoint(context, pc.id, dialled)
        // The owner just paired this PC on purpose: if one particular PC was picked, it's this one now.
        if (TransportStore.selectedPcId(context) != null) TransportStore.selectPc(context, pc.id)
        LogStore.log(LogLevel.INFO, "Paired with ${pc.name} by its code (no cable)")
        job.done.complete(pc.name)
        ReconnectSignal.poke("paired with ${pc.name}")
        return true
    }

    /** For the UI: pair in the background, then [done] on the main thread with the PC's name (null: didn't pair). */
    fun pairInBackground(context: Context, invite: PairingInvite, done: (String?) -> Unit) {
        if (active()?.invite?.token?.contentEquals(invite.token) == true) {
            // The same code tapped again (the camera still shows its link once the pairing screen
            // closes): the pairing already under way answers, once.
            LogStore.log(LogLevel.INFO, "Pairing with ${invite.name} is already under way")
            return
        }
        val app = context.applicationContext
        scope.launch {
            val name = pair(app, invite)
            withContext(Dispatchers.Main) { done(name) }
        }
    }
}
