package app.linc.android.service

import android.app.AppOpsManager
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.WallpaperManager
import android.app.usage.UsageEvents
import android.app.usage.UsageStatsManager
import android.content.Context
import android.content.Intent
import android.graphics.BitmapFactory
import android.provider.Settings
import androidx.core.app.NotificationCompat
import app.linc.android.CaptureConsentActivity
import app.linc.android.R
import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import java.util.Calendar
import kotlin.concurrent.thread
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.put

/**
 * The PC's Device page reaching into the phone (v28): this phone's sound played on the PC,
 * today's screen time, and a wallpaper chosen on the PC. Each runs off the control loop.
 */
object PhoneReach {

    private fun context(): Context? = AudioEverywhere.context()

    // ---- this phone's sound on the PC ---------------------------------------------------------

    /**
     * Start (or stop) sending what this phone plays to the PC. Android only lets an app record
     * other apps' sound after its own consent prompt, so "on" opens that prompt on the phone —
     * straight away when Linc may show itself over other apps, else as a notification to tap.
     */
    fun soundAsk(payload: JsonObject) {
        val context = context() ?: return
        val on = (payload["on"] as? JsonPrimitive)?.booleanOrNull ?: true
        if (!on) {
            if (PhoneSource.state.value.on) PhoneSource.stop(context, "stopped from the PC")
            return
        }
        if (PhoneSource.state.value.on) return
        LogStore.log(LogLevel.INFO, "Phone sound: the PC asked for this phone's sound")
        val consent = Intent(context, CaptureConsentActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (PcVoiceAccess.hasMicPermission(context) && Settings.canDrawOverlays(context)) {
            runCatching { context.startActivity(consent); return }
        }
        notify(context, consent)
    }

    private fun notify(context: Context, consent: Intent) {
        val manager = context.getSystemService(NotificationManager::class.java) ?: return
        manager.createNotificationChannel(NotificationChannel(CHANNEL, "Requests from your PC", NotificationManager.IMPORTANCE_HIGH))
        val target = if (PcVoiceAccess.hasMicPermission(context)) consent
        else Intent(context, app.linc.android.MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        val tap = PendingIntent.getActivity(context, 7, target, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        runCatching {
            manager.notify(SOUND_NOTIFICATION, NotificationCompat.Builder(context, CHANNEL)
                .setSmallIcon(R.drawable.ic_stat_linc)
                .setContentTitle("Play this phone's sound on your PC?")
                .setContentText(if (PcVoiceAccess.hasMicPermission(context)) "Tap, then allow Android's recording prompt. Linc records the sound only, never the screen."
                    else "Open Linc and allow the microphone first, then try again from the PC.")
                .setContentIntent(tap)
                .setAutoCancel(true)
                .setPriority(NotificationCompat.PRIORITY_HIGH)
                .build())
        }
    }

    // ---- screen time ----------------------------------------------------------------------------

    /** Whether Android lets Linc read app usage (the PC can grant it over USB debugging). */
    fun usageAllowed(context: Context): Boolean {
        val ops = context.getSystemService(AppOpsManager::class.java) ?: return false
        return ops.unsafeCheckOpNoThrow(AppOpsManager.OPSTR_GET_USAGE_STATS, android.os.Process.myUid(), context.packageName) ==
            AppOpsManager.MODE_ALLOWED
    }

    /** Today's screen time, per app, most first — what Digital Wellbeing shows. */
    fun usage() {
        val context = context() ?: return
        thread(name = "linc-usage", isDaemon = true) {
            val payload = runCatching { usagePayload(context) }.getOrElse { e ->
                LogStore.log(LogLevel.WARN, "Screen time: couldn't read it (${e.message})")
                buildJsonObject { put("allowed", usageAllowed(context)); put("error", "Android wouldn't give the screen time.") }
            }
            CompanionOutbox.trySend(Envelope(type = MessageType.PHONE_USAGE, payload = payload), requiredVersion = 28)
        }
    }

    private fun usagePayload(context: Context): JsonObject {
        if (!usageAllowed(context)) return buildJsonObject { put("allowed", false) }
        val stats = context.getSystemService(UsageStatsManager::class.java)
        val start = Calendar.getInstance().apply {
            set(Calendar.HOUR_OF_DAY, 0); set(Calendar.MINUTE, 0); set(Calendar.SECOND, 0); set(Calendar.MILLISECOND, 0)
        }.timeInMillis
        val now = System.currentTimeMillis()
        // From the events, not the daily buckets: buckets straddle midnight and over-count.
        val foreground = HashMap<String, Long>()
        val opened = HashMap<String, Long>()
        var unlocks = 0
        val events = stats.queryEvents(start, now)
        val e = UsageEvents.Event()
        while (events.hasNextEvent()) {
            events.getNextEvent(e)
            when (e.eventType) {
                UsageEvents.Event.ACTIVITY_RESUMED -> opened.putIfAbsent(e.packageName, e.timeStamp)
                UsageEvents.Event.ACTIVITY_PAUSED, UsageEvents.Event.ACTIVITY_STOPPED -> opened.remove(e.packageName)?.let {
                    foreground[e.packageName] = (foreground[e.packageName] ?: 0) + (e.timeStamp - it)
                }
                UsageEvents.Event.KEYGUARD_HIDDEN -> unlocks++
            }
        }
        for ((pkg, at) in opened) foreground[pkg] = (foreground[pkg] ?: 0) + (now - at)
        val pm = context.packageManager
        val launcher = runCatching {
            pm.resolveActivity(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME), 0)?.activityInfo?.packageName
        }.getOrNull()
        val apps = foreground.filter { (pkg, ms) -> ms >= 60_000 && pkg != launcher && pkg != "com.android.systemui" }
            .entries.sortedByDescending { it.value }
        return buildJsonObject {
            put("allowed", true)
            put("totalMin", apps.sumOf { it.value } / 60_000)
            put("unlocks", unlocks)
            put("apps", buildJsonArray {
                for ((pkg, ms) in apps.take(12)) add(buildJsonObject {
                    put("package", pkg)
                    put("name", runCatching { pm.getApplicationLabel(pm.getApplicationInfo(pkg, 0)).toString() }.getOrDefault(pkg))
                    put("minutes", ms / 60_000)
                })
            })
        }
    }

    // ---- wallpaper ------------------------------------------------------------------------------

    /** A picture from the PC as this phone's wallpaper: home screen, lock screen, or both. */
    fun wallpaper(payload: JsonObject) {
        val context = context() ?: return
        val b64 = (payload["jpeg"] as? JsonPrimitive)?.contentOrNull ?: return
        val target = (payload["target"] as? JsonPrimitive)?.contentOrNull ?: "both"
        thread(name = "linc-wallpaper", isDaemon = true) {
            runCatching {
                val bytes = android.util.Base64.decode(b64, android.util.Base64.DEFAULT)
                val bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size) ?: error("not a picture")
                val which = when (target) {
                    "home" -> WallpaperManager.FLAG_SYSTEM
                    "lock" -> WallpaperManager.FLAG_LOCK
                    else -> WallpaperManager.FLAG_SYSTEM or WallpaperManager.FLAG_LOCK
                }
                WallpaperManager.getInstance(context).setBitmap(bitmap, null, true, which)
                LogStore.log(LogLevel.INFO, "Wallpaper: set from the PC ($target)")
            }.onFailure { LogStore.log(LogLevel.WARN, "Wallpaper: couldn't set the PC's picture (${it.message})") }
        }
    }

    private const val CHANNEL = "linc.pc.requests"
    private const val SOUND_NOTIFICATION = 4_900
}
