package app.linc.android.service

import android.net.LocalServerSocket
import android.net.LocalSocket
import app.linc.android.BuildConfig
import app.linc.android.protocol.Envelope
import app.linc.android.protocol.ErrorCode
import app.linc.android.protocol.Framing
import app.linc.android.protocol.MessageType
import app.linc.android.protocol.PROTOCOL_MIN_VERSION
import app.linc.android.protocol.PROTOCOL_VERSION
import app.linc.android.protocol.ProtocolJson
import app.linc.android.protocol.SOCKET_NAME
import app.linc.android.protocol.Topic
import app.linc.android.protocol.parseEnvelope
import app.linc.android.protocol.toJson
import app.linc.android.service.CompanionStateHolder.ServiceState
import app.linc.android.service.link.FrameArrival
import app.linc.android.service.link.LinkBrain
import app.linc.android.service.link.LinkSession
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.Socket
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.longOrNull
import kotlinx.serialization.json.put

/**
 * Speaks the companion protocol (docs/PROTOCOL.md) over any connection the phone has:
 * the abstract Unix socket `linc` reached through ADB, and — since v9 — Direct TLS
 * connections the presence loop dials out ([serveExternal]). Each connection is a
 * generic stream pair ([Peer]); the first frame disambiguates a control connection
 * (a `hello` envelope, identical to v≤4) from a typed channel connection
 * (a `{channel, sessionToken}` header — D-018). The desktop is the only expected peer.
 */
