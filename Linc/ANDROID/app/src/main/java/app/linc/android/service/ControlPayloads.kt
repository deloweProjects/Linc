package app.linc.android.service

import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/**
 * Pure `pc.control` payload builders (v17, M4b, D-042) — action name in, JSON payload out, so a
 * unit test can call them directly without a live connection. [PcControl] is the only caller;
 * kept separate so the confirm-flag rule ("destructive actions carry confirm: true, nothing
 * else does") is a single visible place, not folded into the send path.
 */
object ControlPayloads {

    fun lock(): JsonObject = buildJsonObject { put("action", "lock") }

    /** Not destructive (PROTOCOL.md v17) — no confirm flag, unlike shutdown/restart. */
    fun sleep(): JsonObject = buildJsonObject { put("action", "sleep") }

    /** `confirm: true` is mandatory here — the desktop rejects a destructive action without it. */
    fun shutdown(): JsonObject = buildJsonObject { put("action", "shutdown"); put("confirm", true) }

    fun restart(): JsonObject = buildJsonObject { put("action", "restart"); put("confirm", true) }

    fun volumeUp(step: Int? = null): JsonObject = buildJsonObject {
        put("action", "volume.up")
        if (step != null) put("step", step)
    }

    fun volumeDown(step: Int? = null): JsonObject = buildJsonObject {
        put("action", "volume.down")
        if (step != null) put("step", step)
    }

    fun volumeSet(level: Int): JsonObject = buildJsonObject {
        put("action", "volume.set")
        put("level", level)
    }

    /** `on` absent = toggle; present = set explicitly (PROTOCOL.md v17). */
    fun volumeMute(on: Boolean? = null): JsonObject = buildJsonObject {
        put("action", "volume.mute")
        if (on != null) put("on", on)
    }

    fun brightnessSet(level: Int): JsonObject = buildJsonObject {
        put("action", "brightness.set")
        put("level", level)
    }
}
