package app.linc.android.service

import app.linc.android.protocol.Envelope
import java.util.concurrent.ConcurrentHashMap

/**
 * Lets app components (clipboard bridge, notification relay, media bridge, mirror control) send
 * unsolicited messages to the connected desktop. The socket server registers a sender with the
 * negotiated protocol version after each control handshake and releases it on disconnect; sends
 * requiring a newer version than negotiated are silently dropped.
 *
 * v6 adds topics (PROTOCOL.md pub/sub, D-019): on a link negotiated ≥ 6, a send tagged
 * with a topic is dropped unless the desktop has subscribed to it. On older links the
 * topic is ignored and unsolicited sends flow exactly as before.
 *
 * **Registrations are owned.** The desktop may hold several control connections at once
 * (ADB *and* Direct TLS — concurrent transports are a feature), and they die independently.
 * The old single-slot design let ANY dying connection null the slot, even when a different,
 * healthy connection owned it — which silently killed every unsolicited send (the phone's
 * `pc.mirror.start` among them) until the next handshake. Now registrations are a stack:
 * sends go to the newest live connection, and a death only removes itself, falling back to
 * whichever control link is still up.
 */
object CompanionOutbox {

    class Registration internal constructor(
        internal val version: Int,
        internal val send: (Envelope) -> Unit,
    ) {
        internal val topics = ConcurrentHashMap.newKeySet<String>()
    }

    /** Live control connections, oldest first. Sends go to the newest; death falls back. */
    private val registrations = java.util.concurrent.CopyOnWriteArrayList<Registration>()

    private fun current(): Registration? = registrations.lastOrNull()

    internal fun register(negotiatedVersion: Int, send: (Envelope) -> Unit): Registration {
        val registration = Registration(negotiatedVersion, send)
        registrations.add(registration)
        return registration
    }

    /** Removes [registration]; any other live control connection keeps working. */
    internal fun release(registration: Registration) {
        registrations.remove(registration)
    }

    internal fun subscribe(topic: String) {
        current()?.topics?.add(topic)
    }

    internal fun unsubscribe(topic: String) {
        current()?.topics?.remove(topic)
    }

    /** True when a desktop speaking at least [requiredVersion] is connected right now. */
    fun canSend(requiredVersion: Int): Boolean =
        (current()?.version ?: -1) >= requiredVersion

    /**
     * Sends if a desktop speaking at least [requiredVersion] is connected and — on a
     * v6+ link, when [topic] is given — subscribed to that topic.
     */
    fun trySend(envelope: Envelope, requiredVersion: Int, topic: String? = null) {
        val registration = current() ?: return
        if (registration.version < requiredVersion) {
            return
        }
        if (topic != null && registration.version >= 6 && topic !in registration.topics) {
            return
        }
        try {
            registration.send(envelope)
        } catch (_: Exception) {
            // The connection died mid-send; the server loop handles cleanup.
        }
    }
}
