package app.linc.android.service

import android.annotation.SuppressLint
import android.app.ActivityManager
import android.app.ApplicationExitInfo
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.Uri
import android.net.wifi.WifiManager
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Keeps the PC link alive on Wi-Fi, and writes down what happens to it (2026-10-10: "wireless
 * connects and drops" on a friend's phone).
 *
 * The usual reasons a phone drops a Wi-Fi link to the PC are the phone's own power saving:
 * battery optimisation (and the maker's harsher version of it) freezing the app with the screen
 * off, Doze, and Wi-Fi power save. So while the companion service runs this:
 *  - holds a high-performance Wi-Fi lock (Android 13 and older honour it for a foreground service;
 *    newer ones ignore it, harmlessly);
 *  - logs the screen going off/on, Doze, battery saver and Wi-Fi coming and going, so a copied log
 *    shows whether a drop lines up with one of them;
 *  - logs whether Linc is exempt from battery optimisation — the fix the user can apply in
 *    Settings → "Stay connected in the background".
 */
class ConnectionWatch(private val context: Context) {

    private var wifiLock: WifiManager.WifiLock? = null
    private var network: ConnectivityManager.NetworkCallback? = null

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(c: Context, intent: Intent) {
            val power = c.getSystemService(PowerManager::class.java)
            when (intent.action) {
                Intent.ACTION_SCREEN_OFF -> log("Screen off")
                Intent.ACTION_SCREEN_ON -> {
                    log("Screen on")
                    ReconnectSignal.poke("screen on")
                }
                PowerManager.ACTION_DEVICE_IDLE_MODE_CHANGED ->
                    log(if (power?.isDeviceIdleMode == true) "Phone entered Doze (deep sleep)" else "Phone left Doze")
                PowerManager.ACTION_POWER_SAVE_MODE_CHANGED ->
                    log(if (power?.isPowerSaveMode == true) "Battery saver turned on" else "Battery saver turned off")
            }
        }
    }

    fun start() {
        log("Background: ${if (isBatteryExempt(context)) "Linc is exempt from battery optimisation" else
            "Linc is NOT exempt from battery optimisation — Android may cut the PC connection when the screen is off (Settings → Stay connected in the background)"}")
        runCatching {
            context.registerReceiver(receiver, IntentFilter().apply {
                addAction(Intent.ACTION_SCREEN_OFF)
                addAction(Intent.ACTION_SCREEN_ON)
                addAction(PowerManager.ACTION_DEVICE_IDLE_MODE_CHANGED)
                addAction(PowerManager.ACTION_POWER_SAVE_MODE_CHANGED)
            })
        }
        runCatching {
            val callback = object : ConnectivityManager.NetworkCallback() {
                override fun onAvailable(n: Network) {
                    log("Wi-Fi connected")
                    ReconnectSignal.poke("Wi-Fi connected")
                }
                override fun onLost(n: Network) = log("Wi-Fi lost")
            }
            context.getSystemService(ConnectivityManager::class.java).registerNetworkCallback(
                NetworkRequest.Builder().addTransportType(NetworkCapabilities.TRANSPORT_WIFI).build(), callback)
            network = callback
        }
        wifiLock = runCatching {
            @Suppress("DEPRECATION")
            context.applicationContext.getSystemService(WifiManager::class.java)
                .createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "linc:link")
                .apply { setReferenceCounted(false); acquire() }
        }.getOrNull()
    }

    fun stop() {
        runCatching { context.unregisterReceiver(receiver) }
        network?.let { cb -> runCatching { context.getSystemService(ConnectivityManager::class.java).unregisterNetworkCallback(cb) } }
        network = null
        runCatching { wifiLock?.release() }
        wifiLock = null
    }

    private fun log(message: String) = LogStore.log(LogLevel.INFO, message)

    companion object {
        fun isBatteryExempt(context: Context): Boolean =
            context.getSystemService(PowerManager::class.java)?.isIgnoringBatteryOptimizations(context.packageName) == true

        /**
         * Phone makers whose own battery managers stop background apps beyond what Android does,
         * and the screen where the user lets Linc run. Tried in order; the first that exists opens.
         */
        private val makerScreens: Map<String, List<Pair<String, String>>> = mapOf(
            // itel, Tecno, Infinix (Transsion HiOS / XOS): Phone Master's auto-start list.
            "transsion" to listOf(
                "com.transsion.phonemaster" to "com.cyin.himgr.autostart.AutoStartActivity",
                "com.transsion.phonemanager" to "com.itel.autobootmanager.activity.AutoBootMgrActivity",
            ),
            "xiaomi" to listOf("com.miui.securitycenter" to "com.miui.permcenter.autostart.AutoStartManagementActivity"),
            "oppo" to listOf("com.coloros.safecenter" to "com.coloros.safecenter.permission.startup.StartupAppListActivity"),
            "vivo" to listOf("com.vivo.permissionmanager" to "com.vivo.permissionmanager.activity.BgStartUpManagerActivity"),
        )

        private fun makerKey(): String? {
            val m = Build.MANUFACTURER.lowercase()
            return when {
                m in setOf("itel", "tecno", "infinix", "transsion") -> "transsion"
                m in setOf("xiaomi", "redmi", "poco") -> "xiaomi"
                m in setOf("oppo", "realme", "oneplus") -> "oppo"
                m == "vivo" || m == "iqoo" -> "vivo"
                else -> null
            }
        }

        /** True on phones whose maker adds its own background killer on top of Android's. */
        val hasMakerBackgroundManager: Boolean get() = makerKey() != null

        /**
         * The maker's own "allow in background / auto-start" screen, if this phone has one.
         * Returns false if none could be opened (then app details is the fallback).
         */
        fun openMakerBackgroundSettings(context: Context): Boolean {
            val screens = makerKey()?.let { makerScreens[it] } ?: emptyList()
            for ((pkg, cls) in screens) {
                val opened = runCatching {
                    context.startActivity(Intent().setClassName(pkg, cls).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                }.isSuccess
                if (opened) return true
            }
            return runCatching {
                context.startActivity(
                    Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:${context.packageName}"))
                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                )
            }.isSuccess
        }

        /** Android's own "let Linc run in the background" prompt. */
        @SuppressLint("BatteryLife") // sideloaded companion whose whole job is a standing link
        fun requestBatteryExemption(context: Context) {
            val ask = Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:${context.packageName}"))
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            runCatching { context.startActivity(ask) }.onFailure {
                runCatching {
                    context.startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                }
            }
        }

        /**
         * A plain-text report to copy from Logs and send to whoever is helping: the phone and
         * app, the background settings that matter for a Wi-Fi link, why Android ended Linc's
         * recent runs, and the log itself (including earlier runs).
         */
        fun report(context: Context): String = buildString {
            val stamp = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US)
            val pkg = runCatching { context.packageManager.getPackageInfo(context.packageName, 0) }.getOrNull()
            val power = context.getSystemService(PowerManager::class.java)
            appendLine("Linc phone log — ${stamp.format(Date())}")
            appendLine("App: ${pkg?.versionName} (${pkg?.longVersionCode})")
            appendLine("${app.linc.android.Provenance.banner(pkg?.versionName ?: "?")} (${app.linc.android.Provenance.COPYRIGHT})")
            appendLine("Phone: ${Build.MANUFACTURER} ${Build.MODEL}, Android ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT})")
            appendLine("Connection: ${CompanionStateHolder.state.value}")
            appendLine("Background connection setting: ${if (TransportStore.presenceEnabled(context)) "on" else "off"}")
            appendLine("Battery optimisation exempt: ${isBatteryExempt(context)}")
            appendLine("Battery saver: ${power?.isPowerSaveMode}, Doze now: ${power?.isDeviceIdleMode}")
            val bucket = runCatching {
                context.getSystemService(android.app.usage.UsageStatsManager::class.java).appStandbyBucket
            }.getOrNull()
            appendLine("App standby bucket: $bucket (10 active, 20 working set, 30 frequent, 40 rare, 45 restricted)")
            appendLine()
            appendLine("Why Android ended Linc recently (newest first):")
            val exits = runCatching {
                context.getSystemService(ActivityManager::class.java).getHistoricalProcessExitReasons(context.packageName, 0, 8)
            }.getOrDefault(emptyList())
            if (exits.isEmpty()) appendLine("  (none recorded)")
            exits.forEach { e ->
                appendLine("  ${stamp.format(Date(e.timestamp))}  ${reasonName(e.reason)}  ${e.description ?: ""}".trimEnd())
            }
            appendLine()
            appendLine("Log:")
            append(LogStore.persisted())
        }

        private fun reasonName(reason: Int): String = when (reason) {
            ApplicationExitInfo.REASON_ANR -> "not responding (ANR)"
            ApplicationExitInfo.REASON_CRASH -> "crash"
            ApplicationExitInfo.REASON_CRASH_NATIVE -> "native crash"
            ApplicationExitInfo.REASON_LOW_MEMORY -> "low memory"
            ApplicationExitInfo.REASON_EXCESSIVE_RESOURCE_USAGE -> "excessive resource use"
            ApplicationExitInfo.REASON_FREEZER -> "frozen by Android"
            ApplicationExitInfo.REASON_SIGNALED -> "killed (signal)"
            ApplicationExitInfo.REASON_USER_REQUESTED -> "stopped by the user / force stop"
            ApplicationExitInfo.REASON_USER_STOPPED -> "user stopped"
            ApplicationExitInfo.REASON_PERMISSION_CHANGE -> "permission changed"
            ApplicationExitInfo.REASON_DEPENDENCY_DIED -> "dependency died"
            ApplicationExitInfo.REASON_EXIT_SELF -> "exited itself"
            ApplicationExitInfo.REASON_INITIALIZATION_FAILURE -> "failed to start"
            ApplicationExitInfo.REASON_PACKAGE_UPDATED -> "app updated"
            ApplicationExitInfo.REASON_OTHER -> "other (often the maker's battery manager)"
            else -> "reason $reason"
        }
    }
}
