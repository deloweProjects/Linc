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

    fun stop() {
        CompanionOutbox.trySend(
            Envelope(type = MessageType.PC_MIRROR_STOP, payload = buildJsonObject {}),
            requiredVersion = 14,
        )
    }

    /** Absolute pointer move to a point normalised 0..65535 against the mirrored video. */
    fun pointer(kind: String, x: Int, y: Int, mode: String) {
        CompanionOutbox.trySend(
            Envelope(
                type = MessageType.PC_INPUT,
                payload = buildJsonObject {
                    put("kind", kind)
                    put("x", x)
                    put("y", y)
                    put("mode", mode)
                },
            ),
            requiredVersion = 14,
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
