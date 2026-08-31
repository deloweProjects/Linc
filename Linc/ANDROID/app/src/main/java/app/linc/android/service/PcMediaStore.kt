package app.linc.android.service

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/** What's playing on the PC (v13 `pc.media.state`). `present == false` means nothing is playing. */
data class PcMedia(
    val title: String?,
    val artist: String?,
    val playing: Boolean,
    val positionMs: Long,
    val durationMs: Long,
    val app: String?,
    val present: Boolean,
)

/** Latest PC media state, for the Home PC-media widget (M19, D-028). */
object PcMediaStore {

    private val _state = MutableStateFlow<PcMedia?>(null)
    val state: StateFlow<PcMedia?> = _state

    fun update(media: PcMedia?) {
        _state.value = media
    }
}
