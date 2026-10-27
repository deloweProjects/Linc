package app.linc.android.service

import android.content.Context
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

enum class LogLevel { INFO, WARN, ERROR }

/**
 * [runCatching] that leaves a WARN line in the log when [block] fails (TIDY-UP A2). An empty catch
 * is how the MediaSession Looper bug that cut Play everywhere off went unseen. Use it where a
 * failure changes what the user gets; plain runCatching stays for stop()/release() clean-up,
 * where failing only means "already gone".
 */
inline fun <T> runLogged(what: String, block: () -> T): Result<T> =
    runCatching(block).onFailure { LogStore.log(LogLevel.WARN, "$what failed (${it.javaClass.simpleName}: ${it.message})") }

data class LogEntry(val timestamp: Long, val level: LogLevel, val message: String)

/**
 * A bounded activity log (connection/sync events — never clipboard text or notification
 * bodies), following the same MutableStateFlow pattern as CompanionStateHolder.
 *
 * Also kept in a small file (`linc-log.txt`, ~256 KB, the older half dropped when full) once
 * [init] has run, so a log copied from Logs still holds what happened before Android killed the
 * app — which is exactly the part needed to explain a dropped connection.
 */
object LogStore {
    private const val MAX_ENTRIES = 500
    private const val MAX_FILE_BYTES = 256 * 1024

    private val _entries = MutableStateFlow<List<LogEntry>>(emptyList())
    val entries: StateFlow<List<LogEntry>> = _entries

    @Volatile private var file: File? = null
    private val fileLock = Any()
    private val stamp = SimpleDateFormat("yyyy-MM-dd HH:mm:ss.SSS", Locale.US)

    /** Start keeping the log on disk too. Safe to call more than once. */
    fun init(context: Context) {
        if (file == null) file = File(context.applicationContext.filesDir, "linc-log.txt")
    }

    fun log(level: LogLevel, message: String) {
        // Mirrored to logcat (tag "Linc") so timings can be read over adb next to the
        // system's own events; harmless no-op in JVM unit tests.
        runCatching {
            android.util.Log.println(
                when (level) { LogLevel.INFO -> android.util.Log.INFO; LogLevel.WARN -> android.util.Log.WARN; LogLevel.ERROR -> android.util.Log.ERROR },
                "Linc", message,
            )
        }
        val entry = LogEntry(System.currentTimeMillis(), level, message)
        _entries.value = (_entries.value + entry).takeLast(MAX_ENTRIES)
        file?.let { append(it, entry) }
    }

    fun clear() {
        _entries.value = emptyList()
        file?.let { f -> synchronized(fileLock) { runCatching { f.delete() } } }
    }

    /** Everything on disk (this run and earlier ones), oldest first; the in-memory log if none. */
    fun persisted(): String {
        val f = file
        val text = f?.let { synchronized(fileLock) { runCatching { it.readText() }.getOrNull() } }
        return text?.takeIf { it.isNotBlank() } ?: _entries.value.joinToString("\n") { format(it) }
    }

    fun format(entry: LogEntry): String =
        "${synchronized(stamp) { stamp.format(Date(entry.timestamp)) }} ${entry.level.name.padEnd(5)} ${entry.message}"

    private fun append(f: File, entry: LogEntry) = synchronized(fileLock) {
        runCatching {
            if (f.length() > MAX_FILE_BYTES) {
                val keep = f.readText().takeLast(MAX_FILE_BYTES / 2)
                f.writeText(keep.substringAfter('\n'))
            }
            f.appendText(format(entry) + "\n")
        }
    }
}
