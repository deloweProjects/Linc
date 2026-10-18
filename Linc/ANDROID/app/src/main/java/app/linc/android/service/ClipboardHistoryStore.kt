package app.linc.android.service

import android.content.Context
import java.io.File
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.FlowPreview
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.debounce
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.flow.launchIn
import kotlinx.coroutines.flow.onEach
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

/** One clipboard entry (1.0.0.11, D-083). [sent] marks text sent from the composer. */
@Serializable
data class ClipItem(
    val id: Long,
    val text: String,
    val fromPhone: Boolean,
    val at: Long,
    val pinned: Boolean = false,
    val sent: Boolean = false,
)

/**
 * The rules of the clipboard history, pure so they are unit-tested (D-083): newest first, a repeat
 * of an existing text moves it to the top instead of duplicating it, pinned items are never
 * evicted by the size cap, and search is a case-insensitive substring match.
 */
object ClipHistory {
    const val DEFAULT_SIZE = 500
    val SIZES = listOf(100, 500, 2_000)

    fun add(items: List<ClipItem>, text: String, fromPhone: Boolean, at: Long, sent: Boolean, cap: Int): List<ClipItem> {
        if (text.isBlank()) return items
        val existing = items.firstOrNull { it.text == text }
        val item = ClipItem(
            id = existing?.id ?: ((items.maxOfOrNull { it.id } ?: 0L) + 1),
            text = text,
            fromPhone = fromPhone,
            at = at,
            pinned = existing?.pinned ?: false,
            sent = sent,
        )
        return trim(listOf(item) + items.filterNot { it.text == text }, cap)
    }

    /** Keep every pinned item and the newest unpinned ones, up to [cap] in all. */
    fun trim(items: List<ClipItem>, cap: Int): List<ClipItem> {
        if (items.size <= cap) return items
        val pinned = items.count { it.pinned }
        var room = (cap - pinned).coerceAtLeast(0)
        return items.filter { if (it.pinned) true else if (room > 0) { room--; true } else false }
    }

    fun pin(items: List<ClipItem>, id: Long, pinned: Boolean) = items.map { if (it.id == id) it.copy(pinned = pinned) else it }

    fun delete(items: List<ClipItem>, id: Long) = items.filterNot { it.id == id }

    /** Clear history; pinned items stay unless [includePinned]. */
    fun clear(items: List<ClipItem>, includePinned: Boolean = false) = if (includePinned) emptyList() else items.filter { it.pinned }

    fun search(items: List<ClipItem>, query: String): List<ClipItem> {
        val q = query.trim()
        return if (q.isEmpty()) items else items.filter { it.text.contains(q, ignoreCase = true) }
    }

    /** Pinned first (as one group), then by day, newest first. */
    fun groups(items: List<ClipItem>, zone: ZoneId = ZoneId.systemDefault()): List<Pair<String, List<ClipItem>>> {
        val today = LocalDate.now(zone)
        val pinned = items.filter { it.pinned }
        val byDay = items.filterNot { it.pinned }
            .groupBy { Instant.ofEpochMilli(it.at).atZone(zone).toLocalDate() }
            .toSortedMap(compareByDescending { it })
            .map { (day, list) -> dayLabel(day, today) to list.sortedByDescending { it.at } }
        return (if (pinned.isEmpty()) emptyList() else listOf("Pinned" to pinned)) + byDay
    }

    fun dayLabel(day: LocalDate, today: LocalDate): String = when (day) {
        today -> "Today"
        today.minusDays(1) -> "Yesterday"
        else -> if (day.year == today.year) "${day.dayOfMonth} ${month(day)}" else "${day.dayOfMonth} ${month(day)} ${day.year}"
    }

    private fun month(day: LocalDate) =
        day.month.getDisplayName(java.time.format.TextStyle.SHORT, java.util.Locale.getDefault())
}

/** Clipboard settings (Settings → Clipboard). Sync and history default on; 500 items. */
object ClipboardSettings {
    private const val PREFS = "linc"
    private fun prefs(c: Context) = c.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    fun syncEnabled(c: Context) = prefs(c).getBoolean("clip_sync", true)
    fun setSyncEnabled(c: Context, on: Boolean) = prefs(c).edit().putBoolean("clip_sync", on).apply()

    fun keepHistory(c: Context) = prefs(c).getBoolean("clip_keep_history", true)
    fun setKeepHistory(c: Context, on: Boolean) {
        prefs(c).edit().putBoolean("clip_keep_history", on).apply()
        if (!on) ClipboardHistoryStore.clearAll()
    }

    fun historySize(c: Context) = prefs(c).getInt("clip_history_size", ClipHistory.DEFAULT_SIZE)
    fun setHistorySize(c: Context, size: Int) {
        prefs(c).edit().putInt("clip_history_size", size).apply()
        ClipboardHistoryStore.retrim(size)
    }

    /** Android 13+ marks passwords and other secrets copied by apps as sensitive (D-083). */
    fun skipSensitive(c: Context) = prefs(c).getBoolean("clip_skip_sensitive", true)
    fun setSkipSensitive(c: Context, on: Boolean) = prefs(c).edit().putBoolean("clip_skip_sensitive", on).apply()
}

/**
 * The clipboard history (D-083): up to 500 items by default, kept in a small JSON file in Linc's
 * private storage so it survives restarts. No Room — the app has no database dependency and a
 * few hundred short texts don't need one. Text only; nothing is ever logged.
 */
object ClipboardHistoryStore {
    private const val FILE = "clipboard-history.json"
    private val json = Json { ignoreUnknownKeys = true }
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private val _items = MutableStateFlow<List<ClipItem>>(emptyList())
    val items: StateFlow<List<ClipItem>> = _items

    @Volatile private var file: File? = null
    @Volatile private var appContext: Context? = null

    /** Load once, then save (debounced) on every change. Safe to call from every entry point. */
    @OptIn(FlowPreview::class)
    @Synchronized
    fun init(context: Context) {
        if (file != null) return
        appContext = context.applicationContext
        val f = File(context.applicationContext.filesDir, FILE)
        file = f
        if (ClipboardSettings.keepHistory(context)) {
            _items.value = runCatching { json.decodeFromString<List<ClipItem>>(f.readText()) }.getOrDefault(emptyList())
        }
        _items.drop(1).debounce(400).onEach { save(it) }.launchIn(scope)
    }

    fun record(text: String, fromPhone: Boolean, sent: Boolean = false) {
        val context = appContext ?: return
        if (!ClipboardSettings.keepHistory(context)) return
        _items.value = ClipHistory.add(_items.value, text, fromPhone, System.currentTimeMillis(), sent, ClipboardSettings.historySize(context))
    }

    fun pin(id: Long, pinned: Boolean) { _items.value = ClipHistory.pin(_items.value, id, pinned) }
    fun delete(id: Long) { _items.value = ClipHistory.delete(_items.value, id) }
    fun clear() { _items.value = ClipHistory.clear(_items.value) }
    fun clearAll() { _items.value = emptyList() }
    fun retrim(cap: Int) { _items.value = ClipHistory.trim(_items.value, cap) }

    private fun save(items: List<ClipItem>) {
        val f = file ?: return
        runCatching {
            val tmp = File(f.parentFile, "$FILE.tmp")
            tmp.writeText(json.encodeToString(items))
            if (!tmp.renameTo(f)) { f.delete(); tmp.renameTo(f) }
        }.onFailure { LogStore.log(LogLevel.WARN, "Couldn't save the clipboard history: ${it.javaClass.simpleName}") }
    }
}
