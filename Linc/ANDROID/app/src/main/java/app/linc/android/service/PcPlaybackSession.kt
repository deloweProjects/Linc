package app.linc.android.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.media.AudioAttributes
import android.media.AudioFocusRequest
import android.media.AudioManager
import android.media.MediaMetadata
import android.media.session.MediaSession
import android.media.session.PlaybackState
import app.linc.android.MainActivity
import app.linc.android.R
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

/**
 * While the PC's sound plays on this phone (Play everywhere, D-097), Linc is a media player as
 * far as Android is concerned: a media session ("Playing from your PC", with the PC's own track
 * and play/pause/skip that control the PC), and audio focus. Without them Android treated the
 * sound as anonymous background audio — no entry in the media controls, nothing to say why the
 * phone was playing, and other apps' sounds were mixed in without the courtesy given to media.
 */
object PcPlaybackSession {

    private const val CHANNEL_ID = "linc.playback"
    private const val NOTIFICATION_ID = 2
    const val ACTION_MEDIA = "app.linc.android.action.PC_MEDIA"
    const val EXTRA_COMMAND = "command"

    private var session: MediaSession? = null
    private var focus: AudioFocusRequest? = null
    private var watcher: Job? = null
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    @Synchronized
    fun start(context: Context) {
        if (session != null) return
        val app = context.applicationContext
        val audio = app.getSystemService(AudioManager::class.java)
        focus = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN)
            .setAudioAttributes(AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_MEDIA)
                .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                .build())
            // A notification ducks this a little, as it would any music app; it never pauses it.
            .setWillPauseWhenDucked(false)
            .setOnAudioFocusChangeListener { change ->
                LogStore.log(LogLevel.INFO, "Play everywhere: audio focus ${focusName(change)}")
            }
            .build()
            .also { audio?.requestAudioFocus(it) }

        session = MediaSession(app, "LincPc").apply {
            setCallback(object : MediaSession.Callback() {
                override fun onPlay() = PcMediaControl.send("play")
                override fun onPause() = PcMediaControl.send("pause")
                override fun onSkipToNext() = PcMediaControl.send("next")
                override fun onSkipToPrevious() = PcMediaControl.send("prev")
            })
            isActive = true
        }
        // Follow what the PC says it is playing.
        watcher = scope.launch { PcMediaStore.state.collect { update(app, it) } }
        update(app, PcMediaStore.state.value)
        LogStore.log(LogLevel.INFO, "Play everywhere: shown as playing from the PC")
    }

    @Synchronized
    fun stop(context: Context) {
        val app = context.applicationContext
        watcher?.cancel()
        watcher = null
        session?.run { isActive = false; release() }
        session = null
        focus?.let { app.getSystemService(AudioManager::class.java)?.abandonAudioFocusRequest(it) }
        focus = null
        app.getSystemService(NotificationManager::class.java)?.cancel(NOTIFICATION_ID)
    }

    /** A button on the notification. */
    fun onCommand(command: String?) {
        if (command in setOf("play", "pause", "next", "prev")) PcMediaControl.send(command!!)
    }

    @Synchronized
    private fun update(context: Context, media: PcMedia?) {
        val s = session ?: return
        val title = media?.title?.takeIf { it.isNotBlank() } ?: "Playing from your PC"
        val artist = media?.artist?.takeIf { it.isNotBlank() } ?: media?.app ?: "Linc"
        val playing = media?.playing ?: true
        s.setMetadata(MediaMetadata.Builder()
            .putString(MediaMetadata.METADATA_KEY_TITLE, title)
            .putString(MediaMetadata.METADATA_KEY_ARTIST, artist)
            .putLong(MediaMetadata.METADATA_KEY_DURATION, media?.durationMs ?: -1)
            .build())
        s.setPlaybackState(PlaybackState.Builder()
            .setActions(PlaybackState.ACTION_PLAY or PlaybackState.ACTION_PAUSE or PlaybackState.ACTION_PLAY_PAUSE or
                PlaybackState.ACTION_SKIP_TO_NEXT or PlaybackState.ACTION_SKIP_TO_PREVIOUS)
            .setState(if (playing) PlaybackState.STATE_PLAYING else PlaybackState.STATE_PAUSED,
                media?.positionMs ?: PlaybackState.PLAYBACK_POSITION_UNKNOWN, if (playing) 1f else 0f)
            .build())

        val manager = context.getSystemService(NotificationManager::class.java) ?: return
        manager.createNotificationChannel(NotificationChannel(CHANNEL_ID, "Playing from your PC", NotificationManager.IMPORTANCE_LOW))
        fun action(icon: Int, label: String, command: String) = Notification.Action.Builder(
            android.graphics.drawable.Icon.createWithResource(context, icon), label,
            PendingIntent.getService(context, command.hashCode(),
                Intent(context, CompanionService::class.java).setAction(ACTION_MEDIA).putExtra(EXTRA_COMMAND, command),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT),
        ).build()
        val notification = Notification.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_linc)
            .setContentTitle(title)
            .setContentText(artist)
            .setSubText("Playing from your PC")
            .setOngoing(true)
            .setContentIntent(PendingIntent.getActivity(context, 0, Intent(context, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE))
            .addAction(action(android.R.drawable.ic_media_previous, "Previous", "prev"))
            .addAction(if (playing) action(android.R.drawable.ic_media_pause, "Pause", "pause")
                else action(android.R.drawable.ic_media_play, "Play", "play"))
            .addAction(action(android.R.drawable.ic_media_next, "Next", "next"))
            .setStyle(Notification.MediaStyle().setMediaSession(s.sessionToken).setShowActionsInCompactView(0, 1, 2))
            .build()
        runCatching { manager.notify(NOTIFICATION_ID, notification) }
    }

    private fun focusName(change: Int) = when (change) {
        AudioManager.AUDIOFOCUS_GAIN -> "back"
        AudioManager.AUDIOFOCUS_LOSS -> "taken by another player (still playing)"
        AudioManager.AUDIOFOCUS_LOSS_TRANSIENT -> "paused for a moment by another app (still playing)"
        AudioManager.AUDIOFOCUS_LOSS_TRANSIENT_CAN_DUCK -> "ducked for another app's sound"
        else -> change.toString()
    }
}
