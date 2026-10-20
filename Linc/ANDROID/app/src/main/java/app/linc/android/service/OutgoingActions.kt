package app.linc.android.service

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.OpenableColumns
import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import java.io.File
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/** Controls the PC's media from the phone Home widget (v13 `pc.media.control`, fire-and-forget). */
object PcMediaControl {
    fun send(action: String) {
        CompanionOutbox.trySend(
            Envelope(
                type = MessageType.PC_MEDIA_CONTROL,
                payload = buildJsonObject { put("action", action) },
            ),
            requiredVersion = 13,
        )
    }
}

/** Sends a picked file to the PC (v13 Share tab): copy to the outbox, then `share.item kind:file`. */
object ShareSender {

    private const val OUTBOX = "/sdcard/Download/Linc/outbox"

    sealed interface Result {
        /** Staged and announced to a connected PC, which pulls it now. */
        data object Sent : Result
        /** Staged; no PC connected, so it is collected when the PC reconnects (outbox sweep). */
        data object Staged : Result
        data class Failed(val reason: String) : Result
    }

    /**
     * Copy [uri] into Linc's outbox ([onProgress] 0..1 as it copies), then announce it; the PC pulls
     * it over the files channel. Never fails silently: every failure says why (D-083).
     */
    fun sendFile(context: Context, uri: Uri, onProgress: (Float) -> Unit = {}): Result {
        if (!android.os.Environment.isExternalStorageManager()) {
            return Result.Failed("Linc needs All files access to hand files to your PC")
        }
        val name = displayName(context, uri) ?: "shared-file"
        val total = sizeOf(context, uri)
        val outbox = File(OUTBOX).apply { mkdirs() }
        val dest = File(outbox, name)
        val staged = runCatching {
            context.contentResolver.openInputStream(uri)?.use { input ->
                dest.outputStream().use { output ->
                    val buffer = ByteArray(64 * 1024)
                    var copied = 0L
                    while (true) {
                        val n = input.read(buffer)
                        if (n < 0) break
                        output.write(buffer, 0, n)
                        copied += n
                        if (total > 0) onProgress((copied.toFloat() / total).coerceIn(0f, 1f))
                    }
                }
            } ?: return Result.Failed("Couldn't open $name")
        }.isSuccess
        if (!staged) {
            return Result.Failed("Couldn't copy $name — is the phone's storage full?")
        }
        if (!CompanionOutbox.canSend(10)) {
            LogStore.log(LogLevel.INFO, "Staged a file for the PC to collect when it reconnects")
            return Result.Staged
        }
        CompanionOutbox.trySend(
            Envelope(
                type = MessageType.SHARE_ITEM,
                payload = buildJsonObject {
                    put("kind", "file")
                    put("path", dest.absolutePath)
                },
            ),
            requiredVersion = 10,
        )
        LogStore.log(LogLevel.INFO, "Sent a file to the PC")
        return Result.Sent
    }

    /** Text or a link to the PC over Linc (`share.item` text/url, v10). False when not connected. */
    fun sendText(text: String): Boolean {
        if (text.isBlank() || !CompanionOutbox.canSend(10)) return false
        val kind = if (Regex("^https?://\\S+$").matches(text.trim())) "url" else "text"
        CompanionOutbox.trySend(
            Envelope(
                type = MessageType.SHARE_ITEM,
                payload = buildJsonObject {
                    put("kind", kind)
                    put("text", text.trim())
                },
            ),
            requiredVersion = 10,
        )
        return true
    }

    /** The name the picker reports for a uri, for showing progress before the copy starts. */
    fun nameFor(context: Context, uri: Uri): String = displayName(context, uri) ?: "shared-file"

    private fun displayName(context: Context, uri: Uri): String? = runCatching {
        context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use {
            if (it.moveToFirst()) it.getString(0) else null
        }
    }.getOrNull()

    /** The size the picker/share-sheet reports for a uri, for the copy's progress. */
    fun sizeOf(context: Context, uri: Uri): Long = runCatching {
        context.contentResolver.query(uri, arrayOf(OpenableColumns.SIZE), null, null, null)?.use {
            if (it.moveToFirst()) it.getLong(0) else 0L
        } ?: 0L
    }.getOrDefault(0L)
}

/**
 * Decides which URIs a share intent's already-extracted extras represent — the single item for
 * ACTION_SEND, the list for ACTION_SEND_MULTIPLE, empty otherwise (M10 Part B). Generic so the
 * branching is unit-testable without a real android.net.Uri, which is unavailable un-mocked
 * under this module's plain-JUnit test setup (no Robolectric) — the same Context-free split
 * DisplayControlTest already relies on for its pure logic.
 */
fun <T> resolveShareUris(action: String?, single: T?, multiple: List<T>?): List<T> = when (action) {
    Intent.ACTION_SEND_MULTIPLE -> multiple.orEmpty()
    Intent.ACTION_SEND -> listOfNotNull(single)
    else -> emptyList()
}
