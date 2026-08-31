package app.linc.android.service

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/** Connection state shared between the companion service and the UI. */
object CompanionStateHolder {

    sealed interface ServiceState {
        data object Stopped : ServiceState
        data object Listening : ServiceState
        data class Connected(
            val desktopApp: String,
            val desktopVersion: String,
            val protocolVersion: Int,
        ) : ServiceState
    }

    private val _state = MutableStateFlow<ServiceState>(ServiceState.Stopped)
    val state: StateFlow<ServiceState> = _state

    internal fun update(newState: ServiceState) {
        _state.value = newState
    }
}
