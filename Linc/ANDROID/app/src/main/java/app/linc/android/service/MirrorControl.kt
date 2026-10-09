package app.linc.android.service

import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/**
 * The phone's outgoing control for the reverse mirror (v14, M05, D-029): ask the PC to start or
 * stop streaming a display, and forward touches/keys as `pc.input`. All fire-and-forget, like
 * the other pc.* controls — the video itself arrives on channel 5, not in these messages.
 */
object MirrorControl {

    /** True when a v14 desktop is connected and mirror messages can flow right now. */
    fun ready(): Boolean = CompanionOutbox.canSend(14)

    fun start(displayId: Int = 0, maxSize: Int = 0, fps: Int = 0, bitrate: Int = 0) {
        if (!ready()) {
            // A silently dropped start looks like "Connecting..." forever — say why instead.
            LogStore.log(LogLevel.WARN, "PC mirror: can't ask the PC to stream (no v14 desktop connected)")
            return
        }
        CompanionOutbox.trySend(
            Envelope(
                type = MessageType.PC_MIRROR_START,
                payload = buildJsonObject {
                    put("displayId", displayId)
                    if (maxSize > 0) put("maxSize", maxSize)
                    if (fps > 0) put("fps", fps)         // v14: honoured by the desktop encoder
                    if (bitrate > 0) put("bitrate", bitrate)
                },
            ),
            requiredVersion = 14,
        )
    }

    /** A physical/IME key as a Windows virtual-key code (backspace, enter, arrows...). */
    fun key(virtualKey: Int, down: Boolean) {
        CompanionOutbox.trySend(
            Envelope(
                type = MessageType.PC_INPUT,
                payload = buildJsonObject {
                    put("kind", "key")
                    put("keyCode", virtualKey)
                    put("down", down)
                },
            ),
            requiredVersion = 14,
        )
    }

    /**
     * Ask the PC to encode an IDR now (v19). Sent when the decoder has lost sync — after a
     * dropped packet or a decoder restart — so the picture comes back in a frame or two
     * instead of waiting out the PC's two-second GOP.
     */
    fun keyframe() {
        CompanionOutbox.trySend(
            Envelope(type = MessageType.PC_MIRROR_KEYFRAME, payload = buildJsonObject {}),
            requiredVersion = 19,
        )
    }

    fun stop() {
        CompanionOutbox.trySend(
            Envelope(type = MessageType.PC_MIRROR_STOP, payload = buildJsonObject {}),
            requiredVersion = 14,
        )
    }

    /**
     * Absolute pointer move to a point normalised 0..65535 against the mirrored video. A
     * down/up carries [button] ("left", "right", "middle"); a non-left button needs a v20
     * desktop — an older one would turn a right click into a left one, so it is not sent.
     */
    fun pointer(kind: String, x: Int, y: Int, mode: String, button: String = "left") {
        if (button != "left" && !CompanionOutbox.canSend(20)) return
        CompanionOutbox.trySend(
            Envelope(
                type = MessageType.PC_INPUT,
                payload = buildJsonObject {
                    put("kind", kind)
                    put("x", x)
                    put("y", y)
                    put("mode", mode)
                    if (button != "left") put("button", button)
                },
            ),
            requiredVersion = 14,
        )
    }

    /** True when the desktop speaks v20: cursor, stats, right click, smooth scroll, audio. */
    fun v20(): Boolean = CompanionOutbox.canSend(20)

    /** Smooth scroll in raw wheel units (120 = one notch), v20; older desktops get notches. */
    fun scrollPrecise(dx: Int, dy: Int) {
        if (!v20()) {
            if (dy / 120 != 0 || dx / 120 != 0) scroll(dx / 120, dy / 120)
            return
        }
        CompanionOutbox.trySend(
            Envelope(
                type = MessageType.PC_INPUT,
                payload = buildJsonObject {
                    put("kind", "scroll")
                    put("dx", dx)
                    put("dy", dy)
                    put("precise", true)
                },
            ),
            requiredVersion = 20,
        )
    }

    /** Round-trip probe for the connection bar; the PC echoes it as `pc.mirror.pong` (v20). */
    fun ping(nowMs: Long) {
        CompanionOutbox.trySend(
            Envelope(type = MessageType.PC_MIRROR_PING, payload = buildJsonObject { put("t", nowMs) }),
            requiredVersion = 20,
        )
    }

    /** Ask for the PC's display list (answered with `pc.displays`). */
    fun requestDisplays() {
        CompanionOutbox.trySend(
            Envelope(type = MessageType.PC_DISPLAYS_GET, payload = buildJsonObject {}),
            requiredVersion = 14,
        )
    }

    /** Start/stop playing the PC's sound on the phone (v20, channel 6). */
    fun audio(on: Boolean) {
        CompanionOutbox.trySend(
            Envelope(
                type = if (on) MessageType.PC_AUDIO_START else MessageType.PC_AUDIO_STOP,
                payload = buildJsonObject {},
            ),
            requiredVersion = 20,
        )
    }

    /** Relative pointer move by a pixel delta on the mirrored display (trackpad mode). */
    fun pointerRelative(kind: String, dx: Int, dy: Int) {
        CompanionOutbox.trySend(
            Envelope(
                type = MessageType.PC_INPUT,
                payload = buildJsonObject {
                    put("kind", kind)
                    put("dx", dx)
                    put("dy", dy)
                    put("mode", "trackpad")
                },
            ),
            requiredVersion = 14,
        )
    }

    /** A trackpad tap or button press: a click at the PC's current cursor position, no move. */
    fun click(down: Boolean) {
        CompanionOutbox.trySend(
            Envelope(
                type = MessageType.PC_INPUT,
                payload = buildJsonObject {
                    put("kind", if (down) "down" else "up")
                    put("mode", "trackpad")
                },
            ),
            requiredVersion = 14,
        )
    }

    fun scroll(dx: Int, dy: Int) {
        CompanionOutbox.trySend(
            Envelope(
                type = MessageType.PC_INPUT,
                payload = buildJsonObject {
                    put("kind", "scroll")
                    put("dx", dx)
                    put("dy", dy)
                },
            ),
            requiredVersion = 14,
        )
    }

    fun text(text: String) {
        if (text.isEmpty()) return
        CompanionOutbox.trySend(
            Envelope(
                type = MessageType.PC_INPUT,
                payload = buildJsonObject {
                    put("kind", "text")
                    put("text", text)
                },
            ),
            requiredVersion = 14,
        )
    }
}
