package app.linc.android.service

import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import androidx.core.app.NotificationCompat
import app.linc.android.R
import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import java.io.File
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.put

/** One file moving between the PC and this phone right now, as the PC reports it (`share.progress`). */
data class LiveShare(
    val id: String,
    val name: String,
    /** True: PC → phone. False: phone → PC (the PC pulls it from Linc's outbox). */
    val toPhone: Boolean,
    val percent: Int,
    val state: State,
    val reason: String? = null,
) {
    enum class State { Moving, Cancelling, Done, Failed, Cancelled }

    val finished: Boolean get() = state == State.Done || state == State.Failed || state == State.Cancelled
}

/**
 * Every file moving between the PC and this phone, live, with a way to stop it (owner, 2026-11-01:
 * "no control when sharing whatsoever, no progress, no cancel, no anything").
 *
 * The PC does the copying both ways (it pushes to the phone and pulls from Linc's outbox), so only
 * it knows how far a file has got: it reports every step as `share.progress`, and this keeps them
 * for the Share tab's "Moving now" card and shows one notification per file with a progress bar and
 * a Cancel button. Cancel sends `share.cancel`; the PC stops, deletes the half-copied file and
 * reports `cancelled`. A file that reached the PC (or was cancelled) is cleared from the outbox here.
 */
object ShareLive {

    private const val OUTBOX = "/sdcard/Download/Linc/outbox"
    private const val CHANNEL_ID = "linc.share.progress" // ShareProgress creates it
    const val ACTION_CANCEL = "app.linc.android.SHARE_CANCEL"
    const val EXTRA_ID = "id"
    const val EXTRA_LOCAL = "local"

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val _items = MutableStateFlow<List<LiveShare>>(emptyList())
    val items: StateFlow<List<LiveShare>> = _items
    private val lastPosted = java.util.concurrent.ConcurrentHashMap<String, Int>()

    /** The PC's `share.progress`. */
    fun onProgress(context: Context, payload: JsonObject) {
        val id = (payload["id"] as? JsonPrimitive)?.contentOrNull ?: return
        val name = (payload["name"] as? JsonPrimitive)?.contentOrNull ?: return
        val toPhone = (payload["toPhone"] as? JsonPrimitive)?.booleanOrNull ?: true
        val percent = ((payload["percent"] as? JsonPrimitive)?.intOrNull ?: 0).coerceIn(0, 100)
        val state = when ((payload["state"] as? JsonPrimitive)?.contentOrNull) {
            "done" -> LiveShare.State.Done
            "failed" -> LiveShare.State.Failed
            "cancelled" -> LiveShare.State.Cancelled
            else -> LiveShare.State.Moving
        }
        val reason = (payload["reason"] as? JsonPrimitive)?.contentOrNull
        val before = _items.value.firstOrNull { it.id == id }
        // A Cancel already tapped stays "Stopping…" until the PC confirms.
        val shown = if (before?.state == LiveShare.State.Cancelling && state == LiveShare.State.Moving) before.copy(percent = percent)
            else LiveShare(id, name, toPhone, percent, state, reason)
        upsert(shown)
        notify(context.applicationContext, shown)
        if (!toPhone) ShareStore.onPcProgress(name, shown)
        if (shown.finished) {
            if (!toPhone && (state == LiveShare.State.Done || state == LiveShare.State.Cancelled)) {
                // Fetched or cancelled: nothing left for the PC to collect.
                runCatching { File(OUTBOX, name).delete() }
            }
            val finishedId = id
            scope.launch {
                delay(6_000) // the row says how it ended, then leaves
                _items.value = _items.value.filterNot { it.id == finishedId && it.finished }
                lastPosted.remove(finishedId)
            }
        }
    }

