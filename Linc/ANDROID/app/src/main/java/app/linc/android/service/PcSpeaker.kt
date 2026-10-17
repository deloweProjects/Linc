package app.linc.android.service

import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/** Speaker mode as the PC reports it (v22 `pc.speaker.state`). */
data class SpeakerState(
    val on: Boolean = false,
    /** The PC has the virtual cable speaker mode routes its sound into. */
    val cable: Boolean = true,
    /** "no-cable", "denied" or "audio" when the last request failed. */
    val error: String? = null,
)

/**
 * The phone as the PC's speaker (v22, D-076). The PC moves its sound off its own speakers and
 * onto pc-audio channel 6, which [AudioReceiver] plays here, so it is heard once, from the
 * phone, rather than twice and out of step.
 */
object PcSpeaker {

    private val _state = MutableStateFlow(SpeakerState())
    val state: StateFlow<SpeakerState> = _state

    fun ready(): Boolean = CompanionOutbox.canSend(22)

    fun set(on: Boolean) {
        CompanionOutbox.trySend(
            Envelope(type = MessageType.PC_SPEAKER_SET, payload = buildJsonObject { put("on", on) }),
            requiredVersion = 22,
        )
    }

    fun onState(payload: JsonObject) {
        _state.value = SpeakerState(
            on = (payload["on"] as? JsonPrimitive)?.booleanOrNull ?: false,
            cable = (payload["cable"] as? JsonPrimitive)?.booleanOrNull ?: true,
            error = (payload["error"] as? JsonPrimitive)?.takeIf { it.isString }?.content,
        )
    }

    /** What to tell the user about [error], in plain words. */
    fun explain(error: String?): String? = when (error) {
        null -> null
        "no-cable" -> "The PC needs its virtual cable first. Turn on Voice Access below with " +
            "\"Use this phone as the microphone\": Linc sets the cable up, then this works too."
        "denied" -> "Windows didn't let Linc switch the PC's speakers. Try again, or pick " +
            "\"CABLE Input\" in the PC's Sound settings."
        else -> "The PC couldn't start sending its sound. Try again."
    }
}
