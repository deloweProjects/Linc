package app.linc.android.service

import android.Manifest
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.SystemClock
import app.linc.android.ClipboardSyncActivity
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Phone → PC clipboard while Linc is in the background (D-092).
 *
 * Android 10+ only lets the focused app read the clipboard. But a background app that listens
 * for clipboard changes makes the system log "Denying clipboard access to <package>" each time
 * something is copied, and with READ_LOGS (granted by the PC over ADB) Linc can read that line.
 * When it appears, [ClipboardSyncActivity] takes focus for a moment, reads the clip and closes.
 * The same approach KDE Connect uses.
 *
 * Without READ_LOGS this does nothing and the old behaviour stands: the clip goes to the PC the
 * next time Linc is opened.
 */
object ClipboardWatcher {

    @Volatile
    private var thread: Thread? = null

    @Volatile
    private var process: Process? = null

    @Volatile
    private var lastTriggerMs = 0L

    /** Registered only so that the system logs a denial on every copy; it reads nothing itself. */
    private val listener = ClipboardManager.OnPrimaryClipChangedListener { }

    fun start(context: Context) {
        val app = context.applicationContext
        if (thread != null) return
        if (app.checkSelfPermission(Manifest.permission.READ_LOGS) != PackageManager.PERMISSION_GRANTED) {
            LogStore.log(LogLevel.INFO, "Clipboard: copies on the phone reach the PC when Linc is open " +
                "(connect once over USB or wireless debugging to sync them in the background)")
            return
        }
        app.getSystemService(ClipboardManager::class.java).addPrimaryClipChangedListener(listener)
        thread = Thread({ watch(app) }, "linc-clipboard-watch").apply { isDaemon = true; start() }
        LogStore.log(LogLevel.INFO, "Clipboard: syncing copies to the PC in the background")
    }

    fun stop(context: Context) {
        val running = thread ?: return
        thread = null
        context.applicationContext.getSystemService(ClipboardManager::class.java)
            .removePrimaryClipChangedListener(listener)
        process?.destroy()
        running.interrupt()
    }

    private fun watch(app: Context) {
        var backoffMs = 1_000L
        while (thread != null) {
            // Only lines from now on: an old denial must not trigger a read.
            val since = SimpleDateFormat("MM-dd HH:mm:ss.SSS", Locale.US).format(Date())
            try {
                val logcat = ProcessBuilder("logcat", "-T", since, "-v", "brief", "ClipboardService:E", "*:S")
                    .redirectErrorStream(true)
                    .start()
                process = logcat
                logcat.inputStream.bufferedReader().useLines { lines ->
                    for (line in lines) {
                        if (thread == null) break
                        if (isDenialFor(line, app.packageName)) trigger(app)
                    }
                }
            } catch (e: Exception) {
                if (thread == null) return
                LogStore.log(LogLevel.WARN, "Clipboard watcher stopped: ${e.message}")
            }
            // logcat ended under us (it can be killed); start it again, gently.
            try { Thread.sleep(backoffMs) } catch (_: InterruptedException) { return }
            backoffMs = (backoffMs * 2).coerceAtMost(30_000L)
        }
    }

    /**
     * The system's refusal to hand Linc a clip it was told had changed, e.g.
     * `E/ClipboardService( 1234): Denying clipboard access to app.linc.android, application is not
     * in focus nor is it a system service for user 0`. The package must match exactly: another
     * build such as `app.linc.android.debug` is a different app.
     */
    internal fun isDenialFor(line: String, packageName: String): Boolean =
        DENIAL.find(line)?.groupValues?.get(1) == packageName

    private fun trigger(app: Context) {
        val now = SystemClock.uptimeMillis()
        // One copy can log more than once (one line per listener); read once.
        if (now - lastTriggerMs < DEBOUNCE_MS) return
        lastTriggerMs = now
        runCatching {
            app.startActivity(
                Intent(app, ClipboardSyncActivity::class.java)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_NO_ANIMATION or
                        Intent.FLAG_ACTIVITY_EXCLUDE_FROM_RECENTS)
            )
        }.onFailure { LogStore.log(LogLevel.WARN, "Clipboard: couldn't read the new clip: ${it.message}") }
    }

    private const val DEBOUNCE_MS = 800L
    private val DENIAL = Regex("""Denying clipboard access to ([A-Za-z0-9_.]+)""")
}
