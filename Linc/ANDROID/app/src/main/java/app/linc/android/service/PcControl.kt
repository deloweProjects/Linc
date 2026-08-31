package app.linc.android.service

import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.intOrNull

/**
 * The phone's outgoing quick controls for the PC (v17, M4b, D-042): lock/sleep/shutdown/restart,
 * volume and PC display brightness, riding the same [CompanionOutbox] control-connection path as
 * [MirrorControl]. Unlike `pc.input`, every send here is followed by a fresh `pc.state.get` —
 * PROTOCOL.md deliberately has no unsolicited state push, so the phone re-asks after anything
 * that might have changed the PC's controllable state.
 */
object PcControl {

    /** Mirrors the `pc.state` reply payload (PROTOCOL.md v17). Null until the first reply. */
    data class State(
        val volume: Int,
        val muted: Boolean,
        val brightness: Int?,
        val canBrightness: Boolean,
        val canSleep: Boolean,
        val canShutdown: Boolean,
    )

    private val _state = MutableStateFlow<State?>(null)
    val state: StateFlow<State?> = _state

    /** True when a v17 desktop is connected and quick controls can be sent right now. */
    fun ready(): Boolean = CompanionOutbox.canSend(17)

    /** Called by [SocketServer] when a `pc.state` reply arrives. */
    fun onState(payload: JsonObject) {
        _state.value = State(
            volume = (payload["volume"] as? JsonPrimitive)?.intOrNull ?: 0,
            muted = (payload["muted"] as? JsonPrimitive)?.booleanOrNull ?: false,
            brightness = (payload["brightness"] as? JsonPrimitive)?.intOrNull,
            canBrightness = (payload["canBrightness"] as? JsonPrimitive)?.booleanOrNull ?: false,
            canSleep = (payload["canSleep"] as? JsonPrimitive)?.booleanOrNull ?: false,
            canShutdown = (payload["canShutdown"] as? JsonPrimitive)?.booleanOrNull ?: false,
        )
    }

    /** Ask the PC for its current controllable state (Tools opening, or after any send below). */
    fun requestState() {
        CompanionOutbox.trySend(
            Envelope(type = MessageType.PC_STATE_GET, payload = buildJsonObject {}),
            requiredVersion = 17,
        )
    }

    private fun send(payload: JsonObject) {
        CompanionOutbox.trySend(Envelope(type = MessageType.PC_CONTROL, payload = payload), requiredVersion = 17)
        // No unsolicited push exists for PC state (PROTOCOL.md v17) — the phone is the only one
        // that knows it just asked for something that might have changed it.
        requestState()
    }

    fun lock() = send(ControlPayloads.lock())
    fun sleep() = send(ControlPayloads.sleep())

    /** Caller must already have shown its own confirmation dialog (PROTOCOL.md v17: both, not
     * either — a UI-only gate is not the contract). */
    fun shutdown() = send(ControlPayloads.shutdown())
    fun restart() = send(ControlPayloads.restart())

    fun volumeUp(step: Int? = null) = send(ControlPayloads.volumeUp(step))
    fun volumeDown(step: Int? = null) = send(ControlPayloads.volumeDown(step))
    fun volumeSet(level: Int) = send(ControlPayloads.volumeSet(level))
    fun volumeMute(on: Boolean? = null) = send(ControlPayloads.volumeMute(on))
    fun brightnessSet(level: Int) = send(ControlPayloads.brightnessSet(level))
}
