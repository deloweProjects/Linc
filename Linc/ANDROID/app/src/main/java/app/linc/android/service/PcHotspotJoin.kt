package app.linc.android.service

import android.content.Context
import android.net.wifi.WifiManager
import android.net.wifi.WifiNetworkSuggestion

/**
 * The PC's own hotspot, joined by itself (LincLink brief step 5, 2026-11-01). While connected the
 * PC sends its hotspot's name and password (`link.hotspot`, never over plain ADB Wi-Fi), once and
 * again only when they change. This hands them to Android as a **suggested network**: Android asks
 * the owner once whether Linc may suggest networks, then joins the PC's hotspot by itself whenever it
 * appears and nothing better is around, keeping mobile data for the internet if the hotspot has none.
 * When the PC hears this phone over Bluetooth with no link, it starts the hotspot; LincLink's beacon
 * and the PC's own dial then find each other on it.
 *
 * The password is kept in Linc's private storage only, beside what Android itself keeps for a
 * suggestion.
 */
object PcHotspotJoin {

    private const val PREFS = "linc.pc_hotspot"
    private const val KEY_SSID = "ssid"
    private const val KEY_PASSWORD = "password"

    /** Pure: is this a network worth suggesting, and is it different from the one already held? */
    fun isNew(held: Pair<String, String>?, ssid: String, password: String): Boolean =
        ssid.isNotBlank() && password.length >= 8 && held != (ssid to password)

    fun held(context: Context): Pair<String, String>? {
        val p = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val ssid = p.getString(KEY_SSID, null) ?: return null
        val password = p.getString(KEY_PASSWORD, null) ?: return null
        return ssid to password
    }

    /** The PC's `link.hotspot`. */
    fun onDetails(context: Context, ssid: String, password: String) {
        val held = held(context)
        if (!isNew(held, ssid, password)) return
        val wifi = context.applicationContext.getSystemService(WifiManager::class.java) ?: return
        runCatching {
            held?.let { (oldSsid, oldPassword) -> wifi.removeNetworkSuggestions(listOf(suggestion(oldSsid, oldPassword))) }
            val status = wifi.addNetworkSuggestions(listOf(suggestion(ssid, password)))
            if (status == WifiManager.STATUS_NETWORK_SUGGESTIONS_SUCCESS ||
                status == WifiManager.STATUS_NETWORK_SUGGESTIONS_ERROR_ADD_DUPLICATE) {
                context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
                    .putString(KEY_SSID, ssid).putString(KEY_PASSWORD, password).apply()
                LogStore.log(LogLevel.INFO, "Learnt your PC's hotspot \"$ssid\": the phone joins it by itself when the PC turns it on (Android may ask once)")
            } else {
                LogStore.log(LogLevel.WARN, "Android didn't take your PC's hotspot as a suggested network (code $status)")
            }
        }.onFailure { LogStore.log(LogLevel.WARN, "Couldn't save your PC's hotspot (${it.javaClass.simpleName})") }
    }

    private fun suggestion(ssid: String, password: String): WifiNetworkSuggestion =
        WifiNetworkSuggestion.Builder()
            .setSsid(ssid)
            .setWpa2Passphrase(password)
            .setIsAppInteractionRequired(false)
            .build()
}
