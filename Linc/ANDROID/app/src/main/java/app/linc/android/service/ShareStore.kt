package app.linc.android.service

import android.content.Context
import android.media.MediaScannerConnection
import android.net.Uri
import android.os.Environment
import android.webkit.MimeTypeMap
import java.io.File
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch

data class ReceivedFile(val name: String, val path: String, val at: Long)

enum class TransferState { Copying, Sent, Waiting, Failed }

/**
 * One file going to the PC (1.0.0.11, D-083). [progress] is the copy into Linc's outbox (the PC
 * pulls it from there, so the phone can't see the pull itself); [uri] is kept for Retry.
 */
data class Transfer(
    val id: Long,
    val name: String,
    val state: TransferState,
    val at: Long,
    val progress: Float = 0f,
    val uri: Uri? = null,
    val message: String? = null,
)

/** Files exchanged with the PC (v13 `share.incoming` / `share.item`), for the Share tab. */
object ShareStore {

    private val ids = AtomicLong(0)
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private val _received = MutableStateFlow<List<ReceivedFile>>(emptyList())
    val received: StateFlow<List<ReceivedFile>> = _received

    private val _transfers = MutableStateFlow<List<Transfer>>(emptyList())
    val transfers: StateFlow<List<Transfer>> = _transfers

    /** Newest received file name, so the UI can highlight what just landed. */
    private val _lastReceived = MutableStateFlow<String?>(null)
    val lastReceived: StateFlow<String?> = _lastReceived

    /** How many received files the Share tab keeps (it shows the newest 20 until "Show all"). */
    private const val KEEP = 200

    @Volatile private var appContext: Context? = null

    /** From the Share tab (and anything else holding a context), so arrivals can be indexed. */
    fun attach(context: Context) {
        appContext = context.applicationContext
    }

    /**
     * Tell Android's media index about a file the PC just wrote, so pictures and videos show in
     * Gallery / Google Photos at once instead of after some later rescan. Nothing else does: the
     * PC writes with `adb push` or through [FileChannelServer], neither of which goes through
     * MediaStore. Scanning a path that no longer exists drops its stale entry instead.
     */
    fun scan(path: String) {
        val context = appContext ?: AudioEverywhere.context() ?: return
        runCatching {
            val mime = mimeOf(path)
            MediaScannerConnection.scanFile(context, arrayOf(path), if (mime != null) arrayOf(mime) else null, null)
        }
    }

    private fun mimeOf(path: String): String? =
        MimeTypeMap.getSingleton().getMimeTypeFromExtension(path.substringAfterLast('.', "").lowercase())

    fun addReceived(name: String, path: String) {
        _received.value = (listOf(ReceivedFile(name, path, System.currentTimeMillis())) +
            _received.value.filterNot { it.path == path }).take(KEEP)
        _lastReceived.value = name
        scan(path)
    }

    /**
     * Delete received files from the phone (the Share tab's selection), drop them from the list
     * and from the media index. Returns how many were actually removed.
     */
    fun deleteReceived(paths: Collection<String>): Int {
        val gone = paths.filter { path ->
            val file = File(path)
            runCatching { !file.exists() || file.delete() }.getOrDefault(false)
        }.toSet()
        if (gone.isNotEmpty()) {
            _received.value = _received.value.filterNot { it.path in gone }
            gone.forEach { scan(it) }
        }
        return gone.size
    }

    /**
     * What the PC has put in Download/Linc, newest first, so "Received from PC" survives a restart
     * (needs All files access to list; without it only this session's arrivals show).
     */
    fun loadReceived() {
        if (!Environment.isExternalStorageManager()) return
        scope.launch {
            val dir = File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "Linc")
            val files = dir.listFiles { f -> f.isFile }?.sortedByDescending { it.lastModified() }?.take(KEEP) ?: return@launch
            val known = _received.value.map { it.path }.toSet()
            val found = files.filter { it.absolutePath !in known }
            _received.value = (_received.value + found
                .map { ReceivedFile(it.name, it.absolutePath, it.lastModified()) }).sortedByDescending { it.at }.take(KEEP)
            // Files that landed while nothing announced them (or before Linc indexed arrivals):
            // index them once, the first time this process sees them, so they reach the Gallery too.
            found.forEach { scan(it.absolutePath) }
        }
    }

    fun clearHighlight() {
        _lastReceived.value = null
    }

    /** Send [uri] to the PC over Linc: copy into the outbox with progress, then announce it. */
    fun send(context: Context, uri: Uri, existing: Long? = null): Long {
        val app = context.applicationContext
        val name = ShareSender.nameFor(app, uri)
        val id = existing ?: ids.incrementAndGet()
        upsert(Transfer(id, name, TransferState.Copying, System.currentTimeMillis(), 0f, uri))
        scope.launch {
            val result = ShareSender.sendFile(app, uri) { p -> update(id) { it.copy(progress = p) } }
            update(id) {
                when (result) {
                    ShareSender.Result.Sent -> it.copy(state = TransferState.Sent, progress = 1f, message = null)
                    ShareSender.Result.Staged -> it.copy(state = TransferState.Waiting, progress = 1f,
                        message = "Your PC collects it when it reconnects")
                    is ShareSender.Result.Failed -> it.copy(state = TransferState.Failed, message = result.reason)
                }
            }
        }
        return id
    }

    fun retry(context: Context, id: Long) {
        val t = _transfers.value.firstOrNull { it.id == id } ?: return
        t.uri?.let { send(context, it, existing = id) }
    }

    fun dismiss(id: Long) {
        _transfers.value = _transfers.value.filterNot { it.id == id }
    }

    private fun upsert(t: Transfer) {
        _transfers.value = (listOf(t) + _transfers.value.filterNot { it.id == t.id }).take(30)
    }

    private fun update(id: Long, change: (Transfer) -> Transfer) {
        _transfers.value = _transfers.value.map { if (it.id == id) change(it) else it }
    }
}
