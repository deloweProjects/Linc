package app.linc.android.service

import android.app.ActivityManager
import android.app.NotificationManager
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.wifi.WifiManager
import android.os.BatteryManager
import android.os.Build
import android.os.Environment
import android.os.StatFs
import android.os.SystemClock
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import app.linc.android.protocol.PROTOCOL_VERSION
import java.io.File
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

object DeviceStatusReporter {

    // WifiInfo's documented "no signal" sentinel; not exposed as a stable public constant.
    private const val INVALID_RSSI = -127

    data class DeviceStatus(
        val battery: Int,
        val charging: Boolean,
        val storageFreeBytes: Long,
        val storageTotalBytes: Long,
    )

    fun snapshot(context: Context): DeviceStatus {
        val batteryManager = context.getSystemService(Context.BATTERY_SERVICE) as BatteryManager
        val battery = batteryManager.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY)
        // Sticky broadcast read; a null receiver needs no export flag.
        val batteryIntent = context.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        val plugged = batteryIntent?.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) ?: 0
        val stat = StatFs(Environment.getDataDirectory().path)
        return DeviceStatus(
            battery = battery,
            charging = plugged != 0,
            storageFreeBytes = stat.availableBytes,
            storageTotalBytes = stat.totalBytes,
        )
    }

    /** Current ringer mode as a protocol string; null if unreadable. */
    private fun readSoundMode(context: Context): String? = try {
        when (context.getSystemService(android.media.AudioManager::class.java).ringerMode) {
            android.media.AudioManager.RINGER_MODE_SILENT -> "silent"
            android.media.AudioManager.RINGER_MODE_VIBRATE -> "vibrate"
            else -> "normal"
        }
    } catch (_: Exception) {
        null
    }

    /** True when any interruption filter other than "allow all" is active; null if unreadable. */
    private fun readDndEnabled(context: Context): Boolean? = try {
        val filter = context.getSystemService(NotificationManager::class.java).currentInterruptionFilter
        when (filter) {
            NotificationManager.INTERRUPTION_FILTER_UNKNOWN -> null
            NotificationManager.INTERRUPTION_FILTER_ALL -> false
            else -> true
        }
    } catch (_: Exception) {
        null
    }

    /** 1-minute load average from /proc/loadavg. Some OEM/SELinux builds restrict this. */
    private fun readCpuLoad1m(): Double? = try {
        File("/proc/loadavg").readText().trim().split(" ").firstOrNull()?.toDoubleOrNull()
    } catch (_: Exception) {
        null
    }

    /** (usedBytes, totalBytes), or null if unavailable. No special permission required. */
    private fun readRam(context: Context): Pair<Long, Long>? = try {
        val am = context.getSystemService(Context.ACTIVITY_SERVICE) as ActivityManager
        val info = ActivityManager.MemoryInfo()
        am.getMemoryInfo(info)
        (info.totalMem - info.availMem) to info.totalMem
    } catch (_: Exception) {
        null
    }

    /**
     * (signalLevel 0-4, linkSpeedMbps), or null if not on Wi-Fi. Deliberately reads
     * only RSSI/link speed, never SSID — SSID requires location permission on modern
     * Android, which isn't worth asking for just to show a signal indicator.
     *
     * Gate on RSSI validity, not networkId: without location permission, Android 12+
     * redacts networkId to -1 even while connected (verified on a Pixel 7 / Android
     * 17), but rssi/linkSpeed stay valid. A genuinely disconnected radio reports
     * rssi == WifiInfo.INVALID_RSSI (-127).
     */
    private fun readWifiStats(context: Context): Pair<Int, Int>? = try {
        val wifiManager = context.applicationContext
            .getSystemService(Context.WIFI_SERVICE) as? WifiManager
        @Suppress("DEPRECATION")
        val info = wifiManager?.connectionInfo
        val rssi = info?.rssi
        // linkSpeed is -1 and rssi is the invalid sentinel (-127) when the radio
        // isn't associated; a live connection has linkSpeed > 0 and a real dBm rssi.
        if (info == null || rssi == null || rssi <= INVALID_RSSI || info.linkSpeed <= 0) {
            null
        } else {
            @Suppress("DEPRECATION")
            val level = WifiManager.calculateSignalLevel(rssi, 5)
            level to info.linkSpeed
        }
    } catch (_: Exception) {
        null
    }

    /**
     * The phone's Material You dynamic palette as hex strings, so the desktop can
     * match it exactly (docs/PROTOCOL.md v4). Role keys mirror the desktop's brush
     * names. Absent on API 30, where dynamic color doesn't exist — the desktop
     * keeps its built-in palette then, which is what the phone shows too.
     */
    private fun themePayload(scheme: ColorScheme): JsonObject = buildJsonObject {
        put("primary", hex(scheme.primary))
        put("onPrimary", hex(scheme.onPrimary))
        put("primaryContainer", hex(scheme.primaryContainer))
        put("onPrimaryContainer", hex(scheme.onPrimaryContainer))
        put("secondaryContainer", hex(scheme.secondaryContainer))
        put("onSecondaryContainer", hex(scheme.onSecondaryContainer))
        put("tertiaryContainer", hex(scheme.tertiaryContainer))
        put("onTertiaryContainer", hex(scheme.onTertiaryContainer))
        put("surface", hex(scheme.surface))
        put("onSurface", hex(scheme.onSurface))
        put("surfaceContainer", hex(scheme.surfaceContainer))
        put("surfaceContainerHigh", hex(scheme.surfaceContainerHigh))
        put("onSurfaceVariant", hex(scheme.onSurfaceVariant))
        put("outline", hex(scheme.outline))
        put("outlineVariant", hex(scheme.outlineVariant))
        put("error", hex(scheme.error))
        put("errorContainer", hex(scheme.errorContainer))
        put("onErrorContainer", hex(scheme.onErrorContainer))
    }

    private fun hex(color: Color): String = "#%06X".format(color.toArgb() and 0xFFFFFF)

    /** `status` message payload per docs/PROTOCOL.md (v3 adds cpu/ram/uptime/wifi; v4 adds theme). */
    fun payload(context: Context, negotiatedVersion: Int = PROTOCOL_VERSION): JsonObject {
        val status = snapshot(context)
        val ram = readRam(context)
        val wifi = readWifiStats(context)
        return buildJsonObject {
            put("battery", status.battery)
            put("charging", status.charging)
            put("storageFreeBytes", status.storageFreeBytes)
            put("storageTotalBytes", status.storageTotalBytes)
            // Reading the SSID requires fine-location permission and enabled location
            // services; not worth the permission ask, so always null for now.
            put("wifiSsid", JsonNull)
            readCpuLoad1m()?.let { put("cpuLoad1m", it) } ?: put("cpuLoad1m", JsonNull)
            if (ram != null) {
                put("ramUsedBytes", ram.first)
                put("ramTotalBytes", ram.second)
            } else {
                put("ramUsedBytes", JsonNull)
                put("ramTotalBytes", JsonNull)
            }
            put("uptimeMillis", SystemClock.elapsedRealtime())
            // v7: lets the desktop's DND toggle reflect the phone's real state.
            readDndEnabled(context)?.let { put("dndEnabled", it) } ?: put("dndEnabled", JsonNull)
            // v8: ringer mode + wallpaper thumbnail id (absent without the grant — D-021).
            readSoundMode(context)?.let { put("soundMode", it) }
            WallpaperProvider.currentId(context)?.let { put("wallpaperId", it) }
            if (wifi != null) {
                put("wifiSignalLevel", wifi.first)
                put("wifiLinkSpeedMbps", wifi.second)
            } else {
                put("wifiSignalLevel", JsonNull)
                put("wifiLinkSpeedMbps", JsonNull)
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                runCatching {
                    put("themeLight", themePayload(dynamicLightColorScheme(context)))
                    put("themeDark", themePayload(dynamicDarkColorScheme(context)))
                }
            }
            // v15 display control (D-055): three optional fields present only when the
            // negotiated version is >= 15. Settings.System reads need no permission, so
            // these are always available; a v<=14 desktop ignores them per envelope rules.
            if (negotiatedVersion >= 15) {
                runCatching {
                    val display = DisplayControl(context).readState()
                    put("rotationMode", display.rotationMode)
                    put("brightnessAuto", display.brightnessAuto)
                    put("brightnessLevel", display.brightnessLevel)
                }
            }
        }
    }
}
