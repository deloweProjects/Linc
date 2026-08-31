package app.linc.android.service

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

data class ClipItem(val text: String, val fromPhone: Boolean, val at: Long)

/**
 * In-memory clipboard history for the Home clipboard widget (M19). Recorded as clips pass
 * through [ClipboardBridge] in either direction. Never persisted — same privacy posture as
 * the desktop's clipboard history (never logged, never written to disk).
 */
object ClipboardHistoryStore {

    private const val MAX = 10

    private val _items = MutableStateFlow<List<ClipItem>>(emptyList())
    val items: StateFlow<List<ClipItem>> = _items

    fun record(text: String, fromPhone: Boolean) {
        if (text.isBlank()) return
        val deduped = _items.value.filterNot { it.text == text }
        _items.value = (listOf(ClipItem(text, fromPhone, System.currentTimeMillis())) + deduped).take(MAX)
    }
}
