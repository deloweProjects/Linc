package app.linc.android.service

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

enum class LogLevel { INFO, WARN, ERROR }

data class LogEntry(val timestamp: Long, val level: LogLevel, val message: String)

/**
 * A bounded in-memory activity log (connection/sync events — never clipboard text
 * or notification bodies), following the same MutableStateFlow pattern as
 * CompanionStateHolder. In-memory only, no file persistence (Android's smaller scope).
 */
object LogStore {
    private const val MAX_ENTRIES = 500

    private val _entries = MutableStateFlow<List<LogEntry>>(emptyList())
    val entries: StateFlow<List<LogEntry>> = _entries

    fun log(level: LogLevel, message: String) {
        _entries.value = (_entries.value + LogEntry(System.currentTimeMillis(), level, message))
            .takeLast(MAX_ENTRIES)
    }

    fun clear() {
        _entries.value = emptyList()
    }
}
