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
import kotlinx.serialization.json.longOrNull
import kotlinx.serialization.json.put

/** One of the PC's quick settings or shortcuts (`pc.quick.state`). */
data class PcQuickItem(
    val id: String,
    val label: String,
    /** "setting" (Windows' quick settings) or "shortcut" (Linc's own, as on the PC's bubble). */
    val group: String,
    /** "toggle" or "action". */
    val kind: String,
    val on: Boolean? = null,
    val available: Boolean = true,
    val note: String? = null,
)

/** One of the PC's own notifications (`pc.notif.posted`). */
data class PcNote(val id: String, val app: String, val title: String, val text: String, val time: Long)

/** The PC's notifications as the PC last described them (`pc.notifs`). */
data class PcNotes(
    /** PC Settings › "Send this PC's notifications to the phone". */
    val enabled: Boolean = true,
    /** allowed, denied, ask (Windows hasn't asked yet) or unavailable. */
    val access: String = "",
    val items: List<PcNote> = emptyList(),
    val error: String? = null,
    /** False until the PC has answered once. */
    val known: Boolean = false,
)

/**
 * Home › Control, reaching into the PC (2026-11-03; the owner: "on phone add a button under
 * Control in Home that gives settings like dark theme or light theme for PC; then below that a
 * section for notifications from the PC (not the ones from the phone), and a section for those
 * quick tiles from the PC and its quick settings too"). The PC owns the list of tiles and decides
 * what each id does; this side only shows what it was sent and sends back an id.
 *
 * No version bump: everything is sent at ≥ 27 (the v27 PC tools), and a PC that doesn't know
 * these messages ignores them — [PcQuick.items] then stays null and Home says the PC needs updating.
 */
object PcQuick {

    fun ready(): Boolean = CompanionOutbox.canSend(27)

    private fun send(type: String, payload: JsonObject = buildJsonObject { }) =
        CompanionOutbox.trySend(Envelope(type = type, payload = payload), requiredVersion = 27)

    // ---- tiles -----------------------------------------------------------------------------------

    private val _items = MutableStateFlow<List<PcQuickItem>?>(null)
    /** Null until the PC answers (an older PC never does). */
    val items: StateFlow<List<PcQuickItem>?> = _items

    private val _error = MutableStateFlow<String?>(null)
    /** What went wrong with the last tap, as the PC said it; cleared by the next answer. */
    val error: StateFlow<String?> = _error

    fun refresh() = send(MessageType.PC_QUICK_GET)

    /** A toggle set to [on] (flipped by the PC when null), or an action run. */
    fun set(id: String, on: Boolean? = null) = send(MessageType.PC_QUICK_SET, buildJsonObject {
        put("id", id)
        on?.let { put("on", it) }
    })

    fun onState(p: JsonObject) {
        _items.value = parseItems(p)
        _error.value = p.str("error")
    }

    internal fun parseItems(p: JsonObject): List<PcQuickItem> =
        (p["items"] as? JsonArray).orEmpty().mapNotNull { e ->
            val o = e as? JsonObject ?: return@mapNotNull null
            val id = o.str("id") ?: return@mapNotNull null
            PcQuickItem(
                id = id,
                label = o.str("label") ?: id,
                group = o.str("group") ?: "shortcut",
                kind = o.str("kind") ?: "action",
                on = (o["on"] as? JsonPrimitive)?.booleanOrNull,
                available = (o["available"] as? JsonPrimitive)?.booleanOrNull ?: true,
                note = o.str("note"),
            )
        }

    // ---- notifications -----------------------------------------------------------------------

    private val _notes = MutableStateFlow(PcNotes())
    val notes: StateFlow<PcNotes> = _notes

    /** Start (or stop) receiving the PC's notifications; [ask] lets the PC show Windows' prompt. */
    fun watch(on: Boolean, ask: Boolean = false) = send(MessageType.PC_NOTIFS_WATCH, buildJsonObject {
        put("on", on)
        if (ask) put("ask", true)
    })

    fun dismiss(id: String) {
        _notes.value = _notes.value.let { it.copy(items = it.items.filterNot { n -> n.id == id }) }
        send(MessageType.PC_NOTIF_DISMISS, buildJsonObject { put("id", id) })
    }

    fun dismissAll() {
        _notes.value = _notes.value.copy(items = emptyList())
        send(MessageType.PC_NOTIF_DISMISS, buildJsonObject { put("all", true) })
    }

    fun onNotes(p: JsonObject) {
        _notes.value = PcNotes(
            enabled = (p["enabled"] as? JsonPrimitive)?.booleanOrNull ?: true,
            access = p.str("access") ?: "unavailable",
            items = (p["items"] as? JsonArray).orEmpty().mapNotNull { (it as? JsonObject)?.let(::parseNote) }.sortedByDescending { it.time },
            error = p.str("error"),
            known = true,
        )
    }

    fun onPosted(p: JsonObject) {
        val note = parseNote(p) ?: return
        _notes.value = _notes.value.let { it.copy(items = (listOf(note) + it.items.filterNot { n -> n.id == note.id }).take(MAX_NOTES)) }
    }

    fun onRemoved(p: JsonObject) {
        val id = p.str("id") ?: return
        _notes.value = _notes.value.let { it.copy(items = it.items.filterNot { n -> n.id == id }) }
    }

    internal fun parseNote(o: JsonObject): PcNote? {
        val id = o.str("id") ?: return null
        return PcNote(id, o.str("app").orEmpty(), o.str("title").orEmpty(), o.str("text").orEmpty(),
            (o["time"] as? JsonPrimitive)?.longOrNull ?: 0L)
    }

    /** A new connection: forget the last PC's answers, so Home shows nothing stale. */
    fun reset() {
        _items.value = null
        _error.value = null
        _notes.value = PcNotes()
    }

    private const val MAX_NOTES = 50

    // ---- this phone's switch -----------------------------------------------------------------

    private const val PREFS = "linc_pc_quick"
    private const val KEY_NOTES = "pc_notes"

    /** Settings › "The PC's notifications" (on by default): with it off Home shows none and stops watching. */
    fun notesEnabled(context: Context): Boolean =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_NOTES, true)

    fun setNotesEnabled(context: Context, on: Boolean) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean(KEY_NOTES, on).apply()
        watch(on)
        if (!on) _notes.value = PcNotes()
    }

    private fun JsonObject.str(k: String) = (this[k] as? JsonPrimitive)?.takeIf { it.isString }?.contentOrNull
}
