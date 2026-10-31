package app.linc.android.service

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.usage.UsageEvents
import android.app.usage.UsageStatsManager
import android.content.Context
import android.content.Intent
import android.os.PowerManager
import android.provider.Settings
import androidx.core.app.NotificationCompat
import app.linc.android.R
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/**
 * Pause for banking apps (2026-10-31; the idea from NectarLink's feature list, written from scratch
 * for Linc). Many banking and payment apps refuse to open while Developer options or USB/wireless
 * debugging are on, and Linc keeps those on. While an app the owner picked is in front, Linc turns
 * them off and puts back exactly what was there when they leave it.
 *
 * Safety first: what was on is saved (synchronously, `commit()`) BEFORE anything is turned off, so
 * a crash, a force-stop or a reboot never leaves the phone with debugging silently gone: the next
 * start of Linc's service and [BootReceiver] restore it whenever no picked app is in front.
 *
 * Needs the one-time `WRITE_SECURE_SETTINGS` grant ([AdbArming]) to switch the settings, and usage
 * access ([PhoneReach.usageAllowed]) to see which app is in front; the PC grants both over the cable.
 * Linc has no accessibility service of its own, so there is none to switch off.
 */
object BankingPause {

    private const val PREFS = "linc.banking_pause"
    private const val KEY_APPS = "apps"
    private const val KEY_PENDING = "pending"
    private const val KEY_FOR = "paused_for"
    private const val KEY_DEV = "prev_dev"
    private const val KEY_ADB = "prev_adb"
    private const val KEY_WIFI = "prev_wifi"
    private const val POLL_MS = 700L
    private const val NOTIFICATION_ID = 4_900
    private const val CHANNEL_ID = "linc.banking_pause"

    enum class Action { None, Pause, Restore }

    /**
     * Pure: what to do now. [foreground] null means "Android didn't say" (no app switch lately):
     * keep things as they are. Leaving a picked app for another picked app stays paused.
     */
    fun decide(foreground: String?, chosen: Set<String>, paused: Boolean): Action = when {
        paused && chosen.isEmpty() -> Action.Restore
        foreground == null -> Action.None
        foreground in chosen -> if (paused) Action.None else Action.Pause
        paused -> Action.Restore
        else -> Action.None
    }

    private fun prefs(context: Context) = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    fun apps(context: Context): Set<String> = prefs(context).getStringSet(KEY_APPS, emptySet()).orEmpty().toSet()

    fun setApps(context: Context, apps: Set<String>) {
        prefs(context).edit().putStringSet(KEY_APPS, apps.toSet()).apply()
    }

    fun isPaused(context: Context): Boolean = prefs(context).getBoolean(KEY_PENDING, false)

    fun pausedFor(context: Context): String? = prefs(context).getString(KEY_FOR, null)

    /** Both grants held: the feature can work. */
    fun canWork(context: Context): Boolean = AdbArming.isPermissionHeld(context) && PhoneReach.usageAllowed(context)

    /** The watcher, for CompanionService's scope. Cheap when nothing is picked: one prefs read a poll. */
    fun start(context: Context, scope: CoroutineScope) {
        val app = context.applicationContext
        restoreIfStale(app, "Linc started")
        scope.launch {
            val power = app.getSystemService(PowerManager::class.java)
            while (isActive) {
                delay(POLL_MS)
                val chosen = apps(app)
                val paused = isPaused(app)
                if (chosen.isEmpty() && !paused) continue
                if (!power.isInteractive && !paused) continue
                val front = runCatching { foregroundPackage(app) }.getOrNull()
                when (decide(front, chosen, paused)) {
                    Action.Pause -> pause(app, front!!)
                    Action.Restore -> restore(app, if (chosen.isEmpty()) "the list is empty" else "you left ${label(app, pausedFor(app))}")
                    Action.None -> Unit
                }
            }
        }
    }

    /**
     * After a crash or reboot: something is still switched off, and no picked app is known to be in
     * front, so put it back. Also from [BootReceiver], before anything else.
     */
    fun restoreIfStale(context: Context, why: String) {
        if (!isPaused(context)) return
        val front = runCatching { foregroundPackage(context) }.getOrNull()
        if (front == null || front !in apps(context)) restore(context, why)
    }

