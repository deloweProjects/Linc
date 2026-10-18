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
            /** The PC's name, from its hello (optional field, 1.0.0.11); null from older PCs. */
            val host: String? = null,
        ) : ServiceState
    }

    /** How the live link reaches the PC, for Home's hero and the Connection sheet (D-082). */
    enum class Route { Adb, Direct }

    data class Link(val route: Route, val address: String?, val since: Long)

    private val _link = MutableStateFlow<Link?>(null)
    val link: StateFlow<Link?> = _link

    internal fun setLink(link: Link?) {
        _link.value = link
    }

    private val _state = MutableStateFlow<ServiceState>(ServiceState.Stopped)
    val state: StateFlow<ServiceState> = _state

    internal fun update(newState: ServiceState) {
        _state.value = newState
    }
}
