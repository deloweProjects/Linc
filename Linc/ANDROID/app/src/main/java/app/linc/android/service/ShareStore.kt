package app.linc.android.service

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

data class ReceivedFile(val name: String, val path: String, val at: Long)

data class SentFile(val name: String, val at: Long, val ok: Boolean)

/** Files exchanged with the PC (v13 `share.incoming` / `share.item`), for the Share tab. */
object ShareStore {

    private val _received = MutableStateFlow<List<ReceivedFile>>(emptyList())
    val received: StateFlow<List<ReceivedFile>> = _received

    /** A one-line status of the last outgoing send, shown under the Share tab's send button. */
    private val _sendStatus = MutableStateFlow("")
    val sendStatus: StateFlow<String> = _sendStatus

    /** Name of the file currently being staged/sent, or null when idle — drives the progress UI. */
    private val _sending = MutableStateFlow<String?>(null)
    val sending: StateFlow<String?> = _sending

    /** Recently sent files, newest first. */
    private val _sent = MutableStateFlow<List<SentFile>>(emptyList())
    val sent: StateFlow<List<SentFile>> = _sent

    /** Newest received file name, so the UI can highlight what just landed. */
    private val _lastReceived = MutableStateFlow<String?>(null)
    val lastReceived: StateFlow<String?> = _lastReceived

    fun addReceived(name: String, path: String) {
        _received.value = (listOf(ReceivedFile(name, path, System.currentTimeMillis())) + _received.value).take(30)
        _lastReceived.value = name
    }

    fun clearHighlight() {
        _lastReceived.value = null
    }

    fun beginSend(name: String) {
        _sending.value = name
        _sendStatus.value = ""
    }

    fun endSend(name: String, ok: Boolean) {
        _sending.value = null
        _sent.value = (listOf(SentFile(name, System.currentTimeMillis(), ok)) + _sent.value).take(30)
        _sendStatus.value = if (ok) "" else
            "Couldn't send $name — check Linc has All-files access and a PC is connected."
    }

    fun setSendStatus(status: String) {
        _sendStatus.value = status
    }
}
