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
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "Sending to your PC", NotificationManager.IMPORTANCE_LOW)
        )
    }

    /** Sending item [index] (0-based) of [total], called [name]. */
    fun sending(index: Int, name: String) {
        post(
            builder()
                .setContentTitle(if (total == 1) "Sending to your PC" else "Sending ${index + 1} of $total to your PC")
                .setContentText(name)
                .setOngoing(true)
                .setProgress(total, index, total == 1)
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
        .setSilent(true)

    private fun post(builder: NotificationCompat.Builder) {
        // Without POST_NOTIFICATIONS this throws on some builds; the transfer itself goes on.
        runCatching { manager.notify(id, builder.build()) }
    }

    private companion object {
        const val CHANNEL_ID = "linc.share"
        const val DONE_VISIBLE_MS = 3_000L
        const val FAILED_VISIBLE_MS = 8_000L
        val NEXT_ID = java.util.concurrent.atomic.AtomicInteger(4_000)
    }
}
