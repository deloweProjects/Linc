package app.linc.android.service

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioManager
import android.media.MediaPlayer
import android.media.RingtoneManager
import android.os.Handler
import android.os.Looper

/**
 * "Where's my phone?" ring (docs/PROTOCOL.md v7 `device.locate`): loops the default
 * ringtone on the alarm stream at maximum volume — audible even in silent mode, which
 * only mutes the ring stream — restoring the previous alarm volume afterwards.
 * Toggle semantics: a second locate while ringing stops it; otherwise it auto-stops
 * after 30 seconds.
 */
object LocateRinger {

    private const val AUTO_STOP_MS = 30_000L

    private val handler = Handler(Looper.getMainLooper())
    private var player: MediaPlayer? = null
    private var savedAlarmVolume = -1
    private val autoStop = Runnable { stop() }
    private var appContext: Context? = null

    /** Starts ringing, or stops if already ringing. Returns false when starting failed. */
    fun toggle(context: Context): Boolean {
        if (player != null) {
            stop()
            return true
        }
        return start(context.applicationContext)
    }

    private fun start(context: Context): Boolean = try {
        val audio = context.getSystemService(AudioManager::class.java)
        savedAlarmVolume = audio.getStreamVolume(AudioManager.STREAM_ALARM)
        audio.setStreamVolume(
            AudioManager.STREAM_ALARM, audio.getStreamMaxVolume(AudioManager.STREAM_ALARM), 0)

        val tone = RingtoneManager.getDefaultUri(RingtoneManager.TYPE_RINGTONE)
            ?: RingtoneManager.getDefaultUri(RingtoneManager.TYPE_ALARM)
        player = MediaPlayer().apply {
            setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_ALARM)
                    .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
                    .build())
            setDataSource(context, tone)
            isLooping = true
            prepare()
            start()
        }
        appContext = context
        handler.postDelayed(autoStop, AUTO_STOP_MS)
        LogStore.log(LogLevel.INFO, "Locate ring started from the PC")
        true
    } catch (_: Exception) {
        stop()
        false
    }

    private fun stop() {
        handler.removeCallbacks(autoStop)
        runCatching { player?.stop() }
        runCatching { player?.release() }
        player = null
        val context = appContext
        if (savedAlarmVolume >= 0 && context != null) {
            runCatching {
                context.getSystemService(AudioManager::class.java)
                    .setStreamVolume(AudioManager.STREAM_ALARM, savedAlarmVolume, 0)
            }
            LogStore.log(LogLevel.INFO, "Locate ring stopped")
        }
        savedAlarmVolume = -1
        appContext = null
    }
}
