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

    /**
     * How the PC says the two are connected (v32 `link.state`, test build B): the PC owns this,
     * so both screens say the same. Null from an older PC, or until the PC has said; the UI then
     * falls back to what this phone can see of its own link.
     */
    data class PcLink(val route: String, val adb: Boolean, val adbVia: String?) {
        /** "USB cable" or "Wi-Fi": the PC's "Connected over USB / Wi-Fi" (LinkBrain.RouteCaption). */
        val label: String get() = if (route == "usb") "USB cable" else "Wi-Fi"

        companion object {
            private val ROUTES = setOf("usb", "wifi", "direct")
            private val VIAS = setOf("cable", "wireless")

            /** The payload of a `link.state`, or null when it isn't one this phone understands. */
            fun from(route: String?, adb: Boolean?, adbVia: String?): PcLink? {
                if (route !in ROUTES || adb == null) return null
                return PcLink(route!!, adb, adbVia?.takeIf { adb && it in VIAS })
            }
        }
    }

    private val _pcLink = MutableStateFlow<PcLink?>(null)
    val pcLink: StateFlow<PcLink?> = _pcLink

    internal fun setPcLink(link: PcLink?) {
        _pcLink.value = link
    }

    private val _state = MutableStateFlow<ServiceState>(ServiceState.Stopped)
    val state: StateFlow<ServiceState> = _state

    internal fun update(newState: ServiceState) {
        _state.value = newState
    }
}