    private fun pause(context: Context, pkg: String) {
        if (!AdbArming.isPermissionHeld(context)) return // can't switch them; nothing changed
        val resolver = context.contentResolver
        val dev = Settings.Global.getInt(resolver, Settings.Global.DEVELOPMENT_SETTINGS_ENABLED, 0)
        val adb = Settings.Global.getInt(resolver, Settings.Global.ADB_ENABLED, 0)
        val wifi = Settings.Global.getInt(resolver, AdbArmRules.SETTING_NAME, 0)
        if (dev == 0 && adb == 0 && wifi == 0) return // already off: nothing to hide or restore
        // Saved before anything changes, and synchronously, so a crash can always undo it.
        val saved = prefs(context).edit()
            .putBoolean(KEY_PENDING, true).putString(KEY_FOR, pkg)
            .putInt(KEY_DEV, dev).putInt(KEY_ADB, adb).putInt(KEY_WIFI, wifi)
            .commit()
        if (!saved) return
        runCatching {
            Settings.Global.putInt(resolver, AdbArmRules.SETTING_NAME, 0)
            Settings.Global.putInt(resolver, Settings.Global.ADB_ENABLED, 0)
            Settings.Global.putInt(resolver, Settings.Global.DEVELOPMENT_SETTINGS_ENABLED, 0)
        }.onFailure { LogStore.log(LogLevel.WARN, "Pause for banking apps: Android refused a setting (${it.javaClass.simpleName})") }
        val name = label(context, pkg)
        LogStore.log(LogLevel.INFO, "Paused Developer options and debugging while $name is open; they come back when you leave it")
        notify(context, "Paused for $name", "Developer options and debugging are off while $name is open. Linc turns them back on when you leave it.")
    }

    private fun restore(context: Context, why: String) {
        val p = prefs(context)
        if (!p.getBoolean(KEY_PENDING, false)) return
        val resolver = context.contentResolver
        val ok = runCatching {
            // Developer options first: the debugging switches live under it.
            Settings.Global.putInt(resolver, Settings.Global.DEVELOPMENT_SETTINGS_ENABLED, p.getInt(KEY_DEV, 1))
            Settings.Global.putInt(resolver, Settings.Global.ADB_ENABLED, p.getInt(KEY_ADB, 1))
            Settings.Global.putInt(resolver, AdbArmRules.SETTING_NAME, p.getInt(KEY_WIFI, 0))
        }.isSuccess
        if (!ok) {
            LogStore.log(LogLevel.WARN, "Pause for banking apps: couldn't turn Developer options and debugging back on; turn them on in Settings")
            notify(context, "Turn debugging back on", "Linc couldn't switch Developer options and debugging back on. Open Settings › System › Developer options.")
            return
        }
        p.edit().putBoolean(KEY_PENDING, false).remove(KEY_FOR).commit()
        LogStore.log(LogLevel.INFO, "Developer options and debugging are back on ($why)")
        runCatching { context.getSystemService(NotificationManager::class.java).cancel(NOTIFICATION_ID) }
    }

    /** The package most recently brought to the front in the last 10 s, or null if none moved. */
    private fun foregroundPackage(context: Context): String? {
        val usage = context.getSystemService(UsageStatsManager::class.java) ?: return null
        val now = System.currentTimeMillis()
        val events = usage.queryEvents(now - 10_000, now)
        val event = UsageEvents.Event()
        var last: String? = null
        while (events.hasNextEvent()) {
            events.getNextEvent(event)
            if (event.eventType == UsageEvents.Event.ACTIVITY_RESUMED) last = event.packageName
        }
        return last
    }

    fun label(context: Context, pkg: String?): String = pkg?.let {
        runCatching {
            val pm = context.packageManager
            pm.getApplicationLabel(pm.getApplicationInfo(it, 0)).toString()
        }.getOrDefault(it)
    } ?: "the app"

    /** Launchable apps for the picker, by label, without Linc itself. */
    fun launchableApps(context: Context): List<Pair<String, String>> {
        val pm = context.packageManager
        val launcher = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
        return pm.queryIntentActivities(launcher, 0)
            .map { it.activityInfo.packageName to it.loadLabel(pm).toString() }
            .filter { it.first != context.packageName }
            .distinctBy { it.first }
            .sortedBy { it.second.lowercase() }
    }

    private fun notify(context: Context, title: String, text: String) {
        val manager = context.getSystemService(NotificationManager::class.java) ?: return
        runCatching {
            manager.createNotificationChannel(
                NotificationChannel(CHANNEL_ID, "Pause for banking apps", NotificationManager.IMPORTANCE_LOW).apply {
                    description = "Shows while Linc has paused debugging for a banking app"
                }
            )
            manager.notify(NOTIFICATION_ID, NotificationCompat.Builder(context, CHANNEL_ID)
                .setSmallIcon(R.drawable.ic_stat_linc)
                .setContentTitle(title)
                .setContentText(text)
                .setStyle(NotificationCompat.BigTextStyle().bigText(text))
                .setSubText("Linc")
                .setOnlyAlertOnce(true)
                .build())
        }
    }
}
