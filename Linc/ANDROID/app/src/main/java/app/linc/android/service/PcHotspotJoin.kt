package app.linc.android.service

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiManager
import android.net.wifi.WifiNetworkSpecifier
import android.net.wifi.WifiNetworkSuggestion
import android.os.SystemClock
import androidx.core.app.NotificationCompat
import app.linc.android.JoinPcHotspotActivity
import app.linc.android.R

/**
 * The PC's own hotspot, joined by itself (LincLink brief step 5, 2026-11-01). While connected the
 * PC sends its hotspot's name and password (`link.hotspot`, never over plain ADB Wi-Fi), once and
 * again only when they change. This hands them to Android as a **suggested network**: Android asks
 * the owner once whether Linc may suggest networks, then joins the PC's hotspot by itself whenever it
 * appears and nothing better is around, keeping mobile data for the internet if the hotspot has none.
 * When the PC hears this phone over Bluetooth with no link, it starts the hotspot; LincLink's beacon
 * and the PC's own dial then find each other on it.
 *
 * A suggestion alone wasn't enough (owner, 2026-11-01: "Linc doesn't request that Wi-Fi when it
 * finds it"): Android only joins a suggested network when it isn't already on another one, and
 * only once the owner has allowed suggestions. So when the PC says over Bluetooth that its hotspot
 * is on and this phone isn't on it, Linc posts "Join your PC's hotspot"; tapping it asks Android
 * to join that network now ([requestNow], Android's own one-tap dialog).
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
        // The PC sends this on every connect. A network already held is suggested again anyway:
        // Android's copy can be gone while Linc's prefs remain (owner's phone, 2026-11-01: Wi-Fi
        // had no suggestion from Linc at all), and adding a duplicate is harmless.
        if (!isNew(held, ssid, password)) {
            runCatching {
                context.applicationContext.getSystemService(WifiManager::class.java)
                    ?.addNetworkSuggestions(listOf(suggestion(ssid, password)))
            }
            return
        }
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

    private const val CHANNEL_ID = "linc.pc_hotspot"
    private const val NOTIFICATION_ID = 0x1C57

    /** At most one "join" offer per 10 minutes: the PC's beacon arrives every second. */
    private const val OFFER_COOLDOWN_MS = 10 * 60_000L
    private var lastOffer = 0L
    private var request: ConnectivityManager.NetworkCallback? = null

    /**
     * The PC said (Bluetooth) its hotspot is on and this phone isn't on it: make sure Android still
     * has the suggestion, and offer a one-tap join.
     */
    fun offerJoin(context: Context, why: String) {
        val (ssid, password) = held(context) ?: return
        val now = SystemClock.elapsedRealtime()
        if (request != null || (lastOffer != 0L && now - lastOffer < OFFER_COOLDOWN_MS)) return
        lastOffer = now
        val app = context.applicationContext
        runCatching { app.getSystemService(WifiManager::class.java)?.addNetworkSuggestions(listOf(suggestion(ssid, password))) }
        val manager = app.getSystemService(NotificationManager::class.java) ?: return
        manager.createNotificationChannel(NotificationChannel(CHANNEL_ID, "Your PC's hotspot", NotificationManager.IMPORTANCE_HIGH))
        val open = PendingIntent.getActivity(app, 0,
            Intent(app, JoinPcHotspotActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val notification = NotificationCompat.Builder(app, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_linc)
            .setContentTitle("Join your PC's hotspot?")
            .setContentText("$why. Tap to join \"$ssid\" so Linc can connect.")
            .setContentIntent(open)
            .addAction(0, "Join", open)
            .setAutoCancel(true)
            .setTimeoutAfter(OFFER_COOLDOWN_MS)
            .build()
        runCatching { manager.notify(NOTIFICATION_ID, notification) }
        LogStore.log(LogLevel.INFO, "$why; offered to join \"$ssid\" (notification)")
    }

    /**
     * Ask Android to join the PC's hotspot now (its own "Connect to …?" dialog; needs Linc on
     * screen, which the notification's tap gives). The link holds while Linc keeps the request.
     */
    fun requestNow(context: Context): Boolean {
        val (ssid, password) = held(context) ?: return false
        val app = context.applicationContext
        val cm = app.getSystemService(ConnectivityManager::class.java) ?: return false
        app.getSystemService(NotificationManager::class.java)?.cancel(NOTIFICATION_ID)
        request?.let { runCatching { cm.unregisterNetworkCallback(it) } }
        val specifier = WifiNetworkSpecifier.Builder().setSsid(ssid).setWpa2Passphrase(password).build()
        val networkRequest = NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .setNetworkSpecifier(specifier)
            .build()
        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                LogStore.log(LogLevel.INFO, "Joined your PC's hotspot \"$ssid\"; the PC finds this phone on it")
            }

            override fun onUnavailable() {
                request = null
                LogStore.log(LogLevel.INFO, "Didn't join your PC's hotspot \"$ssid\" (cancelled, or it isn't in range)")
            }

            override fun onLost(network: Network) {
                LogStore.log(LogLevel.INFO, "Left your PC's hotspot \"$ssid\"")
                runCatching { cm.unregisterNetworkCallback(this) }
                request = null
            }
        }
        return runCatching {
            cm.requestNetwork(networkRequest, callback)
            request = callback
            LogStore.log(LogLevel.INFO, "Asking Android to join your PC's hotspot \"$ssid\"")
            true
        }.getOrElse {
            LogStore.log(LogLevel.WARN, "Couldn't ask Android to join your PC's hotspot (${it.javaClass.simpleName})")
            false
        }
    }
}
