package app.linc.android.service

import android.app.Notification
import android.app.RemoteInput
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.drawable.Icon
import android.os.Bundle
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import androidx.core.app.NotificationManagerCompat
import androidx.core.graphics.drawable.toBitmap
import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import app.linc.android.protocol.Topic
import java.io.ByteArrayOutputStream
import kotlinx.serialization.json.add
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/**
 * Relays phone notifications to the desktop (docs/PROTOCOL.md v2, rich fields v6) and
 * fires notification actions / inline replies on the desktop's behalf. Requires the
 * user to grant notification access once — the status screen guides them there.
 */
class LincNotificationListener : NotificationListenerService() {

    companion object {
        @Volatile
        var instance: LincNotificationListener? = null
            private set

        fun isEnabled(context: Context): Boolean =
            NotificationManagerCompat.getEnabledListenerPackages(context)
                .contains(context.packageName)

        private const val LARGE_ICON_SIZE_PX = 96
    }

    override fun onListenerConnected() {
        instance = this
    }

    override fun onListenerDisconnected() {
        instance = null
    }

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        relay(sbn, existing = false)
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification) {
        LargeIconCache.remove(sbn.key)
        CompanionOutbox.trySend(Envelope(
            type = MessageType.NOTIFICATION_REMOVED,
            payload = buildJsonObject { put("key", sbn.key) },
        ), requiredVersion = 2, topic = Topic.NOTIFICATIONS)
    }

    /** Sends the current notifications when a desktop connects (flagged as existing). */
    fun pushActiveNotifications() {
        val active = runCatching { activeNotifications }.getOrNull() ?: return
        active.forEach { relay(it, existing = true) }
    }

    /** Desktop asked to dismiss this notification on the phone. */
    fun dismiss(key: String) {
        runCatching { cancelNotification(key) }
    }

    /** Fires a non-reply action's PendingIntent (v6). Returns false when it's gone/failed. */
    fun fireAction(key: String, index: Int): Boolean {
        val action = findAction(key, index) ?: return false
        return runCatching { action.actionIntent.send() }.isSuccess
    }

    /** Fills the action's RemoteInput with [text] and fires it (v6 inline reply). */
    fun reply(key: String, index: Int, text: String): Boolean {
        val action = findAction(key, index) ?: return false
        val remoteInputs = action.remoteInputs?.takeIf { it.isNotEmpty() } ?: return false
        val results = Bundle()
        remoteInputs.forEach { results.putCharSequence(it.resultKey, text) }
        val fill = Intent()
        RemoteInput.addResultsToIntent(remoteInputs, fill, results)
        return runCatching { action.actionIntent.send(this, 0, fill) }.isSuccess
    }

    private fun findAction(key: String, index: Int): Notification.Action? {
        val sbn = runCatching { activeNotifications }.getOrNull()
            ?.firstOrNull { it.key == key } ?: return null
        return sbn.notification.actions?.getOrNull(index)
    }

    private fun relay(sbn: StatusBarNotification, existing: Boolean) {
        if (sbn.packageName == packageName || sbn.isOngoing) {
            return
        }
        val extras = sbn.notification.extras
        val title = extras.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty()
        val text = extras.getCharSequence(Notification.EXTRA_TEXT)?.toString().orEmpty()
        if (title.isEmpty() && text.isEmpty()) {
            return // group summaries and media sessions carry no useful content
        }
        val appName = runCatching {
            packageManager.getApplicationLabel(
                packageManager.getApplicationInfo(sbn.packageName, 0)).toString()
        }.getOrDefault(sbn.packageName)
        val largeIconId = cacheLargeIcon(sbn)
        val conversationTitle =
            extras.getCharSequence(Notification.EXTRA_CONVERSATION_TITLE)?.toString()
        val actions = sbn.notification.actions.orEmpty()

        CompanionOutbox.trySend(Envelope(
            type = MessageType.NOTIFICATION_POSTED,
            payload = buildJsonObject {
                put("key", sbn.key)
                put("app", appName)
                put("title", title)
                put("text", text)
                put("postedAt", sbn.postTime)
                put("existing", existing)
                // v6 rich fields — harmless extras on older links (ignored per spec).
                put("appPackage", sbn.packageName)
                sbn.notification.category?.let { put("category", it) }
                conversationTitle?.let { put("conversationTitle", it) }
                largeIconId?.let { put("largeIconId", it) }
                if (actions.isNotEmpty()) {
                    put("actions", buildJsonArray {
                        actions.forEachIndexed { index, action ->
                            add(buildJsonObject {
                                put("index", index)
                                put("title", action.title?.toString().orEmpty())
                                put("allowsReply", !action.remoteInputs.isNullOrEmpty())
                            })
                        }
                    })
                }
            },
        ), requiredVersion = 2, topic = Topic.NOTIFICATIONS)
        if (!existing) {
            LogStore.log(LogLevel.INFO, "Notification relayed from $appName")
        }
    }

    /** Renders the large icon (sender avatar etc.) to PNG and caches it for bulk fetch. */
    private fun cacheLargeIcon(sbn: StatusBarNotification): String? {
        val icon = sbn.notification.extras.getParcelable(
            Notification.EXTRA_LARGE_ICON, Icon::class.java) ?: return null
        val png = runCatching {
            val bitmap = icon.loadDrawable(this)?.toBitmap(LARGE_ICON_SIZE_PX, LARGE_ICON_SIZE_PX)
                ?: return null
            ByteArrayOutputStream().use { out ->
                bitmap.compress(Bitmap.CompressFormat.PNG, 100, out)
                out.toByteArray()
            }
        }.getOrNull() ?: return null
        return LargeIconCache.put(sbn.key, png)
    }
}
