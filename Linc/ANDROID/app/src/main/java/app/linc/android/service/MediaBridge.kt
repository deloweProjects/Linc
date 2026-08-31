package app.linc.android.service

import android.content.ComponentName
import android.content.Context
import android.graphics.Bitmap
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Handler
import android.os.Looper
import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import app.linc.android.protocol.Topic
import java.io.ByteArrayOutputStream
import java.security.MessageDigest
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/**
 * Watches the phone's media sessions and mirrors the primary one to the desktop as
 * `media.state` events (PROTOCOL.md v6, topic `media`), executing `media.control`
 * transport commands in return. Uses `MediaSessionManager`, which notification access
 * (already required for the notification bridge) unlocks — no new permission. Inert
 * when that access is missing.
 */
class MediaBridge(private val context: Context) {

    private val handler = Handler(Looper.getMainLooper())
    private var manager: MediaSessionManager? = null
    private var controllers: List<MediaController> = emptyList()
    private var primary: MediaController? = null

    // Last few album arts by content-hash id, served over bulk as kind `albumArt`.
    private val artLock = Any()
    private val arts = object : LinkedHashMap<String, ByteArray>(8, 0.75f, true) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<String, ByteArray>) = size > 8
    }

    private val controllerCallback = object : MediaController.Callback() {
        override fun onPlaybackStateChanged(state: PlaybackState?) = electAndPublish()
        override fun onMetadataChanged(metadata: MediaMetadata?) = electAndPublish()
        override fun onSessionDestroyed() = refreshSessions()
    }

    private val sessionsListener =
        MediaSessionManager.OnActiveSessionsChangedListener { list -> onSessions(list.orEmpty()) }

    fun start() {
        val component = ComponentName(context, LincNotificationListener::class.java)
        runCatching {
            val mgr = context.getSystemService(MediaSessionManager::class.java)
            mgr.addOnActiveSessionsChangedListener(sessionsListener, component, handler)
            manager = mgr
            onSessions(mgr.getActiveSessions(component))
        } // SecurityException: notification access not granted — stay inert.
    }

    fun stop() {
        runCatching { manager?.removeOnActiveSessionsChangedListener(sessionsListener) }
        controllers.forEach { runCatching { it.unregisterCallback(controllerCallback) } }
        controllers = emptyList()
        primary = null
        manager = null
    }

    /** Sends the current state now — called when the desktop subscribes to `media`. */
    fun pushCurrent() {
        handler.post { electAndPublish() }
    }

    /** Executes a `media.control` command on the primary session (no-op without one). */
    fun control(action: String, positionMs: Long?) {
        handler.post {
            if (primary == null) {
                refreshSessions() // a session may have appeared without a list-change callback
            }
            val transport = primary?.transportControls ?: return@post
            when (action) {
                "play" -> transport.play()
                "pause" -> transport.pause()
                "next" -> transport.skipToNext()
                "prev" -> transport.skipToPrevious()
                "seek" -> positionMs?.let { transport.seekTo(it) }
            }
        }
    }

    /** Album art bytes for an `artId` handed out in `media.state`, or null. */
    fun artFor(id: String): ByteArray? = synchronized(artLock) { arts[id] }

    private fun refreshSessions() {
        val component = ComponentName(context, LincNotificationListener::class.java)
        runCatching { manager?.let { onSessions(it.getActiveSessions(component)) } }
    }

    private fun onSessions(list: List<MediaController>) {
        controllers.forEach { runCatching { it.unregisterCallback(controllerCallback) } }
        controllers = list
        controllers.forEach { it.registerCallback(controllerCallback, handler) }
        electAndPublish()
    }

    private fun electAndPublish() {
        // Sticky election: while paused, the OS's session order flaps, so re-electing
        // "first in list" every callback silently retargets controls at a different app
        // (the bug where play/next sometimes did nothing). Keep the current primary as
        // long as its session is alive; switch only when another session is playing.
        val playing = controllers.firstOrNull { it.playbackState?.state == PlaybackState.STATE_PLAYING }
        val sticky = primary?.let { current ->
            controllers.firstOrNull { it.sessionToken == current.sessionToken }
        }
        primary = playing ?: sticky ?: controllers.firstOrNull()
        publish()
    }

    private fun publish() {
        val controller = primary
        val payload = if (controller == null) {
            buildJsonObject { put("none", true) }
        } else {
            val metadata = controller.metadata
            val state = controller.playbackState
            val artId = metadata?.let { cacheArt(it) }
            buildJsonObject {
                metadata?.getString(MediaMetadata.METADATA_KEY_TITLE)?.let { put("title", it) }
                metadata?.getString(MediaMetadata.METADATA_KEY_ARTIST)?.let { put("artist", it) }
                metadata?.getString(MediaMetadata.METADATA_KEY_ALBUM)?.let { put("album", it) }
                artId?.let { put("artId", it) }
                state?.position?.let { put("positionMs", it) }
                metadata?.getLong(MediaMetadata.METADATA_KEY_DURATION)
                    ?.takeIf { it > 0 }?.let { put("durationMs", it) }
                put("playing", state?.state == PlaybackState.STATE_PLAYING)
                put("appPackage", controller.packageName)
            }
        }
        CompanionOutbox.trySend(
            Envelope(type = MessageType.MEDIA_STATE, payload = payload),
            requiredVersion = 6,
            topic = Topic.MEDIA,
        )
    }

    private fun cacheArt(metadata: MediaMetadata): String? {
        val bitmap = metadata.getBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART)
            ?: metadata.getBitmap(MediaMetadata.METADATA_KEY_ART)
            ?: return null
        val png = runCatching {
            val scaled = if (bitmap.width > ART_SIZE_PX)
                Bitmap.createScaledBitmap(
                    bitmap, ART_SIZE_PX, ART_SIZE_PX * bitmap.height / bitmap.width, true)
            else bitmap
            ByteArrayOutputStream().use { out ->
                scaled.compress(Bitmap.CompressFormat.PNG, 90, out)
                out.toByteArray()
            }
        }.getOrNull() ?: return null
        val id = MessageDigest.getInstance("SHA-1").digest(png)
            .joinToString("") { "%02x".format(it) }.take(16)
        synchronized(artLock) { arts[id] = png }
        return id
    }

    private companion object {
        const val ART_SIZE_PX = 256
    }
}
