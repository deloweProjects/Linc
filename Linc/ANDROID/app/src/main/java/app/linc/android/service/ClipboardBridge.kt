package app.linc.android.service

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import app.linc.android.protocol.Topic
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/**
 * Phone side of clipboard sync (docs/PROTOCOL.md v1).
 *
 * Android 10+ only lets the focused app read the clipboard, so phone → desktop
 * sync is best effort: it fires while the Linc activity is visible (focus gain
 * and clipboard-changed events). Desktop → phone always works — writing the
 * clipboard is allowed from the companion service.
 */
object ClipboardBridge {

    /** Last text the desktop pushed; used to stop it echoing straight back. */
    @Volatile
    private var lastFromDesktop: String? = null

    @Volatile
    private var lastSent: String? = null

    /** Desktop → phone: apply and remember for echo protection. */
    fun applyFromDesktop(context: Context, text: String) {
        lastFromDesktop = text
        val clipboard = context.getSystemService(ClipboardManager::class.java)
        clipboard.setPrimaryClip(ClipData.newPlainText("Linc", text))
        ClipboardHistoryStore.record(text, fromPhone = false)
        LogStore.log(LogLevel.INFO, "Clipboard synced from PC")
    }

    /** Phone → desktop: send the current clip if it is new. Call only while focused. */
    fun pushIfChanged(context: Context) {
        val clipboard = context.getSystemService(ClipboardManager::class.java)
        val clip = clipboard.primaryClip?.takeIf { it.itemCount > 0 } ?: return
        val text = clip.getItemAt(0).coerceToText(context)?.toString() ?: return
        if (text.isEmpty() || text == lastSent || text == lastFromDesktop) {
            return
        }
        lastSent = text
        ClipboardHistoryStore.record(text, fromPhone = true)
        CompanionOutbox.trySend(Envelope(
            type = MessageType.CLIPBOARD_CHANGED,
            payload = buildJsonObject { put("text", text) },
        ), requiredVersion = 1, topic = Topic.CLIPBOARD)
        LogStore.log(LogLevel.INFO, "Clipboard synced to PC")
    }
}