    /** Stop a file the PC is moving (the row's or the notification's Cancel). */
    fun cancel(context: Context, id: String) {
        val item = _items.value.firstOrNull { it.id == id } ?: return
        if (item.finished) return
        val sent = CompanionOutbox.canSend(13)
        if (sent) CompanionOutbox.trySend(
            Envelope(type = MessageType.SHARE_CANCEL, payload = buildJsonObject { put("id", id) }),
            13,
        )
        val now = if (sent) item.copy(state = LiveShare.State.Cancelling) else item.copy(state = LiveShare.State.Failed, reason = "Your PC isn't connected")
        upsert(now)
        notify(context.applicationContext, now)
        LogStore.log(LogLevel.INFO, if (sent) "Asked the PC to stop ${item.name}" else "Couldn't stop ${item.name}: the PC isn't connected")
    }

    /** The phone → PC transfer the PC is fetching under [name], if any (to cancel from the phone's own row). */
    fun fetching(name: String): LiveShare? = _items.value.firstOrNull { !it.toPhone && it.name == name && !it.finished }

    private fun upsert(item: LiveShare) {
        _items.value = listOf(item) + _items.value.filterNot { it.id == item.id }
    }

    private fun notificationId(id: String) = 6_000 + (id.hashCode() and 0x0FFF)

    private fun notify(context: Context, item: LiveShare) {
        val manager = context.getSystemService(NotificationManager::class.java) ?: return
        ShareProgress(context, 1) // makes sure the channel exists
        val nid = notificationId(item.id)
        if (item.toPhone && item.state == LiveShare.State.Done) {
            manager.cancel(nid) // "Received from your PC" (tap to open) follows from share.incoming
            return
        }
        // Throttle: whole steps of 2 % while moving.
        if (item.state == LiveShare.State.Moving) {
            val last = lastPosted[item.id]
            if (last != null && item.percent - last in 0..1 && item.percent < 100) return
            lastPosted[item.id] = item.percent
        }
        val builder = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_linc)
            .setOnlyAlertOnce(true)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_PROGRESS)
            .setSubText("Linc")
            .setContentText(item.name)
        when (item.state) {
            LiveShare.State.Moving, LiveShare.State.Cancelling -> {
                builder.setContentTitle(
                    if (item.state == LiveShare.State.Cancelling) "Stopping…"
                    else if (item.toPhone) "Receiving from your PC · ${item.percent}%" else "Sending to your PC · ${item.percent}%"
                )
                    .setProgress(100, item.percent, false)
                    .setOngoing(true)
                if (item.state == LiveShare.State.Moving) {
                    builder.addAction(0, "Cancel", cancelIntent(context, item.id, local = false))
                }
            }
            else -> {
                builder.setContentTitle(
                    when (item.state) {
                        LiveShare.State.Done -> "On your PC"
                        LiveShare.State.Cancelled -> "Cancelled"
                        else -> if (item.toPhone) "Couldn't receive from your PC" else "Couldn't send to your PC"
                    }
                )
                    .setContentText(item.reason?.let { "${item.name}: $it" } ?: item.name)
                    .setStyle(NotificationCompat.BigTextStyle().bigText(item.reason?.let { "${item.name}\n$it" } ?: item.name))
                    .setOngoing(false)
                    .setAutoCancel(true)
                    .setTimeoutAfter(if (item.state == LiveShare.State.Failed) 15_000L else 4_000L)
            }
        }
        runCatching { manager.notify(nid, builder.build()) }
    }

    /** The Cancel button: [local] ids are this phone's own copies into the outbox (ShareStore). */
    fun cancelIntent(context: Context, id: String, local: Boolean): PendingIntent =
        PendingIntent.getBroadcast(
            context, (id + local).hashCode(),
            Intent(context, ShareCancelReceiver::class.java).setAction(ACTION_CANCEL).putExtra(EXTRA_ID, id).putExtra(EXTRA_LOCAL, local),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
}

/** A notification's Cancel. */
class ShareCancelReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val id = intent.getStringExtra(ShareLive.EXTRA_ID) ?: return
        if (intent.getBooleanExtra(ShareLive.EXTRA_LOCAL, false)) {
            id.split(',').mapNotNull { it.toLongOrNull() }.forEach { ShareStore.cancel(context, it) }
        } else {
            ShareLive.cancel(context, id)
        }
    }
}
