package app.linc.android.service

import android.content.Context
import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import app.linc.android.protocol.Topic
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/**
 * Offline delivery for text, links and the clipboard (2026-10-31; the idea from NectarLink's
 * feature list, written from scratch for Linc). Shared or copied while no PC is connected, they
 * wait here and go to the PC in order, once each, when it next connects. Files already have their
 * own outbox folder ([ShareSender]); this is the small stuff.
 *
 * Kept to [CAP] items, a day at most ([MAX_AGE_MS]). Only the newest clipboard copy is kept: an
 * older one would only overwrite something newer. Saved in app-private storage so a restart of
 * Linc doesn't lose them.
 */
object PendingSends {

    @Serializable
    data class Item(val kind: String, val text: String, val at: Long)

    const val TEXT = "text"
    const val URL = "url"
    const val CLIP = "clip"
    private const val CAP = 20
    private const val MAX_AGE_MS = 24 * 60 * 60 * 1000L
    private const val PREFS = "linc.pending_sends"
    private const val KEY = "items"

    private val json = Json { ignoreUnknownKeys = true }
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val lock = Any()

    @Volatile private var appContext: Context? = null

    fun attach(context: Context) {
        appContext = context.applicationContext
    }

    /** Pure: the queue after adding [item] (a clip replaces any older clip; oldest drop past the cap). */
    fun added(queue: List<Item>, item: Item, now: Long): List<Item> =
        (fresh(queue, now).filterNot { item.kind == CLIP && it.kind == CLIP } + item).takeLast(CAP)

    /** Pure: what is still worth sending. */
    fun fresh(queue: List<Item>, now: Long): List<Item> = queue.filter { now - it.at <= MAX_AGE_MS }

    /** Queue [text] (kind [TEXT], [URL] or [CLIP]) for the PC. False when there's nowhere to keep it. */
    fun add(context: Context, kind: String, text: String): Boolean {
        if (text.isBlank()) return false
        attach(context)
        synchronized(lock) {
            save(context, added(load(context), Item(kind, text, System.currentTimeMillis()), System.currentTimeMillis()))
        }
        if (kind != CLIP) LogStore.log(LogLevel.INFO, "No PC connected: Linc sends that ${if (kind == URL) "link" else "text"} when your PC reconnects")
        return true
    }

    fun count(context: Context): Int = synchronized(lock) { fresh(load(context), System.currentTimeMillis()).size }

    /**
     * A PC just connected ([CompanionOutbox.register]): send what waited, oldest first. A short
     * pause first, so the PC has subscribed to the clipboard topic before the clip arrives.
     */
    fun flushSoon() {
        val context = appContext ?: AudioEverywhere.context() ?: return
        scope.launch {
            delay(2_000)
            val items = synchronized(lock) { fresh(load(context), System.currentTimeMillis()) }
            if (items.isEmpty()) return@launch
            var sent = 0
            for (item in items) {
                val ok = when (item.kind) {
                    CLIP -> CompanionOutbox.canSend(1).also {
                        if (it) CompanionOutbox.trySend(Envelope(
                            type = MessageType.CLIPBOARD_CHANGED,
                            payload = buildJsonObject { put("text", item.text) },
                        ), requiredVersion = 1, topic = Topic.CLIPBOARD)
                    }
                    else -> CompanionOutbox.canSend(10).also {
                        if (it) CompanionOutbox.trySend(Envelope(
                            type = MessageType.SHARE_ITEM,
                            payload = buildJsonObject {
                                put("kind", item.kind)
                                put("text", item.text)
                            },
                        ), requiredVersion = 10)
                    }
                }
                if (!ok) break // the PC went again; the rest wait, still in order
                sent++
                synchronized(lock) { save(context, load(context).filterNot { it == item }) }
            }
            if (sent > 0) LogStore.log(LogLevel.INFO, "Sent $sent item(s) to the PC that waited while it was away")
        }
    }

    private fun load(context: Context): List<Item> = runCatching {
        val raw = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(KEY, null) ?: return emptyList()
        json.decodeFromString(ListSerializer(Item.serializer()), raw)
    }.getOrDefault(emptyList())

    private fun save(context: Context, items: List<Item>) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putString(KEY, json.encodeToString(ListSerializer(Item.serializer()), items)).apply()
    }
}
