package app.linc.android.service.link

import android.content.Context
import android.net.wifi.WifiManager
import app.linc.android.service.CompanionStateHolder
import app.linc.android.service.LogLevel
import app.linc.android.service.LogStore
import app.linc.android.service.TransportStore
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.SocketException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/**
 * The phone's end of the LAN beacon (v32, UDP 46003, linclink.md §6; the wire format is
 * [LinkBeacon]). It answers a paired PC's probe straight back to it — Windows lets that reply in
 * through its firewall — so the PC learns this phone's address and dials it; and it announces
 * the phone on its own strong events (boot, Wi-Fi joined, a PC link ending). A stranger's probe
 * never matches a secret, so it gets nothing.
 */
class LinkBeaconService(private val context: Context, private val scope: CoroutineScope) {
    private var job: Job? = null
    private var lockJob: Job? = null

    @Volatile
    private var socket: DatagramSocket? = null
    private var multicastLock: WifiManager.MulticastLock? = null
    private val lastAnswered = mutableMapOf<String, Long>()

    fun start() {
        if (job != null) return
        job = scope.launch(Dispatchers.IO) {
            while (isActive) {
                val udp = try {
                    DatagramSocket(null).apply {
                        reuseAddress = true
                        broadcast = true
                        bind(InetSocketAddress(LinkBeacon.PORT))
                    }
                } catch (e: SocketException) {
                    LogStore.log(LogLevel.WARN, "LincLink: couldn't open the network beacon (${e.message}); retrying")
                    delay(10_000)
                    continue
                }
                socket = udp
                val buffer = ByteArray(64)
                try {
                    while (isActive) {
                        val packet = DatagramPacket(buffer, buffer.size)
                        udp.receive(packet)
                        onPacket(udp, packet.data.copyOf(packet.length), packet.address, packet.port)
                    }
                } catch (_: Exception) {
                    // closed by stop(), or the network changed: reopen
                } finally {
                    socket = null
                    runCatching { udp.close() }
                }
            }
        }
        // Some phones filter broadcast traffic while the screen is off; hold the lock only while
        // no PC is connected, which is when the beacon matters.
        lockJob = scope.launch {
            CompanionStateHolder.state.collectLatest { state ->
                if (state is CompanionStateHolder.ServiceState.Connected) releaseLock() else acquireLock()
            }
        }
    }

    fun stop() {
        job?.cancel()
        job = null
        lockJob?.cancel()
        lockJob = null
        runCatching { socket?.close() }
        socket = null
        releaseLock()
    }

    private fun onPacket(udp: DatagramSocket, data: ByteArray, from: InetAddress, port: Int) {
        val packet = LinkBeacon.parse(data) ?: return
        if (packet.role != LinkBeacon.ROLE_PROBE) return // only PCs probe; announcements are ours
        val now = System.currentTimeMillis() / 1000
        // A PC whose QR code is being paired probes with the code's token.
        LinkPairingClient.pendingToken()?.let { token ->
            if (LinkBeacon.matches(packet, token, now)) {
                reply(udp, token, packet, from, port)
                return
            }
        }
        for ((pcId, secret) in LinkStore.allSecrets(context)) {
            if (!LinkBeacon.matches(packet, secret, now)) continue
            reply(udp, secret, packet, from, port)
            val last = lastAnswered[pcId] ?: 0L
            if (now - last >= 60) {
                lastAnswered[pcId] = now
                val name = TransportStore.pc(context, pcId)?.name ?: "the PC"
                LogStore.log(LogLevel.INFO, "LincLink: $name asked for this phone on the network; answered it at ${from.hostAddress}")
            }
            return
        }
    }

    private fun reply(udp: DatagramSocket, secret: ByteArray, probe: BeaconPacket, to: InetAddress, port: Int) {
        val answer = LinkBeacon.build(LinkBeacon.ROLE_REPLY, secret, System.currentTimeMillis() / 1000, probe.nonce, LinkBrain.LISTEN_PORT)
        runCatching { udp.send(DatagramPacket(answer, answer.size, to, port)) }
    }

    /** Say "I'm here" to every paired PC on every network the phone is on (cheap: 23 bytes each). */
    fun announce(why: String) {
        val udp = socket ?: return
        scope.launch(Dispatchers.IO) {
            val secrets = LinkStore.allSecrets(context)
            if (secrets.isEmpty()) return@launch
            val targets = broadcastTargets()
            for ((_, secret) in secrets) {
                val packet = LinkBeacon.build(LinkBeacon.ROLE_ANNOUNCE, secret, System.currentTimeMillis() / 1000, LinkBeacon.newNonce(), LinkBrain.LISTEN_PORT)
                for (target in targets) runCatching { udp.send(DatagramPacket(packet, packet.size, target, LinkBeacon.PORT)) }
            }
            LogStore.log(LogLevel.INFO, "LincLink: told the network this phone is here ($why)")
        }
    }

    private fun broadcastTargets(): List<InetAddress> {
        val targets = linkedSetOf<InetAddress>(InetAddress.getByName("255.255.255.255"))
        runCatching {
            for (iface in NetworkInterface.getNetworkInterfaces()) {
                if (!iface.isUp || iface.isLoopback) continue
                for (address in iface.interfaceAddresses) {
                    if (address.address is Inet4Address) address.broadcast?.let { targets += it }
                }
            }
        }
        return targets.toList()
    }

    private fun acquireLock() {
        if (multicastLock?.isHeld == true) return
        multicastLock = runCatching {
            context.applicationContext.getSystemService(WifiManager::class.java)
                .createMulticastLock("linc:beacon").apply { setReferenceCounted(false); acquire() }
        }.getOrNull()
    }

    private fun releaseLock() {
        runCatching { multicastLock?.takeIf { it.isHeld }?.release() }
        multicastLock = null
    }

    companion object {
        /** The phone's LAN addresses the PC can dial (Wi-Fi, Ethernet, tethering; not mobile data or VPNs). */
        fun lanAddresses(): List<String> = runCatching {
            NetworkInterface.getNetworkInterfaces().toList()
                .filter { it.isUp && !it.isLoopback && !it.name.startsWith("rmnet") && !it.name.startsWith("ccmni") &&
                    !it.name.startsWith("tun") && !it.name.startsWith("dummy") }
                .flatMap { iface -> iface.inetAddresses.toList().filterIsInstance<Inet4Address>().map { it.hostAddress ?: "" } }
                .filter { it.isNotEmpty() && !it.startsWith("169.254") }
                .distinct()
        }.getOrDefault(emptyList())
    }
}
