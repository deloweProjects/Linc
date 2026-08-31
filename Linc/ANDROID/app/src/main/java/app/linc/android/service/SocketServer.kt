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
) {
    /** One connection, transport-agnostic: LocalSocket over ADB or SSLSocket over LAN. */
    class Peer(
        val input: DataInputStream,
        val output: DataOutputStream,
        val setTimeout: (Int) -> Unit,
    )

    private var serverSocket: LocalServerSocket? = null
    private var acceptJob: Job? = null

    fun start() {
        if (acceptJob != null) return
        acceptJob = scope.launch(Dispatchers.IO) {
            val server = try {
                LocalServerSocket(SOCKET_NAME)
            } catch (_: IOException) {
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

    /** Serves a Direct-TLS connection the presence loop dialed (v9). Blocks until it ends. */
    fun serveExternal(socket: Socket) {
        val peer = Peer(
            DataInputStream(socket.getInputStream()),
            DataOutputStream(socket.getOutputStream()),
            { millis -> socket.soTimeout = millis },
        )
        try {
            dispatch(peer)
        } catch (_: IOException) {
        } catch (_: SerializationException) {
        } catch (_: IllegalArgumentException) {
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
        }
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
        runCatching { serverSocket?.close() }
        serverSocket = null
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
        // Speak the highest version both sides support.
        val negotiated = minOf(maxV, PROTOCOL_VERSION)
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

        // v5: hand the desktop a session token so it can open channel connections.
        val sessionToken = if (negotiated >= 5) SessionRegistry.issue() else null
        var outboxRegistration: CompanionOutbox.Registration? = null
        send(Envelope(
            type = MessageType.HELLO,
            replyTo = hello.id,
            payload = buildJsonObject {
                put("app", "linc-android")
                put("appVersion", BuildConfig.VERSION_NAME)
                put("v", negotiated)
                if (sessionToken != null) put("sessionToken", sessionToken)
            },
        ))

        try {
            val desktopApp = (hello.payload["app"] as? JsonPrimitive)?.contentOrNull ?: "desktop"
            val desktopVersion = (hello.payload["appVersion"] as? JsonPrimitive)?.contentOrNull ?: "?"
            CompanionStateHolder.update(ServiceState.Connected(desktopApp, desktopVersion, negotiated))
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

            while (true) {
                val json = Framing.read(peer.input) ?: return
                val message = try {
                    parseEnvelope(json)
                } catch (_: SerializationException) {
                    continue // unknown/garbled messages are ignored per spec
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
                        val ownCert = if (cert != null) onTlsExchange(cert, tlsPort, reversePort) else null
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
                    MessageType.CHANNEL_OPEN -> if (negotiated >= 9) {
                        val channel = (message.payload["channel"] as? JsonPrimitive)?.intOrNull
                        val opened = channel != null && sessionToken != null &&
                            onChannelOpen(channel, sessionToken)
                        send(if (opened) {
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
                        })
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
        } finally {
            // Release only OUR registration — with concurrent transports another control
            // connection may have registered after us, and nulling its slot silently kills
            // every unsolicited send until the next handshake.
            outboxRegistration?.let { CompanionOutbox.release(it) }
            SessionRegistry.clear(sessionToken)
            // Only report a disconnect when the peer left, not when we're shutting down.
            if (serverSocket != null) {
                CompanionStateHolder.update(ServiceState.Listening)
                LogStore.log(LogLevel.WARN, "Desktop disconnected")
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
        const val CHANNEL_FILES = 2
        const val CHANNEL_BULK = 3
        const val CHANNEL_VIDEO = 5
    }
}
