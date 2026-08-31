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
        val kind = if (URL_REGEX.matches(text)) "url" else "text"
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
            Toast.makeText(this, R.string.share_not_connected, Toast.LENGTH_SHORT).show()
            return
        }

        val sizes = uris.map { ShareSender.sizeOf(this, it) }
        val refusal = ShareSizeLimits.check(sizes)
        if (refusal != null) {
            Toast.makeText(this, refusal, Toast.LENGTH_LONG).show()
            return
        }

        LogStore.log(LogLevel.INFO, "Sharing ${uris.size} item(s) with the PC")
        Toast.makeText(
            this,
            if (uris.size == 1) getString(R.string.share_sending_one)
            else getString(R.string.share_sending_many, uris.size),
            Toast.LENGTH_SHORT,
        ).show()

        // Copying + sending can be slow (I/O over a large file); the activity must finish
        // promptly and never hold the UI open for a transfer (B2.6), so this rides a
        // process-lifetime scope rather than one tied to the activity — the same posture
        // CompanionService's own background scope uses for its long-lived work.
        val appContext = applicationContext
        backgroundScope.launch {
            // Sequential, in selection order (B2.3) — not a new batch message.
            for (uri in uris) {
                ShareSender.sendFile(appContext, uri)
            }
        }
    }

    private companion object {
        val URL_REGEX = Regex("^https?://\\S+$")
        const val SHARE_ITEM_VERSION = 10
        val backgroundScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    }
}