class SocketServer(
    private val scope: CoroutineScope,
    private val statusPayload: (negotiatedVersion: Int) -> JsonObject,
    private val onSetClipboard: (String) -> Unit,
    private val onClipboardGet: () -> Unit = {},
    private val onDesktopConnected: () -> Unit,
    private val onDismissNotification: (String) -> Unit,
    private val bulkFetch: (kind: String, id: String) -> ByteArray?,
    private val onNotificationAction: (key: String, index: Int) -> Boolean,
    private val onNotificationReply: (key: String, index: Int, text: String) -> Boolean,
    private val onMediaControl: (action: String, positionMs: Long?) -> Unit,
    private val onMediaSubscribed: () -> Unit,
    private val onLocate: () -> Boolean,
    private val onSetDnd: (enabled: Boolean) -> Boolean,
    private val onSetSound: (mode: String) -> Boolean,
    // v15 display control (D-055): each returns false when the WRITE_SETTINGS appop is
    // missing (the SocketServer branch then answers error not-granted) or the payload is
    // bad; true only when the Settings.System write actually landed.
    private val onSetRotation: (mode: String) -> Boolean,
    private val onSetBrightness: (auto: Boolean, level: Int?) -> Boolean,
    private val onTlsExchange: (certB64: String, tlsPort: Int, reversePort: Int) -> String?,
    private val onChannelOpen: (channel: Int, token: String) -> Boolean,
    private val fileServe: (java.io.DataInputStream, java.io.DataOutputStream) -> Unit,
    private val photosRecent: (limit: Int) -> JsonObject,
    private val onContinueUrl: (url: String) -> Boolean,
    private val onSyncConfig: (config: JsonObject) -> Unit,
    private val smsList: (limit: Int) -> JsonObject?,
    private val smsSend: (address: String, body: String) -> Boolean,
    private val callLog: (limit: Int) -> JsonObject?,
    private val callDial: (number: String) -> Boolean,
    private val callDecline: () -> Boolean,
    private val onPcMediaState: (JsonObject) -> Unit,
    // v16 (D-058): the launchable-app inventory. A pull, not a push — there is deliberately
    // no install/remove event, so this only ever runs in answer to an apps.get.
    private val appsGet: () -> JsonObject,
    private val onShareIncoming: (name: String, path: String) -> Unit,
    // v18 (M13b): detect adbd's port, prove it is accepting, and announce it. Called once per
    // control handshake and again whenever the desktop asks with adb.arm. Always off the control
    // loop — it does a loopback connect and an NSD browse, and blocking the loop on those would
    // stall every other message for as long as they take.
    private val announceAdb: (reason: String) -> Unit,
    private val onAdbAck: (ok: Boolean, endpoint: String) -> Unit,
    // M13c §2.2: arm adbd (when the one-time grant is held) and then announce. Separate from
    // announceAdb because the handshake path must never arm on its own — arming is a visible
    // change to the user's device and only happens when the desktop explicitly asks.
    private val onAdbArm: (reason: String) -> Unit,
    // D-088: one PC at a time, and the PC the owner picked.
    private val gate: PcGate,
    // v32 (LincLink): what a resumable session needs from the rest of the app. Null keeps the
    // server at v31, exactly as before.
    private val link: LinkHost? = null,
) {
    /** v32 (LincLink, Vibe/agent/design/linclink.md): what the server needs from the rest of the app. */
    interface LinkHost {
        /** The highest version to negotiate: 32 with the new engine on, 31 with it off. */
        fun maxVersion(): Int

        /** This phone's LincLink id: the hash of its certificate, as the PC computes it. */
        fun phoneId(): String?

        /** The link-secret generation held for this PC (0: none yet). */
        fun secretGen(pcId: String?): Int

        fun saveSecret(pcId: String, secret: ByteArray, gen: Int): Boolean

        /** Where the PC can dial this phone (`link.where`), or null when the listener isn't up. */
        fun where(): JsonObject?

        /** A session lost its transport: find the PC again now. */
        fun sessionDetached()
    }

    /** One connection, transport-agnostic: LocalSocket over ADB or SSLSocket over LAN. */
    class Peer(
        val input: DataInputStream,
        val output: DataOutputStream,
        val setTimeout: (Int) -> Unit,
        /** Where this connection came from: null over ADB, the PC's address over Direct TLS. */
        val tlsAddress: String? = null,
        /** Ends the connection from outside — when the owner picks another PC (D-088). */
        val close: () -> Unit = {},
    )

    private var serverSocket: LocalServerSocket? = null

    /** Control connections being served right now; more than one only during a handoff (D-081). */
    private val liveControls = java.util.concurrent.atomic.AtomicInteger(0)

    /**
     * A PC is connecting: sessions still waiting for a different PC — or an older session of this
     * one that it didn't resume — end now, so they can't keep it out (the gate's one-PC rule).
     */
    private fun releaseWaitingFor(pcId: String?) {
        for (session in sessions.values.toList()) {
            if (!session.attached) session.end(if (session.pcId == pcId) "the PC started a new session" else "another PC connected")
        }
    }

    /** v32 sessions by id, attached or waiting for a resume (linclink.md §4). */
    private val sessions = java.util.concurrent.ConcurrentHashMap<String, LinkControl>()

    /** True while a v32 session waits for its PC to come back on another route: worth dialling for. */
    fun wantsPc(): Boolean = sessions.values.any { !it.attached }
    private var acceptJob: Job? = null

    fun start() {
        if (acceptJob != null) return
        acceptJob = scope.launch(Dispatchers.IO) {
            // Stop then Start used to leave Linc "stopped": the old socket's name was still taken
            // (D-091), so this bind failed once and gave up. stop() now frees it properly; a few
            // retries cover the moment it takes Android to let go.
            var server: LocalServerSocket? = null
            for (attempt in 1..10) {
                server = try {
                    LocalServerSocket(SOCKET_NAME)
                } catch (_: IOException) {
                    null
                }
                if (server != null || !isActive) break
                kotlinx.coroutines.delay(300)
            }
            if (server == null) {
                LogStore.log(LogLevel.WARN, "Linc couldn't open its connection point; stop and start Linc again")
                CompanionStateHolder.update(ServiceState.Stopped)
                return@launch
            }
            serverSocket = server
            CompanionStateHolder.update(ServiceState.Listening)
            while (isActive) {
                val client = try {
                    server.accept()
                } catch (_: IOException) {
                    break // socket closed by stop()
                }
                // Each connection is served on its own coroutine so a bulk transfer can
                // never block the control channel (and vice versa).
                scope.launch(Dispatchers.IO) {
                    val peer = Peer(
                        DataInputStream(client.inputStream),
                        DataOutputStream(client.outputStream),
                        { millis -> client.soTimeout = millis },
                        close = { runCatching { client.shutdownInput() }; runCatching { client.close() } },
                    )
                    try {
                        dispatch(peer)
                    } catch (_: IOException) {
                        // Peer vanished mid-conversation.
                    } catch (_: SerializationException) {
                        // First frame wasn't valid JSON; drop the client.
                    } catch (_: IllegalArgumentException) {
                        // First frame was valid JSON but not an object; drop the client.
                    } finally {
                        runCatching { client.close() }
                    }
                }
            }
        }
    }

    /**
     * Serves a Direct-TLS connection the presence loop dialed (v9). Blocks until it ends, and
     * says why it ended (null for a clean close) so a dropped link shows up in the log.
     */
    fun serveExternal(socket: Socket): String? {
        val peer = Peer(
            DataInputStream(socket.getInputStream()),
            DataOutputStream(socket.getOutputStream()),
            { millis -> socket.soTimeout = millis },
            tlsAddress = socket.inetAddress?.hostAddress,
            close = { runCatching { socket.close() } },
        )
        try {
            dispatch(peer)
            return null
        } catch (e: IOException) {
            return "${e.javaClass.simpleName}: ${e.message}"
        } catch (_: SerializationException) {
            return "bad message"
        } catch (_: IllegalArgumentException) {
            return "bad message"
        } finally {
            runCatching { socket.close() }
        }
    }

    /**
     * Serves a connection the PC dialled to this phone's LincLink listener (v32): a control
     * connection (the PC's hello) or a typed channel (any of them: the PC dials its own channels
     * on a link it dialled). Blocks until it ends; says why it ended, like [serveExternal].
     */
    fun serveListened(socket: Socket): String? {
        val peer = Peer(
            DataInputStream(socket.getInputStream()),
            DataOutputStream(socket.getOutputStream()),
            { millis -> socket.soTimeout = millis },
            tlsAddress = socket.inetAddress?.hostAddress,
            close = { runCatching { socket.close() } },
        )
        try {
            peer.setTimeout(HANDSHAKE_TIMEOUT_MS)
            val firstJson = Framing.read(peer.input) ?: return null
            val first = ProtocolJson.parseToJsonElement(firstJson).jsonObject
            if (first.containsKey("channel")) {
                val token = (first["sessionToken"] as? JsonPrimitive)?.contentOrNull
                val channel = (first["channel"] as? JsonPrimitive)?.intOrNull
                if (!SessionRegistry.isValid(token) || channel == null) {
                    LogStore.log(LogLevel.WARN, "Rejected a channel connection with an invalid session token")
                    return "bad channel header"
                }
                peer.setTimeout(0)
                serveDialedChannel(
                    channel,
                    DataInputStream(java.io.BufferedInputStream(socket.getInputStream(), 64 * 1024)),
                    peer.output,
                )
            } else {
                serveControl(firstJson, peer)
            }
            return null
        } catch (e: IOException) {
            return "${e.javaClass.simpleName}: ${e.message}"
        } catch (_: SerializationException) {
            return "bad message"
        } catch (_: IllegalArgumentException) {
            return "bad message"
        } finally {
            runCatching { socket.close() }
        }
    }

    /** Serves a channel connection the phone itself dialed back (v9 `channel.open`). */
    fun serveDialedChannel(channel: Int, input: DataInputStream, output: DataOutputStream) {
        when (channel) {
            CHANNEL_BULK -> bulkLoop(input, output)
            CHANNEL_FILES -> fileServe(input, output)
            CHANNEL_VIDEO -> videoReceive(input)
            CHANNEL_AUDIO -> audioReceive(input, output)
            CHANNEL_MIC -> PcVoiceAccess.stream(output)
            CHANNEL_PHONE_AUDIO -> PhoneSource.serve(input, output)
            CHANNEL_TERMINAL -> PcTools.serveTerminal(input, output)
            CHANNEL_PC_FILES -> PcTools.serveFiles(input, output)
        }
    }

    /**
     * Channel 6 (pc-audio, v20): like pc-video, the desktop serves; config frame, then AAC. A v26
     * config with `clock` is Play everywhere's stream: PC-clock timestamps, and clock probes
     * answered on the channel's back direction (D-096).
     */
    private fun audioReceive(input: DataInputStream, output: DataOutputStream) {
        val configJson = Framing.read(input) ?: return
        val config = ProtocolJson.parseToJsonElement(configJson).jsonObject
        val rate = (config["sampleRate"] as? JsonPrimitive)?.intOrNull ?: return
        val channels = (config["channels"] as? JsonPrimitive)?.intOrNull ?: 2
        if ((config["clock"] as? JsonPrimitive)?.booleanOrNull == true) SyncedAudio.receive(input, output, rate, channels)
        else AudioReceiver.receive(input, rate, channels)
    }

    /**
     * Channel 5 (pc-video, v14/M05): unlike bulk/files the DESKTOP is the server here — after
     * the JSON config frame the desktop streams H.264 and the phone decodes it. So this reads
     * the config, then hands the stream to [MirrorReceiver].
     */
    private fun videoReceive(input: DataInputStream) {
        val configJson = Framing.read(input) ?: return
        val config = ProtocolJson.parseToJsonElement(configJson).jsonObject
        val width = (config["width"] as? JsonPrimitive)?.intOrNull ?: return
        val height = (config["height"] as? JsonPrimitive)?.intOrNull ?: return
        MirrorReceiver.receive(input, width, height)
    }

    fun stop() {
        acceptJob?.cancel()
        acceptJob = null
        // Closing a LocalServerSocket does not wake a thread blocked in accept() on Android, and
        // until that thread returns the socket's name stays taken, so the next Start failed to
        // bind (D-091). Shutting the descriptor down first makes accept() return at once.
        serverSocket?.let { s ->
            runCatching { android.system.Os.shutdown(s.fileDescriptor, android.system.OsConstants.SHUT_RDWR) }
            runCatching { s.close() }
        }
        serverSocket = null
        for (session in sessions.values.toList()) session.end("Linc stopped")
        CompanionStateHolder.update(ServiceState.Stopped)
    }

    /** Reads the first frame and routes to the control or a typed-channel handler. */
    private fun dispatch(peer: Peer) {
        // Every connection must open within the handshake window (spec §Timeouts).
        peer.setTimeout(HANDSHAKE_TIMEOUT_MS)
        val firstJson = Framing.read(peer.input) ?: return
        val first = ProtocolJson.parseToJsonElement(firstJson).jsonObject

        // Pinned disambiguation (PROTOCOL.md v5): a `channel` field ⇒ typed channel,
        // otherwise the frame is a `hello` envelope ⇒ control/legacy connection.
        if (first.containsKey("channel")) {
            val token = (first["sessionToken"] as? JsonPrimitive)?.contentOrNull
            if (!SessionRegistry.isValid(token)) {
                LogStore.log(LogLevel.WARN, "Rejected a channel connection with an invalid session token")
                return
            }
            peer.setTimeout(0)
            when ((first["channel"] as? JsonPrimitive)?.intOrNull) {
                CHANNEL_BULK -> bulkLoop(peer.input, peer.output)
                CHANNEL_FILES -> fileServe(peer.input, peer.output)
                else -> Unit // channels not implemented yet: close.
            }
        } else {
            serveControl(firstJson, peer)
        }
    }

    /** The control channel: exactly the v0–v4 conversation, plus the v5+ session token. */
    private fun serveControl(helloJson: String, peer: Peer) {
        val writeLock = Any()
        fun send(envelope: Envelope) = synchronized(writeLock) {
            Framing.write(peer.output, envelope.toJson())
        }

        val hello = parseEnvelope(helloJson)
        if (hello.type != MessageType.HELLO) return

        val minV = (hello.payload["minV"] as? JsonPrimitive)?.intOrNull ?: 0
        val maxV = (hello.payload["maxV"] as? JsonPrimitive)?.intOrNull ?: 0
        // Speak the highest version both sides support. LincLink (v32) only with its host and its
        // switch on; otherwise the ceiling is 31 and everything below is exactly the old path.
        val ceiling = link?.maxVersion() ?: (LinkBrain.VERSION - 1)
        val negotiated = minOf(maxV, ceiling, PROTOCOL_VERSION)
        if (negotiated < minV || negotiated < PROTOCOL_MIN_VERSION) {
            send(Envelope(
                type = MessageType.ERROR,
                replyTo = hello.id,
                payload = buildJsonObject {
                    put("code", ErrorCode.VERSION_MISMATCH)
                    put("message", "phone supports protocol v$PROTOCOL_MIN_VERSION..v$PROTOCOL_VERSION, desktop offered $minV..$maxV")
                },
            ))
            return
        }

        peer.setTimeout(0) // idle is fine after the handshake; the desktop pings

        // D-088: is this PC allowed the phone right now? One PC at a time, and the picked one.
        val pcId = (hello.payload["pcId"] as? JsonPrimitive)?.contentOrNull
        val pcHost = (hello.payload["host"] as? JsonPrimitive)?.contentOrNull
        if (negotiated >= LinkBrain.VERSION && link != null) {
            serveLinkControl(hello, negotiated, pcId, pcHost, peer, link)
            return
        }
        releaseWaitingFor(pcId)
        val ticket = when (val answer = gate.admit(pcId, pcHost, peer.close)) {
            is PcGate.Answer.Refuse -> {
                send(Envelope(
                    type = MessageType.ERROR,
                    replyTo = hello.id,
                    payload = buildJsonObject {
                        put("code", ErrorCode.BUSY)
                        put("message", answer.message)
                    },
                ))
                return
            }
            is PcGate.Answer.Admit -> answer.ticket
        }

        // v5: hand the desktop a session token so it can open channel connections.
        val sessionToken = if (negotiated >= 5) SessionRegistry.issue() else null
        var outboxRegistration: CompanionOutbox.Registration? = null
        try {
            send(Envelope(
                type = MessageType.HELLO,
                replyTo = hello.id,
                payload = buildJsonObject {
                    put("app", "linc-android")
                    put("appVersion", BuildConfig.VERSION_NAME)
                    // Informational only (Provenance.kt); optional, older desktops ignore it.
                    put("author", app.linc.android.Provenance.AUTHOR)
                    put("v", negotiated)
                    if (sessionToken != null) put("sessionToken", sessionToken)
                },
            ))
        } catch (e: Exception) {
            gate.release(ticket)
            throw e
        }

        liveControls.incrementAndGet()
        try {
            val desktopApp = (hello.payload["app"] as? JsonPrimitive)?.contentOrNull ?: "desktop"
            val desktopVersion = (hello.payload["appVersion"] as? JsonPrimitive)?.contentOrNull ?: "?"
            val host = (hello.payload["host"] as? JsonPrimitive)?.contentOrNull
            CompanionStateHolder.update(ServiceState.Connected(desktopApp, desktopVersion, negotiated, host))
            CompanionStateHolder.setLink(CompanionStateHolder.Link(
                if (peer.tlsAddress != null) CompanionStateHolder.Route.Direct else CompanionStateHolder.Route.Adb,
                peer.tlsAddress,
                System.currentTimeMillis(),
            ))
            LogStore.log(LogLevel.INFO, "Connected to $desktopApp (protocol v$negotiated)")

            // Unsolicited phone -> desktop messages (clipboard, notifications) exist from v1 on.
            if (negotiated >= 1) {
                outboxRegistration = CompanionOutbox.register(negotiated, ::send)
            }
            // Pre-v6 peers can't subscribe, so they get the notification backlog at
            // handshake as always; a v6 peer gets it on subscribe("notifications")
            // instead — pushing it here would be dropped by topic gating (D-019).
            if (negotiated in 2..5) {
                onDesktopConnected()
            }
            // v18: the announcement is unsolicited and belongs at handshake — this control
            // socket coming up IS the link event, and on a hotspot its far end is exactly the
            // desktop that needs the port.
            if (negotiated >= 18) {
                scope.launch(Dispatchers.IO) { announceAdb("control connection established") }
            }

            controlLoop(peer.input, negotiated, ticket, pcHost, sessionToken, ::send, link = null)
        } finally {
            // Release only OUR registration — with concurrent transports another control
            // connection may have registered after us, and nulling its slot silently kills
            // every unsolicited send until the next handshake.
            outboxRegistration?.let { CompanionOutbox.release(it) }
            SessionRegistry.clear(sessionToken)
            gate.release(ticket)
            // Only report a disconnect when the peer left, not when we're shutting down — and not
            // while another control connection is still live: the PC moves the link between USB
            // and Wi-Fi by opening the new one before closing the old (D-081).
            val remaining = liveControls.decrementAndGet()
            if (serverSocket != null && remaining == 0) {
                CompanionStateHolder.update(ServiceState.Listening)
                CompanionStateHolder.setLink(null)
                LogStore.log(LogLevel.WARN, "Desktop disconnected")
                // The PC may still be right there on another route (the cable just came out):
                // dial it directly now rather than after the presence loop's wait.
                ReconnectSignal.poke("the PC link ended")
            } else if (remaining > 0) {
                LogStore.log(LogLevel.INFO, "One of two PC links closed; still connected on the other")
            }
        }
    }

    /**
     * v32 control: a session that outlives its transport (linclink.md §4). A hello naming a session
     * this phone still holds for the same PC resumes it — the hello reply, then every frame the PC
     * lacks, then the same conversation — and the old transport, if any, is closed. Otherwise a
     * new session starts exactly as below v32, plus numbering. When the transport dies the session
     * waits [LinkBrain.SESSION_KEEP_MS]; the phone keeps showing "Connected" for the 12 s linger.
     */
    private fun serveLinkControl(hello: Envelope, negotiated: Int, pcId: String?, pcHost: String?, peer: Peer, host: LinkHost) {
        val offer = hello.payload["link"] as? JsonObject
        val offeredId = (offer?.get("session") as? JsonPrimitive)?.contentOrNull
        val pcRecv = (offer?.get("recv") as? JsonPrimitive)?.longOrNull ?: 0L
        val pcKeep = (offer?.get("keep") as? JsonPrimitive)?.longOrNull ?: 1L
        val desktopApp = (hello.payload["app"] as? JsonPrimitive)?.contentOrNull ?: "desktop"
        val desktopVersion = (hello.payload["appVersion"] as? JsonPrimitive)?.contentOrNull ?: "?"

        // The PC can replay what this side lacks only if it still keeps everything after `received`.
        val known = offeredId?.let { sessions[it] }
            ?.takeIf { it.pcId == pcId && it.negotiated == negotiated && pcKeep <= it.link.received + 1 }
        var session = known
        var gen = known?.attach(peer, hello.id, pcRecv, resumed = true)
        if (gen == null) {
            releaseWaitingFor(pcId)
            // A new session: admitted by the gate like any other, its ticket held until it ends.
            var created: LinkControl? = null
            val ticket = when (val answer = gate.admit(pcId, pcHost) { created?.end("another PC was picked") ?: peer.close() }) {
                is PcGate.Answer.Refuse -> {
                    Framing.write(peer.output, Envelope(
                        type = MessageType.ERROR,
                        replyTo = hello.id,
                        payload = buildJsonObject {
                            put("code", ErrorCode.BUSY)
                            put("message", answer.message)
                        },
                    ).toJson())
                    return
                }
                is PcGate.Answer.Admit -> answer.ticket
            }
            val fresh = LinkControl(
                id = java.util.UUID.randomUUID().toString().replace("-", ""),
                pcId = pcId, pcHost = pcHost, negotiated = negotiated, ticket = ticket,
                sessionToken = SessionRegistry.issue(), desktopApp = desktopApp, desktopVersion = desktopVersion, host = host,
            )
            created = fresh
            gen = try {
                fresh.attach(peer, hello.id, pcRecv, resumed = false)
            } catch (e: Exception) {
                fresh.end("the hello couldn't be sent")
                throw e
            }
            if (gen == null) {
                fresh.end("the session couldn't start")
                return
            }
            sessions[fresh.id] = fresh
            fresh.outbox = CompanionOutbox.register(negotiated, fresh::send)
            session = fresh
            LogStore.log(LogLevel.INFO, "Connected to $desktopApp (protocol v$negotiated)")
        } else {
            LogStore.log(LogLevel.INFO, "Connected to $desktopApp again: the session carried on, nothing lost (protocol v$negotiated)")
        }
        val live = session!!
        peer.setTimeout(LinkBrain.PEER_SILENCE_MS.toInt()) // the PC says something every ~4 s; silence is a dead route
        liveControls.incrementAndGet()
        try {
            CompanionStateHolder.update(ServiceState.Connected(desktopApp, desktopVersion, negotiated, pcHost))
            CompanionStateHolder.setLink(CompanionStateHolder.Link(
                if (peer.tlsAddress != null) CompanionStateHolder.Route.Direct else CompanionStateHolder.Route.Adb,
                peer.tlsAddress,
                System.currentTimeMillis(),
            ))
            host.where()?.let { live.send(Envelope(type = MessageType.LINK_WHERE, payload = it)) }
            scope.launch(Dispatchers.IO) { announceAdb("control connection established") }
            controlLoop(peer.input, negotiated, live.ticket, pcHost, live.sessionToken, live::send, live)
        } finally {
            live.detach(gen!!)
            val remaining = liveControls.decrementAndGet()
            if (serverSocket != null && remaining == 0) {
                // The PC is probably right there on another route (a cable pulled, Wi-Fi moved):
                // look for it at once. "Connected" stays up for the linger, so a blip is invisible.
                ReconnectSignal.poke("the PC link ended")
                host.sessionDetached()
                scope.launch {
                    kotlinx.coroutines.delay(LINGER_MS)
                    if (liveControls.get() == 0 && serverSocket != null) {
                        CompanionStateHolder.update(ServiceState.Listening)
                        CompanionStateHolder.setLink(null)
                        LogStore.log(LogLevel.WARN, "Desktop disconnected")
                    }
                }
            } else if (remaining > 0) {
                LogStore.log(LogLevel.INFO, "One of two PC links closed; still connected on the other")
            }
        }
    }

    /** One v32 session: its numbering, its transport (or none, between routes), and its gate ticket. */
    private inner class LinkControl(
        val id: String,
        val pcId: String?,
        val pcHost: String?,
        val negotiated: Int,
        val ticket: Long,
        val sessionToken: String?,
        val desktopApp: String,
        val desktopVersion: String,
        private val host: LinkHost,
    ) {
        val link = LinkSession(id)
        private val lock = Any()
        private var output: DataOutputStream? = null
        private var closeTransport: (() -> Unit)? = null
        private var gen = 0
        private var ended = false
        private var expiry: Job? = null
        private var ticker: Job? = null
        var outbox: CompanionOutbox.Registration? = null

        val attached: Boolean get() = synchronized(lock) { output != null && !ended }

        /** Numbered, kept until acknowledged, and written if a transport is attached. */
        fun send(envelope: Envelope) = synchronized(lock) {
            val seq = link.stamp()
            val json = envelope.copy(seq = seq, ack = link.received).toJson()
            link.keep(seq, json)
            link.ackSent(link.received)
            write(json)
        }

        private fun write(json: String) {
            val out = output ?: return // between transports: kept, replayed on the resume
            try {
                Framing.write(out, json)
            } catch (_: IOException) {
                // The transport is dying: the frame is kept, and the read loop reports the loss.
            }
        }

        /**
         * Put [peer] under this session: hello reply first, then (resuming) every frame the PC
         * lacks. Returns the transport's generation, or null when it can't (ended, or frames gone).
         */
        fun attach(peer: Peer, helloId: String, pcReceived: Long, resumed: Boolean): Int? {
            val old: (() -> Unit)?
            val mine: Int
            synchronized(lock) {
                if (ended) return null
                val replay = if (resumed) link.replayAfter(pcReceived) ?: return null else emptyList()
                old = closeTransport
                output = peer.output
                closeTransport = peer.close
                mine = ++gen
                expiry?.cancel()
                expiry = null
                try {
                    Framing.write(peer.output, helloReply(helloId, resumed).toJson())
                    for (frame in replay) Framing.write(peer.output, frame)
                } catch (e: IOException) {
                    output = null
                    closeTransport = null
                    throw e
                }
            }
            if (old != null) runCatching { old() } // the PC moved the session: the old route goes
            startTicker()
            return mine
        }

        private fun helloReply(helloId: String, resumed: Boolean) = Envelope(
            type = MessageType.HELLO,
            replyTo = helloId,
            payload = buildJsonObject {
                put("app", "linc-android")
                put("appVersion", BuildConfig.VERSION_NAME)
                // Informational only (Provenance.kt); optional, older desktops ignore it.
                put("author", app.linc.android.Provenance.AUTHOR)
                put("v", negotiated)
                if (sessionToken != null) put("sessionToken", sessionToken)
                put("link", buildJsonObject {
                    put("session", id)
                    put("recv", link.received)
                    put("resumed", resumed)
                    host.phoneId()?.let { put("id", it) }
                    put("gen", host.secretGen(pcId))
                })
            },
        )

        /** The transport of generation [mine] ended: wait for a resume, or end after [LinkBrain.SESSION_KEEP_MS]. */
        fun detach(mine: Int) = synchronized(lock) {
            if (mine != gen || ended) return@synchronized
            output = null
            closeTransport = null
            expiry = scope.launch {
                kotlinx.coroutines.delay(LinkBrain.SESSION_KEEP_MS)
                end("the PC didn't come back within ${LinkBrain.SESSION_KEEP_MS / 1000} s")
            }
        }

        fun end(why: String) {
            val close: (() -> Unit)?
            synchronized(lock) {
                if (ended) return
                ended = true
                close = closeTransport
                output = null
                closeTransport = null
                expiry?.cancel()
                ticker?.cancel()
            }
            sessions.remove(id, this)
            outbox?.let { CompanionOutbox.release(it) }
            SessionRegistry.clear(sessionToken)
            gate.release(ticket)
            if (close != null) runCatching { close() }
            LogStore.log(LogLevel.INFO, "Session with $desktopApp ended ($why)")
        }

        /** Acks this side owes, sent bare when nothing outgoing carried them. */
        private fun startTicker() {
            if (ticker?.isActive == true) return
            ticker = scope.launch(Dispatchers.IO) {
                while (isActive) {
                    kotlinx.coroutines.delay(1_000)
                    val due = synchronized(lock) { output != null && link.ackDue(android.os.SystemClock.elapsedRealtime()) }
                    if (due) sendAck(echo = false)
                }
            }
        }

        private fun sendAck(echo: Boolean) = synchronized(lock) {
            val ack = link.received
            link.ackSent(ack)
            write(Envelope(type = MessageType.LINK_ACK, payload = buildJsonObject {
                put("ack", ack)
                put("echo", echo)
            }).toJson())
        }

        fun onAckFrame(message: Envelope) {
            synchronized(lock) {
                (message.payload["ack"] as? JsonPrimitive)?.longOrNull?.let { link.onAck(it) }
            }
            if ((message.payload["echo"] as? JsonPrimitive)?.booleanOrNull == true) sendAck(echo = false)
        }

        /** False for a replay this side already handled (never twice). */
        fun accept(message: Envelope): Boolean = synchronized(lock) {
            message.ack?.let { link.onAck(it) }
            val seq = message.seq ?: return@synchronized true
            link.accept(seq, android.os.SystemClock.elapsedRealtime()) != FrameArrival.DUPLICATE
        }

        /** `link.secret`: the PC's key for the LAN beacon, kept encrypted (LinkStore). */
        fun onSecret(message: Envelope) {
            val secret = (message.payload["secret"] as? JsonPrimitive)?.contentOrNull
                ?.let { runCatching { android.util.Base64.decode(it, android.util.Base64.NO_WRAP) }.getOrNull() }
            val gen = (message.payload["gen"] as? JsonPrimitive)?.intOrNull ?: 0
            val saved = pcId != null && secret != null && host.saveSecret(pcId, secret, gen)
            send(if (saved) {
                Envelope(type = MessageType.OK, replyTo = message.id)
            } else {
                Envelope(type = MessageType.ERROR, replyTo = message.id, payload = buildJsonObject {
                    put("code", ErrorCode.INTERNAL)
                    put("message", "The phone couldn't keep the PC's link key.")
                })
            })
            if (saved) LogStore.log(LogLevel.INFO, "Got $desktopApp's link key: the two can now find each other on any Wi-Fi")
        }
    }

    /**
     * The control conversation after the handshake, shared by every version: each message in,
     * its answer out through [send]. [link] is the v32 session (numbering, acks, the link
     * secret), or null below v32, where this is exactly the conversation it always was.
     */
    private fun controlLoop(
        input: DataInputStream,
        negotiated: Int,
        ticket: Long,
        pcHost: String?,
        sessionToken: String?,
        send: (Envelope) -> Unit,
        link: LinkControl?,
    ) {
        while (true) {
            val json = Framing.read(input) ?: return
            val message = try {
                parseEnvelope(json)
            } catch (_: SerializationException) {
                continue // unknown/garbled messages are ignored per spec
            }
            if (link != null) {
                // v32: numbering, acks and the keepalive echo; a replay already handled is dropped.
                if (message.type == MessageType.LINK_ACK) {
                    link.onAckFrame(message)
                    continue
                }
                if (!link.accept(message)) continue
                if (message.type == MessageType.LINK_SECRET) {
                    link.onSecret(message)
                    continue
                }
            }
            when (message.type) {
                MessageType.PING ->
                    send(Envelope(type = MessageType.PONG, replyTo = message.id))
                MessageType.STATUS_GET ->
                    send(Envelope(type = MessageType.STATUS, replyTo = message.id, payload = statusPayload(negotiated)))
                MessageType.CLIPBOARD_SET -> {
                    val text = (message.payload["text"] as? JsonPrimitive)?.contentOrNull
                    if (text != null) {
                        onSetClipboard(text)
                    }
                    send(Envelope(type = MessageType.OK, replyTo = message.id))
                }
                // v24 (D-083): the PC's Sync now. Android lets Linc read the clipboard only
                // while Linc is open, so this answers then and is silently a no-op otherwise.
                MessageType.CLIPBOARD_GET -> if (negotiated >= 24) {
                    onClipboardGet()
                }
                MessageType.NOTIFICATION_DISMISS -> {
                    val key = (message.payload["key"] as? JsonPrimitive)?.contentOrNull
                    if (key != null) {
                        onDismissNotification(key)
                    }
                    send(Envelope(type = MessageType.OK, replyTo = message.id))
                }
                MessageType.SUBSCRIBE, MessageType.UNSUBSCRIBE -> if (negotiated >= 6) {
                    val topic = (message.payload["topic"] as? JsonPrimitive)?.contentOrNull
                    if (topic != null && message.type == MessageType.SUBSCRIBE) {
                        CompanionOutbox.subscribe(topic)
                        when (topic) {
                            Topic.NOTIFICATIONS -> onDesktopConnected() // backlog push
                            Topic.MEDIA -> onMediaSubscribed() // current state push
                        }
                    } else if (topic != null) {
                        CompanionOutbox.unsubscribe(topic)
                    }
                    send(Envelope(type = MessageType.OK, replyTo = message.id))
                }
                MessageType.NOTIFICATION_ACTION -> if (negotiated >= 6) {
                    val key = (message.payload["key"] as? JsonPrimitive)?.contentOrNull
                    val index = (message.payload["index"] as? JsonPrimitive)?.intOrNull
                    val fired = key != null && index != null && onNotificationAction(key, index)
                    send(actionResult(fired, message.id))
                }
                MessageType.NOTIFICATION_REPLY -> if (negotiated >= 6) {
                    val key = (message.payload["key"] as? JsonPrimitive)?.contentOrNull
                    val index = (message.payload["index"] as? JsonPrimitive)?.intOrNull
                    val text = (message.payload["text"] as? JsonPrimitive)?.contentOrNull
                    val fired = key != null && index != null && text != null &&
                        onNotificationReply(key, index, text)
                    send(actionResult(fired, message.id))
                }
                MessageType.DEVICE_LOCATE -> if (negotiated >= 7) {
                    send(if (onLocate()) {
                        Envelope(type = MessageType.OK, replyTo = message.id)
                    } else {
                        Envelope(
                            type = MessageType.ERROR,
                            replyTo = message.id,
                            payload = buildJsonObject {
                                put("code", ErrorCode.INTERNAL)
                                put("message", "The phone couldn't start ringing.")
                            },
                        )
                    })
                }
                MessageType.DND_SET -> if (negotiated >= 7) {
                    val enabled = (message.payload["enabled"] as? JsonPrimitive)?.booleanOrNull
                    send(if (enabled != null && onSetDnd(enabled)) {
                        Envelope(type = MessageType.OK, replyTo = message.id)
                    } else {
                        Envelope(
                            type = MessageType.ERROR,
                            replyTo = message.id,
                            payload = buildJsonObject {
                                put("code", ErrorCode.NOT_GRANTED)
                                put("message", "Linc on the phone doesn't have Do Not Disturb access. " +
                                    "On the phone, open Settings > Notifications > Do Not Disturb access and allow Linc.")
                            },
                        )
                    })
                }
                MessageType.SOUND_SET -> if (negotiated >= 8) {
                    val mode = (message.payload["mode"] as? JsonPrimitive)?.contentOrNull
                    send(if (mode != null && onSetSound(mode)) {
                        Envelope(type = MessageType.OK, replyTo = message.id)
                    } else {
                        Envelope(
                            type = MessageType.ERROR,
                            replyTo = message.id,
                            payload = buildJsonObject {
                                put("code", ErrorCode.NOT_GRANTED)
                                put("message", "Linc on the phone doesn't have Do Not Disturb access " +
                                    "(changing the sound profile needs it). On the phone, open " +
                                    "Settings > Notifications > Do Not Disturb access and allow Linc.")
                            },
                        )
                    })
                }
                MessageType.MEDIA_CONTROL -> if (negotiated >= 6) {
                    val action = (message.payload["action"] as? JsonPrimitive)?.contentOrNull
                    val positionMs = (message.payload["positionMs"] as? JsonPrimitive)?.longOrNull
                    if (action != null) {
                        onMediaControl(action, positionMs)
                    }
                    send(Envelope(type = MessageType.OK, replyTo = message.id))
                }
                MessageType.TLS_EXCHANGE -> if (negotiated >= 9) {
                    val cert = (message.payload["cert"] as? JsonPrimitive)?.contentOrNull
                    val tlsPort = (message.payload["tlsPort"] as? JsonPrimitive)?.intOrNull ?: 0
                    val reversePort = (message.payload["reversePort"] as? JsonPrimitive)?.intOrNull ?: 0
                    val ownCert = if (cert != null) {
                        gate.exchanged(ticket, pcHost, cert, tlsPort, reversePort)
                        onTlsExchange(cert, tlsPort, reversePort)
                    } else null
                    send(if (ownCert != null) {
                        Envelope(
                            type = MessageType.TLS_EXCHANGE,
                            replyTo = message.id,
                            payload = buildJsonObject { put("cert", ownCert) },
                        )
                    } else {
                        Envelope(
                            type = MessageType.ERROR,
                            replyTo = message.id,
                            payload = buildJsonObject {
                                put("code", ErrorCode.INTERNAL)
                                put("message", "The phone couldn't prepare its direct-connection identity.")
                            },
                        )
                    })
                }
                // Off this loop: dialling back can take seconds over Wi-Fi (a probe race,
                // then TLS), and while it ran here nothing else was read — the PC's status
                // probes went unanswered and it dropped the whole link ("keeps
                // disconnecting"). The reply still carries replyTo, so order doesn't matter.
                MessageType.CHANNEL_OPEN -> if (negotiated >= 9) scope.launch(Dispatchers.IO) {
                    val channel = (message.payload["channel"] as? JsonPrimitive)?.intOrNull
                    val opened = channel != null && sessionToken != null &&
                        runCatching { onChannelOpen(channel, sessionToken) }.getOrDefault(false)
                    if (!opened) LogStore.log(LogLevel.WARN, "Channel $channel: couldn't dial back to the PC on any route")
                    runCatching { send(if (opened) {
                        Envelope(type = MessageType.OK, replyTo = message.id)
                    } else {
                        Envelope(
                            type = MessageType.ERROR,
                            replyTo = message.id,
                            payload = buildJsonObject {
                                put("code", ErrorCode.INTERNAL)
                                put("message", "The phone couldn't open that channel back to the PC.")
                            },
                        )
                    }) }
                }
                MessageType.PHOTOS_RECENT -> if (negotiated >= 10) {
                    val limit = (message.payload["limit"] as? JsonPrimitive)?.intOrNull ?: 20
                    send(Envelope(
                        type = MessageType.PHOTOS_RECENT,
                        replyTo = message.id,
                        payload = photosRecent(limit.coerceIn(1, 60)),
                    ))
                }
                MessageType.CONTINUE_URL -> if (negotiated >= 10) {
                    val url = (message.payload["url"] as? JsonPrimitive)?.contentOrNull
                    val opened = url != null && onContinueUrl(url)
                    send(if (opened) {
                        Envelope(type = MessageType.OK, replyTo = message.id)
                    } else {
                        Envelope(
                            type = MessageType.ERROR,
                            replyTo = message.id,
                            payload = buildJsonObject {
                                put("code", ErrorCode.INTERNAL)
                                put("message", "The phone couldn't open that link.")
                            },
                        )
                    })
                }
                MessageType.SYNC_CONFIG -> if (negotiated >= 11) {
                    onSyncConfig(message.payload)
                    send(Envelope(type = MessageType.OK, replyTo = message.id))
                }
                MessageType.SMS_LIST -> if (negotiated >= 11) {
                    val limit = (message.payload["limit"] as? JsonPrimitive)?.intOrNull ?: 50
                    val result = smsList(limit.coerceIn(1, 200))
                    send(if (result != null) {
                        Envelope(type = MessageType.SMS_LIST, replyTo = message.id, payload = result)
                    } else {
                        notGranted(message.id, "Linc on the phone needs the SMS permission. Enable the Messages lane on the phone.")
                    })
                }
                MessageType.SMS_SEND -> if (negotiated >= 11) {
                    val address = (message.payload["address"] as? JsonPrimitive)?.contentOrNull
                    val body = (message.payload["body"] as? JsonPrimitive)?.contentOrNull
                    val sent = address != null && body != null && smsSend(address, body)
                    send(if (sent) {
                        Envelope(type = MessageType.OK, replyTo = message.id)
                    } else {
                        notGranted(message.id, "Couldn't send that text. Enable the Messages lane (SMS permission) on the phone.")
                    })
                }
                MessageType.CALL_LOG -> if (negotiated >= 12) {
                    val limit = (message.payload["limit"] as? JsonPrimitive)?.intOrNull ?: 50
                    val result = callLog(limit.coerceIn(1, 200))
                    send(if (result != null) {
                        Envelope(type = MessageType.CALL_LOG, replyTo = message.id, payload = result)
                    } else {
                        notGranted(message.id, "Linc on the phone needs the Phone/Call-log permission. Enable the Calls lane on the phone.")
                    })
                }
                MessageType.CALL_DIAL -> if (negotiated >= 12) {
                    val number = (message.payload["number"] as? JsonPrimitive)?.contentOrNull
                    val ok = number != null && callDial(number)
                    send(if (ok) Envelope(type = MessageType.OK, replyTo = message.id)
                    else notGranted(message.id, "Couldn't place that call. Enable the Calls lane (Phone permission) on the phone."))
                }
                MessageType.CALL_DECLINE -> if (negotiated >= 12) {
                    val ok = callDecline()
                    send(if (ok) Envelope(type = MessageType.OK, replyTo = message.id)
                    else notGranted(message.id, "Couldn't decline the call. Enable the Calls lane on the phone."))
                }
                // v13 PC → phone pushes: unsolicited, no reply.
                MessageType.PC_MEDIA_STATE -> if (negotiated >= 13) {
                    onPcMediaState(message.payload)
                }
                MessageType.SHARE_INCOMING -> if (negotiated >= 13) {
                    val name = (message.payload["name"] as? JsonPrimitive)?.contentOrNull
                    val path = (message.payload["path"] as? JsonPrimitive)?.contentOrNull
                    if (name != null && path != null) onShareIncoming(name, path)
                }
                // v14: the PC says a text field gained/lost focus -> raise/lower the keyboard.
                MessageType.PC_TEXT_FOCUS -> if (negotiated >= 14) {
                    val focused = (message.payload["focused"] as? JsonPrimitive)?.booleanOrNull ?: false
                    MirrorReceiver.textFocus.value = focused
                }
                // v20 PC Remote side-channels (D-067): pointer, link health, ping echo, displays.
                MessageType.PC_MIRROR_CURSOR -> if (negotiated >= 20) MirrorSession.onCursor(message.payload)
                MessageType.PC_MIRROR_STATS -> if (negotiated >= 20) MirrorSession.onStats(message.payload)
                MessageType.PC_MIRROR_PONG -> if (negotiated >= 20) MirrorSession.onPong(message.payload)
                MessageType.PC_DISPLAYS -> if (negotiated >= 14) MirrorSession.onDisplays(message.payload)
                MessageType.PC_VOICEACCESS_STATE -> if (negotiated >= 21) PcVoiceAccess.onState(message.payload)
                MessageType.PC_TABLETMODE_STATE -> if (negotiated >= 22) PcTabletMode.onState(message.payload)
                MessageType.PC_SPEAKER_STATE -> if (negotiated >= 25) PcSpeaker.onState(message.payload)
                MessageType.AUDIO_EVERYWHERE_STATE -> if (negotiated >= 26) AudioEverywhere.onState(message.payload)
                MessageType.AUDIO_EVERYWHERE_TEST -> if (negotiated >= 26) AudioEverywhere.onTest(message.payload)
                // v27 (D-097): answers for the PC tools.
                MessageType.PC_LOOK_STATE -> if (negotiated >= 27) PcTools.onLook(message.payload)
                MessageType.PC_INFO -> if (negotiated >= 27) PcTools.onInfo(message.payload)
                MessageType.PC_APPS -> if (negotiated >= 27) PcTools.onApps(message.payload)
                MessageType.PC_FILES_LISTING -> if (negotiated >= 27) PcTools.onListing(message.payload)
                MessageType.PC_FILES_DONE -> if (negotiated >= 27) PcTools.onDone(message.payload)
                MessageType.PC_TERM_STATE -> if (negotiated >= 27) PcTools.onTermState(message.payload)
                // v28: the PC's outputs, its screen curtain, and the Device page reaching in.
                MessageType.PC_AUDIO_OUTPUTS -> if (negotiated >= 28) PcTools.onOutputs(message.payload)
                MessageType.PC_CURTAIN_STATE -> if (negotiated >= 28) PcTools.onCurtain(message.payload)
                MessageType.PC_BTAUDIO_STATE -> if (negotiated >= 29) PcTools.onBtAudio(message.payload)
                MessageType.SYNC_STATE -> if (negotiated >= 30) SyncRemote.onState(message.payload)
                MessageType.PHONE_SOUND_ASK -> if (negotiated >= 28) PhoneReach.soundAsk(message.payload)
                MessageType.PHONE_USAGE_GET -> if (negotiated >= 28) PhoneReach.usage()
                MessageType.PHONE_WALLPAPER_SET -> if (negotiated >= 28) PhoneReach.wallpaper(message.payload)
                // v15: the PC sets the phone's screen orientation (D-055). Bad payload →
                // internal; WRITE_SETTINGS not granted → not-granted (a plain-language route
                // to the Settings screen — same wording style as v7 dnd.set).
                MessageType.DISPLAY_ROTATION_SET -> if (negotiated >= 15) {
                    val mode = (message.payload["mode"] as? JsonPrimitive)?.contentOrNull
                    if (mode == null || mode !in setOf("auto", "portrait", "landscape")) {
                        send(Envelope(
                            type = MessageType.ERROR,
                            replyTo = message.id,
                            payload = buildJsonObject {
                                put("code", ErrorCode.INTERNAL)
                                put("message", "The rotation mode wasn't recognised. Choose auto, portrait, or landscape.")
                            },
                        ))
                    } else if (!onSetRotation(mode)) {
                        send(Envelope(
                            type = MessageType.ERROR,
                            replyTo = message.id,
                            payload = buildJsonObject {
                                put("code", ErrorCode.NOT_GRANTED)
                                put("message", "Linc on the phone can't change the screen orientation. " +
                                    "On the phone, open Settings > Apps > Special app access > " +
                                    "Modify system settings and allow Linc.")
                            },
                        ))
                    } else {
                        send(Envelope(type = MessageType.OK, replyTo = message.id))
                    }
                }
                // v15: the PC sets the phone's brightness (D-055). Bad payload (missing level
                // while auto is false, level out of 0–100) → internal; not-granted → appop miss.
                MessageType.DISPLAY_BRIGHTNESS_SET -> if (negotiated >= 15) {
                    val auto = (message.payload["auto"] as? JsonPrimitive)?.booleanOrNull
                    val level = (message.payload["level"] as? JsonPrimitive)?.intOrNull
                    val badPayload = auto == null ||
                        (auto == false && (level == null || level !in 0..100))
                    if (badPayload) {
                        send(Envelope(
                            type = MessageType.ERROR,
                            replyTo = message.id,
                            payload = buildJsonObject {
                                put("code", ErrorCode.INTERNAL)
                                put("message", "The brightness request wasn't recognised. " +
                                    "Send auto: true, or auto: false with a level between 0 and 100.")
                            },
                        ))
                    } else if (!onSetBrightness(auto!!, level)) {
                        send(Envelope(
                            type = MessageType.ERROR,
                            replyTo = message.id,
                            payload = buildJsonObject {
                                put("code", ErrorCode.NOT_GRANTED)
                                put("message", "Linc on the phone can't change the screen brightness. " +
                                    "On the phone, open Settings > Apps > Special app access > " +
                                    "Modify system settings and allow Linc.")
                            },
                        ))
                    } else {
                        send(Envelope(type = MessageType.OK, replyTo = message.id))
                    }
                }
                // v16: the desktop asks for the launchable-app inventory (D-058). No grant
                // is involved — querying the launcher intent needs no permission — so the
                // only reply shape is the apps envelope; a query failure degrades to an
                // empty list inside AppInventory rather than an error the UI can't act on.
                MessageType.APPS_GET -> if (negotiated >= 16) {
                    send(Envelope(
                        type = MessageType.APPS,
                        replyTo = message.id,
                        payload = appsGet(),
                    ))
                }
                // v17 (D-042): the desktop's reply to a pc.state.get / pc.control this phone
                // sent. PcControl is a plain singleton (like MirrorReceiver.textFocus above),
                // so no constructor callback is needed to route it to the Tools screen.
                MessageType.PC_STATE -> if (negotiated >= 17) {
                    PcControl.onState(message.payload)
                }
                // A pc.control reply is ok (silent success) or error (replyTo set). The
                // phone's own confirm dialog + wire confirm:true is the primary safety path
                // (PROTOCOL.md v17); this is only the backstop, so surface a failure in the
                // log rather than building UI for what should be rare.
                MessageType.ERROR -> if (negotiated >= 17 && message.replyTo != null) {
                    val code = (message.payload["code"] as? JsonPrimitive)?.contentOrNull
                    val text = (message.payload["message"] as? JsonPrimitive)?.contentOrNull
                    LogStore.log(LogLevel.WARN, "PC control request failed: ${code ?: "unknown"} — ${text ?: ""}")
                }
                // v18: the desktop asks this phone to (re-)arm adbd. M13c makes this really
                // arm — Settings.Global adb_wifi_enabled — when the one-time grant is held,
                // then re-runs detection and re-announces. Without the grant it degrades to
                // the M13b behaviour (detect and announce whatever is already up).
                //
                // `prefer` IS READ AND DELIBERATELY NOT OBEYED (PROTOCOL.md v18, M13e §1.2).
                // It is advisory, and it cannot be otherwise: arming legacy tcpip means
                // setting the `service.adb.tcp.port` system property, and `setprop` needs the
                // shell or root UID. The one-time WRITE_SECURE_SETTINGS grant this app can
                // hold covers Settings.Global/Secure only — enough for `adb_wifi_enabled` and
                // nothing beyond it. So an unprivileged app can turn wireless debugging ON but
                // cannot choose the port it lands on. Promotion to the fixed port is the
                // DESKTOP's job: it runs `adb tcpip 5555` itself once it holds any working ADB
                // connection. Do not "fix" this by trying to honour `prefer` here.
                MessageType.ADB_ARM -> if (negotiated >= 18) {
                    val prefer = (message.payload["prefer"] as? JsonPrimitive)?.contentOrNull
                    // LogStore has no DEBUG level and this codebase uses no android.util.Log,
                    // so this records the ask at the lowest level available, in plain language.
                    LogStore.log(
                        LogLevel.INFO,
                        "The PC asked to re-arm wireless debugging" +
                            if (prefer != null) " (it would prefer '$prefer'; the port is not this app's to choose)." else ".",
                    )
                    scope.launch(Dispatchers.IO) { onAdbArm("the PC asked for a re-arm") }
                }
                // The outcome of the desktop's connect attempt, so the phone stops retrying
                // and its log says what actually happened rather than nothing.
                MessageType.ADB_ACK -> if (negotiated >= 18) {
                    val ok = (message.payload["ok"] as? JsonPrimitive)?.booleanOrNull ?: false
                    val endpoint = (message.payload["endpoint"] as? JsonPrimitive)?.contentOrNull ?: ""
                    onAdbAck(ok, endpoint)
                }
                else -> Unit // unknown types are ignored per spec
            }
        }
    }

    /** Bulk channel (3): request/response for small binaries keyed by id (PROTOCOL.md v5). */
    private fun bulkLoop(input: DataInputStream, output: DataOutputStream) {
        while (true) {
            val requestJson = Framing.read(input) ?: return
            val request = try {
                ProtocolJson.parseToJsonElement(requestJson).jsonObject
            } catch (_: SerializationException) {
                continue
            }
            val kind = (request["kind"] as? JsonPrimitive)?.contentOrNull
            val id = (request["id"] as? JsonPrimitive)?.contentOrNull
            val bytes = if (kind != null && id != null) bulkFetch(kind, id) ?: ByteArray(0) else ByteArray(0)
            Framing.writeBytes(output, bytes) // 0-length ⇒ not found / unavailable
        }
    }

    private fun notGranted(replyTo: String, message: String): Envelope = Envelope(
        type = MessageType.ERROR,
        replyTo = replyTo,
        payload = buildJsonObject {
            put("code", ErrorCode.NOT_GRANTED)
            put("message", message)
        },
    )

    /** `ok` when the action/reply fired, `error` when the notification/action is gone. */
    private fun actionResult(fired: Boolean, replyTo: String): Envelope = if (fired) {
        Envelope(type = MessageType.OK, replyTo = replyTo)
    } else {
        Envelope(
            type = MessageType.ERROR,
            replyTo = replyTo,
            payload = buildJsonObject {
                put("code", ErrorCode.INTERNAL)
                put("message", "that notification (or its action) is no longer available")
            },
        )
    }

    private companion object {
        const val HANDSHAKE_TIMEOUT_MS = 5_000

        /** v32: how long "Connected" stays up after a session's transport dies (the PC's linger). */
        const val LINGER_MS = 12_000L
        const val CHANNEL_FILES = 2
        const val CHANNEL_BULK = 3
        const val CHANNEL_VIDEO = 5
        const val CHANNEL_AUDIO = 6
        const val CHANNEL_MIC = 7
        /** v27 (D-097): the phone's own sound, phone → desktop, for Play everywhere. */
        const val CHANNEL_PHONE_AUDIO = 8
        /** v27 (D-097): a terminal on the PC, both ways. */
        const val CHANNEL_TERMINAL = 9
        /** v27 (D-097): one PC file, either way. */
        const val CHANNEL_PC_FILES = 10
    }
}
