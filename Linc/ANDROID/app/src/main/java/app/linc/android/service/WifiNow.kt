package app.linc.android.service

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.wifi.WifiInfo
import android.net.wifi.WifiManager

/**
 * The Wi-Fi the phone is joined to, **even when it isn't Android's default network.** A PC hotspot
 * with no internet is the common case: Android joins it but keeps mobile data as the default, so
 * `activeNetwork` (and the old `WifiManager.connectionInfo`) said "not on Wi-Fi". That made the PC
 * show "Not on Wi-Fi", and LincLink never turned wireless debugging back on there (owner's log,
 * 2026-11-01: "phone connects to PC hotspot, wireless debug is on but Linc says it isn't").
 */
object WifiNow {

    /** Windows' Mobile Hotspot always hands out 192.168.137.x with itself at .1 (Internet Connection Sharing). */
    const val WINDOWS_HOTSPOT_GATEWAY = "192.168.137.1"

    fun network(context: Context): Network? = runCatching {
        val cm = context.getSystemService(ConnectivityManager::class.java) ?: return null
        val isWifi = { n: Network -> cm.getNetworkCapabilities(n)?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true }
        cm.activeNetwork?.takeIf(isWifi)?.let { return it }
        @Suppress("DEPRECATION")
        cm.allNetworks.firstOrNull(isWifi)
    }.getOrNull()

    /** The Wi-Fi's IPv4 gateway, or null off Wi-Fi. */
    fun gateway(context: Context): String? = runCatching {
        val cm = context.getSystemService(ConnectivityManager::class.java) ?: return null
        val props = cm.getLinkProperties(network(context) ?: return null) ?: return null
        props.routes.firstOrNull { it.isDefaultRoute && it.gateway is java.net.Inet4Address }?.gateway?.hostAddress
    }.getOrNull()

    /** The Wi-Fi's own IPv4 address and prefix length, or null off Wi-Fi. */
    fun address(context: Context): Pair<ByteArray, Int>? = runCatching {
        val cm = context.getSystemService(ConnectivityManager::class.java) ?: return null
        val props = cm.getLinkProperties(network(context) ?: return null) ?: return null
        val v4 = props.linkAddresses.firstOrNull { it.address is java.net.Inet4Address } ?: return null
        v4.address.address to v4.prefixLength
    }.getOrNull()

    /** On a Windows PC's Mobile Hotspot (any PC's: Linc then still has to find which). */
    fun onPcHotspot(context: Context): Boolean = gateway(context) == WINDOWS_HOTSPOT_GATEWAY

    fun radioOn(context: Context): Boolean = runCatching {
        context.applicationContext.getSystemService(WifiManager::class.java)?.isWifiEnabled == true
    }.getOrDefault(false)

    /** Signal bars (0-4) and link speed in Mbps, or null off Wi-Fi. */
    fun signal(context: Context): Pair<Int, Int>? = runCatching {
        val cm = context.getSystemService(ConnectivityManager::class.java) ?: return null
        val caps = cm.getNetworkCapabilities(network(context) ?: return null) ?: return null
        val info = caps.transportInfo as? WifiInfo
        val rssi = info?.rssi?.takeIf { it > -127 } ?: caps.signalStrength.takeIf { it != Int.MIN_VALUE } ?: return null
        @Suppress("DEPRECATION")
        val level = WifiManager.calculateSignalLevel(rssi, 5)
        level to (info?.linkSpeed?.takeIf { it > 0 } ?: 0)
    }.getOrNull()
}
