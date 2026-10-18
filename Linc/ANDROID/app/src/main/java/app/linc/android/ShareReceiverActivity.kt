package app.linc.android

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.core.content.IntentCompat
import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import app.linc.android.service.CompanionOutbox
import app.linc.android.service.LogLevel
import app.linc.android.service.LogStore
import app.linc.android.service.ShareSender
import app.linc.android.service.ShareSizeLimits
import app.linc.android.service.ShareStore
import app.linc.android.service.ShareProgress
import app.linc.android.service.TransferState
import kotlinx.coroutines.flow.first
import app.linc.android.service.resolveShareUris
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/**
 * The Android share-sheet "Send to PC" target (PROTOCOL.md v10 `share.item`, D-024).
 * A no-UI activity: takes shared text/links, exactly as before, or one-or-more files (M10 Part
 * B — ACTION_SEND and ACTION_SEND_MULTIPLE, any mime type) and hands them to the outbox for the
 * connected desktop, then finishes. Requires Linc to be connected to a PC.
 */
class ShareReceiverActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val intent = intent
        val text = intent?.takeIf { it.action == Intent.ACTION_SEND }
            ?.getStringExtra(Intent.EXTRA_TEXT)?.trim()

        if (!text.isNullOrEmpty()) {
            sendText(text)
        } else {
            val single = intent?.let { IntentCompat.getParcelableExtra(it, Intent.EXTRA_STREAM, Uri::class.java) }
            val multiple = intent?.let {
                IntentCompat.getParcelableArrayListExtra(it, Intent.EXTRA_STREAM, Uri::class.java)
            }
            val uris = resolveShareUris(intent?.action, single, multiple)
            if (uris.isNotEmpty()) {
                handleFiles(uris)
            }
        }
        finish()
    }

    private fun sendText(text: String) {
        // Not connected: Quick Share instead of a dead end (D-083 — one model on both apps).
        if (!CompanionOutbox.canSend(SHARE_ITEM_VERSION)) {
            if (app.linc.android.service.QuickShareLauncher.sendText(this, text)) {
                Toast.makeText(this, R.string.share_via_quick_share, Toast.LENGTH_SHORT).show()
            } else {
                Toast.makeText(this, R.string.share_not_connected, Toast.LENGTH_SHORT).show()
            }
            return
        }
        val kind = if (URL_REGEX.matches(text)) "url" else "text"
        // The PC puts shared text on its clipboard, so it belongs in the clipboard history too —
        // and sharing to Linc is how text copied in other apps reaches the PC (D-083).
        app.linc.android.service.ClipboardHistoryStore.init(this)
        app.linc.android.service.ClipboardHistoryStore.record(text, fromPhone = true, sent = true)
        CompanionOutbox.trySend(
            Envelope(
                type = MessageType.SHARE_ITEM,
                payload = buildJsonObject {
                    put("kind", kind)
                    put("text", text)
                },
            ),
            requiredVersion = SHARE_ITEM_VERSION,
        )
        Toast.makeText(this, R.string.share_sent, Toast.LENGTH_SHORT).show()
    }

    private fun handleFiles(uris: List<Uri>) {
        if (!CompanionOutbox.canSend(SHARE_ITEM_VERSION)) {
            // Not connected: Quick Share instead of a dead end (D-083).
            if (app.linc.android.service.QuickShareLauncher.send(this, uris)) {
                Toast.makeText(this, R.string.share_via_quick_share, Toast.LENGTH_SHORT).show()
            } else {
                Toast.makeText(this, R.string.share_not_connected, Toast.LENGTH_SHORT).show()
            }
            return
        }

        val sizes = uris.map { ShareSender.sizeOf(this, it) }
        val refusal = ShareSizeLimits.check(sizes)
        if (refusal != null) {
            Toast.makeText(this, refusal, Toast.LENGTH_LONG).show()
            return
        }

        LogStore.log(LogLevel.INFO, "Sharing ${uris.size} item(s) with the PC")
        // The notification, not a toast, carries the feedback (D-092): it stays while the copy
        // runs, turns into "Sent" when every item is across, then dismisses itself.
        val progress = ShareProgress(this, uris.size)
        progress.sending(0, ShareSender.nameFor(this, uris[0]))

        // Copying + sending can be slow (I/O over a large file); the activity must finish
        // promptly and never hold the UI open for a transfer (B2.6), so this rides a
        // process-lifetime scope rather than one tied to the activity — the same posture
        // CompanionService's own background scope uses for its long-lived work.
        val appContext = applicationContext
        backgroundScope.launch {
            // Sequential, in selection order (B2.3) — not a new batch message.
            val ids = uris.map { ShareStore.send(appContext, it) }
            // Follow ShareStore's own transfer states into the notification.
            ShareStore.transfers.first { list ->
                val mine = list.filter { it.id in ids }
                val done = mine.count { it.state != TransferState.Copying }
                mine.firstOrNull { it.state == TransferState.Copying }?.let { progress.sending(done, it.name) }
                mine.size == ids.size && done == ids.size
            }.let { list ->
                progress.done(failed = list.count { it.id in ids && it.state == TransferState.Failed })
            }
        }
    }

    private companion object {
        val URL_REGEX = Regex("^https?://\\S+$")
        const val SHARE_ITEM_VERSION = 10
        val backgroundScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    }
}
