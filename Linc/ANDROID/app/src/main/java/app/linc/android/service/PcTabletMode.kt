package app.linc.android.service

import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.put

/** What the PC reports about Windows tablet mode (v22 `pc.tabletmode.state`). */
data class TabletModeState(
    val known: Boolean = false,
    val on: Boolean = false,
    /** The PC has a touch screen; without one Windows 11 usually won't change its layout. */
    val touch: Boolean = false,
    /** "11" or "10". On 11 every switch needs a Windows permission prompt on the PC. */
    val windows: String = "",
    val error: String? = null,
)

/** Windows tablet mode from PC Remote's bubble (v22, D-074). */
object PcTabletMode {

    private val _state = MutableStateFlow(TabletModeState())
    val state: StateFlow<TabletModeState> = _state

    fun ready(): Boolean = CompanionOutbox.canSend(22)

    fun set(on: Boolean) {
        CompanionOutbox.trySend(
            Envelope(type = MessageType.PC_TABLETMODE_SET, payload = buildJsonObject { put("on", on) }),
            requiredVersion = 22,
        )
    }

    fun refresh() {
        CompanionOutbox.trySend(Envelope(type = MessageType.PC_TABLETMODE_GET, payload = buildJsonObject {}), requiredVersion = 22)
    }

    fun onState(payload: JsonObject) {
        fun bool(key: String) = (payload[key] as? JsonPrimitive)?.booleanOrNull ?: false
        _state.value = TabletModeState(
            known = true,
            on = bool("on"),
            touch = bool("touch"),
            windows = (payload["windows"] as? JsonPrimitive)?.contentOrNull ?: "",
            error = (payload["error"] as? JsonPrimitive)?.contentOrNull,
        )
    }
}
