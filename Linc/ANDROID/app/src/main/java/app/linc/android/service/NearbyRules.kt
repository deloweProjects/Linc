package app.linc.android.service

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.bluetooth.BluetoothAdapter
import android.content.Context
import android.content.Intent
import android.provider.Settings
import androidx.core.app.NotificationCompat
import app.linc.android.R
import app.linc.android.service.link.LinkBrain
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch

/**
 * LincLink's "at home" rules on the phone (owner, 2026-11-01: "when at home auto turn on Bluetooth
 * … when such and such network is detected, laptop and phone start looking for each other").
 *
 * Home is any Wi-Fi where this phone has been linked with the PC before (remembered by gateway and
 * subnet, [LinkBrain.fingerprint]: reading Wi-Fi names would need the location permission). On
 * joining one with Bluetooth off, Linc asks to turn it on, so the two hear each other at once next
 * time. Android doesn't let apps switch Bluetooth on silently, hence one tap. Off by default.
 */
object NearbyRules {
    private const val PREFS = "linc.nearby"
    private const val KEY_HOMES = "homes"
    private const val KEY_ASK_BT = "askBluetoothAtHome"
    private const val CHANNEL_ID = "linc.nearby"
    private const val NOTIFICATION_ID = 0x1C58

    fun askBluetoothAtHome(context: Context): Boolean =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_ASK_BT, false)

    fun setAskBluetoothAtHome(context: Context, on: Boolean) =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean(KEY_ASK_BT, on).apply()

    fun homes(context: Context): Set<String> =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getStringSet(KEY_HOMES, emptySet()).orEmpty()

    fun forgetHomes(context: Context) =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().remove(KEY_HOMES).apply()

    fun currentNetwork(context: Context): String? {
        val (address, prefix) = WifiNow.address(context) ?: return null
        return LinkBrain.fingerprint(WifiNow.gateway(context), LinkBrain.subnetOf(address, prefix))
    }

    fun atHome(context: Context): Boolean = currentNetwork(context)?.let { it in homes(context) } == true

    /** Remember every Wi-Fi the phone is linked on (up to 10). */
    fun start(context: Context, scope: CoroutineScope) {
        scope.launch {
            CompanionStateHolder.state.collect { state ->
                if (state !is CompanionStateHolder.ServiceState.Connected) return@collect
                val network = currentNetwork(context) ?: return@collect
                val known = homes(context)
                if (network in known) return@collect
                context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
                    .putStringSet(KEY_HOMES, (known.toList() + network).takeLast(10).toSet()).apply()
            }
        }
    }

    /** Joined a Wi-Fi: at home with Bluetooth off, ask to turn it on. */
    fun onWifiJoined(context: Context) {
        if (!askBluetoothAtHome(context) || BlePresenceAdvertiser.bluetoothOn(context) || !atHome(context)) return
        val app = context.applicationContext
        val manager = app.getSystemService(NotificationManager::class.java) ?: return
        manager.createNotificationChannel(NotificationChannel(CHANNEL_ID, "Finding your PC", NotificationManager.IMPORTANCE_DEFAULT))
        val turnOn = if (BlePresenceAdvertiser.hasPermission(app, android.Manifest.permission.BLUETOOTH_CONNECT)) {
            Intent(BluetoothAdapter.ACTION_REQUEST_ENABLE)
        } else {
            Intent(Settings.ACTION_BLUETOOTH_SETTINGS)
        }.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        val open = PendingIntent.getActivity(app, 1, turnOn, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val notification = NotificationCompat.Builder(app, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_linc)
            .setContentTitle("You're home: turn Bluetooth on?")
            .setContentText("Your PC finds this phone faster with Bluetooth on. Tap to turn it on.")
            .setContentIntent(open)
            .setAutoCancel(true)
            .setTimeoutAfter(15 * 60_000L)
            .build()
        runCatching { manager.notify(NOTIFICATION_ID, notification) }
        LogStore.log(LogLevel.INFO, "On a Wi-Fi where you use your PC, with Bluetooth off: asked to turn it on")
    }
}
