package app.linc.android.service

import android.content.Context
import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.put

/** One folder kept the same on the PC and this phone (the Folders lane, v30). */
data class FolderPair(
    val id: String,
    val name: String,
    val pcPath: String,
    val phonePath: String,
    val direction: String = BOTH,
    val enabled: Boolean = true,
) {
    companion object {
        const val BOTH = "both"
        const val TO_PHONE = "toPhone"
        const val TO_PC = "toPc"

        fun newId(): String = "pair-" + java.util.UUID.randomUUID().toString().replace("-", "").take(8)

        /** A folder's own name from its path, as the PC names a new pair. */
        fun nameFor(path: String): String =
            path.trimEnd('\\', '/').substringAfterLast('\\').substringAfterLast('/').ifBlank { path }
    }
}

/** The PC's sync setup as last told (sync.state). [loaded] is false until the PC has answered. */
data class SyncSetup(
    val loaded: Boolean = false,
    val lanes: Map<String, Boolean> = emptyMap(),
    val pairs: List<FolderPair> = emptyList(),
    val status: String = "",
    val photosPcPath: String = "",
)

/**
 * The phone's Sync page (v30): the PC runs the sync engine and owns its setup, so this asks for
 * it, edits it, and shows what the PC says back. Lanes also stay in [SyncStore] here, as
 * sync.config keeps them.
 */
object SyncRemote {

    private val _setup = MutableStateFlow(SyncSetup())
    val setup: StateFlow<SyncSetup> = _setup

    fun available(): Boolean = CompanionOutbox.canSend(30)

    private fun send(type: String, payload: JsonObject = buildJsonObject { }) =
        CompanionOutbox.trySend(Envelope(type = type, payload = payload), requiredVersion = 30)

    fun refresh() {
        if (available()) send(MessageType.SYNC_STATE_GET)
    }

    fun setLane(context: Context, lane: String, on: Boolean) {
        SyncStore.set(context, lane, on)
        _setup.value = _setup.value.copy(lanes = _setup.value.lanes + (lane to on))
        send(MessageType.SYNC_LANE_SET, buildJsonObject { put("lane", lane); put("on", on) })
    }

    /** Sends the whole list; shown at once, then replaced by what the PC kept. */
    fun setPairs(pairs: List<FolderPair>) {
        _setup.value = _setup.value.copy(pairs = pairs)
        send(MessageType.SYNC_PAIRS_SET, buildJsonObject {
            put("pairs", JsonArray(pairs.map { p ->
                buildJsonObject {
                    put("id", p.id)
                    put("name", p.name)
                    put("pcPath", p.pcPath)
                    put("phonePath", p.phonePath)
                    put("direction", p.direction)
                    put("enabled", p.enabled)
                }
            }))
        })
    }

    fun savePair(pair: FolderPair) {
        val pairs = _setup.value.pairs
        setPairs(if (pairs.any { it.id == pair.id }) pairs.map { if (it.id == pair.id) pair else it } else pairs + pair)
    }

    fun removePair(id: String) = setPairs(_setup.value.pairs.filterNot { it.id == id })

    fun syncNow() = send(MessageType.SYNC_NOW)

    fun onState(p: JsonObject) {
        val lanes = (p["lanes"] as? JsonObject).orEmpty()
            .mapNotNull { (k, v) -> (v as? JsonPrimitive)?.booleanOrNull?.let { k to it } }.toMap()
        val pairs = (p["pairs"] as? JsonArray).orEmpty().mapNotNull { e ->
            val o = runCatching { e.jsonObject }.getOrNull() ?: return@mapNotNull null
            FolderPair(
                id = o.str("id") ?: return@mapNotNull null,
                name = o.str("name") ?: "",
                pcPath = o.str("pcPath") ?: return@mapNotNull null,
                phonePath = o.str("phonePath") ?: return@mapNotNull null,
                direction = o.str("direction") ?: FolderPair.BOTH,
                enabled = (o["enabled"] as? JsonPrimitive)?.booleanOrNull ?: true,
            )
        }
        _setup.value = SyncSetup(true, lanes, pairs, p.str("status") ?: "", p.str("photosPcPath") ?: "")
    }

    private fun JsonObject.str(k: String) = (this[k] as? JsonPrimitive)?.takeIf { it.isString }?.contentOrNull
}
