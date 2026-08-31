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

    /** Returns true when the file was staged and announced (the PC pulls it over the files channel). */
    fun sendFile(context: Context, uri: Uri): Boolean {
        val name = displayName(context, uri) ?: "shared-file"
        val dest = File(File(OUTBOX).apply { mkdirs() }, name)
        val staged = runCatching {
            context.contentResolver.openInputStream(uri)?.use { input ->
                dest.outputStream().use { input.copyTo(it) }
            } ?: return false
        }.isSuccess
        if (!staged) {
            return false
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
        return true
    }

    /** The name the picker reports for a uri, for showing progress before the copy starts. */
    fun nameFor(context: Context, uri: Uri): String = displayName(context, uri) ?: "shared-file"

    private fun displayName(context: Context, uri: Uri): String? = runCatching {
        context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use {
            if (it.moveToFirst()) it.getString(0) else null
        }
    }.getOrNull()

    /** The size the picker/share-sheet reports for a uri, for the M10 Part B bound checks. */
    fun sizeOf(context: Context, uri: Uri): Long = runCatching {
        context.contentResolver.query(uri, arrayOf(OpenableColumns.SIZE), null, null, null)?.use {
            if (it.moveToFirst()) it.getLong(0) else 0L
        } ?: 0L
    }.getOrDefault(0L)
}

/**
 * Refuses an oversized share before anything touches the phone's storage or the PC (M10 Part
 * B / B2.4). The bulk channel has no chunk-resume, so a huge transfer that dies mid-way wastes
 * everything — better to refuse clearly upfront than fail late.
 */
object ShareSizeLimits {
    const val MAX_SINGLE_ITEM_BYTES = 100L * 1024 * 1024
    const val MAX_TOTAL_BYTES = 250L * 1024 * 1024

    /** Pure: returns a plain-language refusal naming the limit, or null when the selection is OK. */
    fun check(sizes: List<Long>): String? {
        if (sizes.any { it > MAX_SINGLE_ITEM_BYTES }) {
            return "That file is over the 100 MB limit for a single share."
        }
        if (sizes.sum() > MAX_TOTAL_BYTES) {
            return "That selection is over the 250 MB total limit."
        }
        return null
    }
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
