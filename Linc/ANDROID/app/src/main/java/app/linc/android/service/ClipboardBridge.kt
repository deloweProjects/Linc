package app.linc.android.service

import android.content.ClipData
import android.content.ClipDescription
import android.content.ClipboardManager
import android.content.Context
import android.os.Build
import android.os.PersistableBundle
import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import app.linc.android.protocol.Topic
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/**
 * Phone side of clipboard sync (docs/PROTOCOL.md v1; history, Send text and Sync now in 1.0.0.11,
 * D-083).
 *
 * Android 10+ only lets the focused app read the clipboard, so phone → PC sync happens while Linc
 * is open (focus gain and clipboard-changed events), or when text is shared to Linc. PC → phone
 * always works — writing the clipboard is allowed from the companion service.
 *
 * Privacy: a clip an app marked sensitive (Android 13+, `EXTRA_IS_SENSITIVE` — passwords from a
 * password manager, one-time codes) is never sent or saved while "Don't save sensitive copies" is
 * on (the default). Nothing here ever logs clipboard text.
 */
object ClipboardBridge {

    /** Last text the desktop pushed; used to stop it echoing straight back. */
    @Volatile
    private var lastFromDesktop: String? = null

    @Volatile
    private var lastSent: String? = null

    /** True while Linc's own window has focus — the only time Android lets it read the clipboard. */
    @Volatile
    var focused: Boolean = false

    /** v24: the PC can be asked for its clipboard ([requestFromPc]). */
    const val GET_VERSION = 24

    /** Desktop → phone: apply and remember for echo protection. */
    fun applyFromDesktop(context: Context, text: String) {
        if (!ClipboardSettings.syncEnabled(context)) return
        lastFromDesktop = text
        val clipboard = context.getSystemService(ClipboardManager::class.java)
        clipboard.setPrimaryClip(ClipData.newPlainText("Linc", text))
        ClipboardHistoryStore.init(context)
        ClipboardHistoryStore.record(text, fromPhone = false)
        LogStore.log(LogLevel.INFO, "Clipboard synced from PC")
    }

    /**
     * Phone → desktop: send the current clip if it is new. Call only while focused. [force] sends
     * it even if it was sent before (Sync now).
     */
    fun pushIfChanged(context: Context, force: Boolean = false) {
        if (!ClipboardSettings.syncEnabled(context)) return
        val clipboard = context.getSystemService(ClipboardManager::class.java)
        val clip = clipboard.primaryClip?.takeIf { it.itemCount > 0 } ?: return
        if (isSensitive(clip.description) && ClipboardSettings.skipSensitive(context)) return
        val text = clip.getItemAt(0).coerceToText(context)?.toString() ?: return
        if (text.isEmpty() || (!force && (text == lastSent || text == lastFromDesktop))) {
            return
        }
        lastSent = text
        ClipboardHistoryStore.init(context)
        ClipboardHistoryStore.record(text, fromPhone = true)
        send(text)
        LogStore.log(LogLevel.INFO, "Clipboard synced to PC")
    }

    /**
     * The composer's Send: put [text] on the PC's clipboard — like copying it there — and keep it in
     * the history marked as sent. Returns false when no PC is connected.
     */
    fun sendToPc(context: Context, text: String): Boolean {
        if (text.isBlank() || !CompanionOutbox.canSend(1)) return false
        lastSent = text
        ClipboardHistoryStore.init(context)
        ClipboardHistoryStore.record(text, fromPhone = true, sent = true)
        send(text)
        LogStore.log(LogLevel.INFO, "Sent text to the PC's clipboard")
        return true
    }

    /** Copy a history item onto this phone's clipboard (no echo back to the PC). */
    fun copyHere(context: Context, text: String) {
        lastSent = text
        context.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("Linc", text))
    }

    /** Sync now: push this phone's clipboard (if Linc may read it) and ask the PC for its own. */
    fun syncNow(context: Context) {
        if (focused) pushIfChanged(context, force = true)
        requestFromPc()
    }

    /** v24 `clipboard.get`: the PC answers with its clipboard as an ordinary `clipboard.set`. */
    fun requestFromPc() {
        CompanionOutbox.trySend(Envelope(type = MessageType.CLIPBOARD_GET), requiredVersion = GET_VERSION)
    }

    /** The PC asked for this phone's clipboard (v24). Android only allows it while Linc is open. */
    fun onPcAsked(context: Context) {
        if (focused) pushIfChanged(context, force = true)
    }

    private fun send(text: String) {
        CompanionOutbox.trySend(Envelope(
            type = MessageType.CLIPBOARD_CHANGED,
            payload = buildJsonObject { put("text", text) },
        ), requiredVersion = 1, topic = Topic.CLIPBOARD)
    }

    /** Pure enough to reason about: the platform's "this clip is sensitive" flag (API 33+). */
    fun isSensitive(description: ClipDescription?): Boolean {
        val extras: PersistableBundle = description?.extras ?: return false
        val key = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) ClipDescription.EXTRA_IS_SENSITIVE
        else "android.content.extra.IS_SENSITIVE"
        return extras.getBoolean(key, false)
    }
}
