package app.linc.android.protocol

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import java.util.UUID

// Companion protocol v0 — the wire contract with Linc Desktop.
// This package must stay free of android.* imports so it runs in plain JVM unit tests.
// Spec: docs/PROTOCOL.md. Wire-format changes require a version bump there first.

const val PROTOCOL_VERSION = 19

/** Lowest version this app can still speak (peers may negotiate down to it). */
const val PROTOCOL_MIN_VERSION = 0

/** Abstract Unix socket name; desktop reaches it via `adb forward tcp:0 localabstract:linc`. */
const val SOCKET_NAME = "linc"

object MessageType {
    const val HELLO = "hello"
    const val PING = "ping"
    const val PONG = "pong"
    const val STATUS_GET = "status.get"
    const val STATUS = "status"
    const val ERROR = "error"

    // v1
    const val OK = "ok"
    const val CLIPBOARD_SET = "clipboard.set"
    const val CLIPBOARD_CHANGED = "clipboard.changed"

    // v2
    const val NOTIFICATION_POSTED = "notification.posted"
    const val NOTIFICATION_REMOVED = "notification.removed"
    const val NOTIFICATION_DISMISS = "notification.dismiss"

    // v6
    const val SUBSCRIBE = "subscribe"
    const val UNSUBSCRIBE = "unsubscribe"
    const val NOTIFICATION_ACTION = "notification.action"
    const val NOTIFICATION_REPLY = "notification.reply"
    const val MEDIA_STATE = "media.state"
    const val MEDIA_CONTROL = "media.control"

    // v7
    const val DEVICE_LOCATE = "device.locate"
    const val DND_SET = "dnd.set"

    // v8
    const val SOUND_SET = "sound.set"

    // v9
    const val TLS_EXCHANGE = "tls.exchange"
    const val CHANNEL_OPEN = "channel.open"

    // v10
    const val PHOTOS_RECENT = "photos.recent"
    const val CONTINUE_URL = "continue.url"
    const val SHARE_ITEM = "share.item"

    // v11
    const val SYNC_CONFIG = "sync.config"
    const val SMS_LIST = "sms.list"
    const val SMS_SEND = "sms.send"
    const val SMS_RECEIVED = "sms.received"

    // v12
    const val CALL_LOG = "call.log"
    const val CALL_DIAL = "call.dial"
    const val CALL_DECLINE = "call.decline"
    const val CALL_INCOMING = "call.incoming"

    // v13 — PC → phone push (media + share) behind the phone's Home/Share screens
    const val PC_MEDIA_STATE = "pc.media.state"      // desktop → phone
    const val PC_MEDIA_CONTROL = "pc.media.control"  // phone → desktop
    const val SHARE_INCOMING = "share.incoming"      // desktop → phone

    // v14 — reverse mirror: the phone views and controls the PC (M05, D-029)
    const val PC_MIRROR_START = "pc.mirror.start"    // phone → desktop
    const val PC_MIRROR_STOP = "pc.mirror.stop"      // either direction
    const val PC_DISPLAYS_GET = "pc.displays.get"    // phone → desktop
    const val PC_DISPLAYS = "pc.displays"            // desktop → phone
    const val PC_MIRROR_KEYFRAME = "pc.mirror.keyframe" // phone → desktop: resend an IDR (v19)
    const val PC_INPUT = "pc.input"                  // phone → desktop
    const val PC_TEXT_FOCUS = "pc.textfocus"         // desktop → phone: a text field gained/lost focus

    // v15 — display control from the PC (M5c, D-055): the PC sends these, the phone
    // applies them through Settings.System with WRITE_SETTINGS (an appop, never adb — D-001).
    const val DISPLAY_ROTATION_SET = "display.rotation.set"     // desktop → phone
    const val DISPLAY_BRIGHTNESS_SET = "display.brightness.set"  // desktop → phone

    // v16 — installed-app inventory (M6c, D-058): launchable packages only. Pull on connect,
    // deliberately no push event; icons keep riding the v5 bulk kind `appIcon`.
    const val APPS_GET = "apps.get"                              // desktop → phone
    const val APPS = "apps"                                      // phone → desktop (replyTo set)

    // v17 — phone drives the PC: quick controls (M4b, D-042). Request/reply, unlike pc.input:
    // a dropped shutdown is not self-correcting the way a dropped mouse delta is.
    const val PC_CONTROL = "pc.control"          // phone → desktop (replyTo: ok/error)
    const val PC_STATE_GET = "pc.state.get"      // phone → desktop
    const val PC_STATE = "pc.state"              // desktop → phone (replyTo set)

    // v18 — instant ADB link-up over a hotspot (M13b). The desktop already holds a socket over
    // the link, so it knows WHERE this phone is; only the port is unknown, and this phone knows
    // it. All four carry `gen`, the monotonic counter that makes a stale announcement harmless.
    const val ADB_ANNOUNCE = "adb.announce"      // phone → desktop (unsolicited)
    const val ADB_DOWN = "adb.down"              // phone → desktop
    const val ADB_ARM = "adb.arm"                // desktop → phone
    const val ADB_ACK = "adb.ack"                // desktop → phone
}

object Topic {
    const val STATUS = "status"
    const val NOTIFICATIONS = "notifications"
    const val CLIPBOARD = "clipboard"
    const val MEDIA = "media"
}

object ErrorCode {
    const val VERSION_MISMATCH = "version-mismatch"
    const val BAD_FRAME = "bad-frame"
    const val INTERNAL = "internal"

    // v7: the request needs a phone-side permission/grant the user hasn't given.
    const val NOT_GRANTED = "not-granted"

    // v17: pc.control reply codes (PROTOCOL.md "Reply contract").
    const val NEEDS_CONFIRM = "needs-confirm"
    const val UNSUPPORTED = "unsupported"
    const val DENIED = "denied"
}

@Serializable
data class Envelope(
    val v: Int = PROTOCOL_VERSION,
    val type: String,
    val id: String = UUID.randomUUID().toString(),
    val replyTo: String? = null,
    val payload: JsonObject = JsonObject(emptyMap()),
)

val ProtocolJson = Json {
    ignoreUnknownKeys = true
    encodeDefaults = true
}

fun Envelope.toJson(): String = ProtocolJson.encodeToString(Envelope.serializer(), this)

fun parseEnvelope(json: String): Envelope = ProtocolJson.decodeFromString(Envelope.serializer(), json)
