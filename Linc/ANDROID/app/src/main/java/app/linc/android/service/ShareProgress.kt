package app.linc.android.service

import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import androidx.core.app.NotificationCompat
import app.linc.android.R

/**
 * The "Send to PC" share target's progress, as a notification that outlives the share sheet:
 * sending (with progress for several files), then "Sent", then gone on its own. The share
 * activity finishes at once (B2.6), so a toast was the only feedback before and it vanished
 * long before a big file was actually across.
 */
class ShareProgress(context: Context, private val total: Int) {

    private val context = context.applicationContext
    private val manager = this.context.getSystemService(NotificationManager::class.java)
    private val id = NEXT_ID.getAndIncrement()

    init {
        // High importance so it pops up over the app you shared from (the owner asked to see it),
        // but with no sound or vibration: it's feedback, not an alert. The old low-importance
        // channel only reached the shade, so the share looked like it did nothing; it is replaced
        // (a channel's importance can't be raised once created).
        manager.deleteNotificationChannel(OLD_CHANNEL_ID)
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "Sending to your PC", NotificationManager.IMPORTANCE_HIGH).apply {
                setSound(null, null)
                enableVibration(false)
                description = "Shows while a file you shared goes to your PC"
            }
        )
    }

    private var lastPercent = -1
    private var lastIndex = -1

    /**
     * Sending item [index] (0-based) of [total], called [name], [fraction] (0..1) of the way
     * through it, or null while that isn't known yet. Throttled to whole percents.
     */
    fun sending(index: Int, name: String, fraction: Float? = null) {
        val percent = fraction?.let { (it.coerceIn(0f, 1f) * 100).toInt() } ?: -1
        if (index == lastIndex && percent == lastPercent) return
        lastIndex = index
        lastPercent = percent
        val overall = if (fraction == null) index * 100 else index * 100 + percent
        post(
            builder()
                .setContentTitle(if (total == 1) "Sending to your PC" else "Sending ${index + 1} of $total to your PC")
                .setContentText(if (percent >= 0) "$name · $percent%" else name)
                .setSubText("Linc")
                .setOngoing(true)
                .setProgress(total * 100, overall, fraction == null && total == 1)
        )
    }

    /** Every item went; [failed] of them could not be read or sent. Dismisses itself. */
    fun done(failed: Int) {
        val sent = total - failed
        post(
            builder()
                .setContentTitle(
                    when {
                        failed == 0 -> if (total == 1) "Sent to your PC" else "Sent $total items to your PC"
                        sent == 0 -> "Couldn't send to your PC"
                        else -> "Sent $sent of $total items to your PC"
                    }
                )
                .setContentText(if (failed == 0) "Done" else "Check that Linc is connected, then share again.")
                .setOngoing(false)
                .setAutoCancel(true)
                .setTimeoutAfter(if (failed == 0) DONE_VISIBLE_MS else FAILED_VISIBLE_MS)
        )
    }

    private fun builder() = NotificationCompat.Builder(context, CHANNEL_ID)
        .setSmallIcon(R.drawable.ic_stat_linc)
        .setOnlyAlertOnce(true)
        // Not setSilent(): that files it in a hidden group whose alerts are suppressed, so it never
        // pops up. The channel itself has no sound, and only the first post alerts.
        .setPriority(NotificationCompat.PRIORITY_HIGH)
        .setCategory(NotificationCompat.CATEGORY_PROGRESS)

    private fun post(builder: NotificationCompat.Builder) {
        // Without POST_NOTIFICATIONS this throws on some builds; the transfer itself goes on.
        runCatching { manager.notify(id, builder.build()) }
    }

    private companion object {
        const val OLD_CHANNEL_ID = "linc.share"
        const val CHANNEL_ID = "linc.share.progress"
        const val DONE_VISIBLE_MS = 3_000L
        const val FAILED_VISIBLE_MS = 8_000L
        val NEXT_ID = java.util.concurrent.atomic.AtomicInteger(4_000)
    }
}
