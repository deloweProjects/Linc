package app.linc.android.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import app.linc.android.MainActivity
import app.linc.android.R
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.longOrNull

/** Foreground service hosting the companion socket server. */
class CompanionService : Service() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var server: SocketServer? = null
    private var media: MediaBridge? = null
    private var presence: PresenceClient? = null
    private var blePresence: BlePresenceAdvertiser? = null
    private var callWatcher: CallStateWatcher? = null

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        ServiceCompat.startForeground(
            this,
            NOTIFICATION_ID,
            buildNotification(),
            ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE,
        )
        if (server == null) {
            val mediaBridge = MediaBridge(this).also { it.start() }
            media = mediaBridge
            server = SocketServer(
                scope,
                statusPayload = { negotiatedVersion -> DeviceStatusReporter.payload(this, negotiatedVersion) },
                onSetClipboard = { text -> ClipboardBridge.applyFromDesktop(this, text) },
                onDesktopConnected = { LincNotificationListener.instance?.pushActiveNotifications() },
                onDismissNotification = { key -> LincNotificationListener.instance?.dismiss(key) },
                bulkFetch = { kind, id ->
                    when (kind) {
                        "largeIcon" -> LargeIconCache.get(id)
                        "albumArt" -> mediaBridge.artFor(id)
                        "wallpaper" -> WallpaperProvider.thumbnail(id)
                        "photo" -> PhotosProvider.thumbnail(this, id)
                        else -> BulkResources.fetch(this, kind, id)
                    }
                },
                onNotificationAction = { key, index ->
                    LincNotificationListener.instance?.fireAction(key, index) ?: false
                },
                onNotificationReply = { key, index, text ->
                    LincNotificationListener.instance?.reply(key, index, text) ?: false
                },
                onMediaControl = { action, positionMs -> mediaBridge.control(action, positionMs) },
                onMediaSubscribed = { mediaBridge.pushCurrent() },
                onLocate = { LocateRinger.toggle(this) },
                onSetDnd = { enabled -> setDnd(enabled) },
                onSetSound = { mode -> setSoundMode(mode) },
                // v15 display control (D-055): a DisplayControl owns the Settings.System writes.
                onSetRotation = { mode -> DisplayControl(this).setRotation(mode) },
                onSetBrightness = { auto, level -> DisplayControl(this).setBrightness(auto, level) },
                onTlsExchange = { certB64, tlsPort, reversePort ->
                    TransportStore.saveExchange(this, certB64, tlsPort, reversePort)
                    TlsIdentity.certificateBase64()
                },
                onChannelOpen = { channel, token ->
                    presence?.openChannel(channel, token) ?: false
                },
                fileServe = { input, output -> FileChannelServer.serve(input, output) },
                photosRecent = { limit -> PhotosProvider.recent(this, limit) },
                onContinueUrl = { url -> openUrl(url) },
                onSyncConfig = { config ->
                    for (lane in listOf("folders", "photos", "messages", "calls")) {
                        (config[lane] as? kotlinx.serialization.json.JsonPrimitive)?.booleanOrNull
                            ?.let { SyncStore.set(this, lane, it) }
                    }
                },
                smsList = { limit -> SmsProvider.recent(this, limit) },
                smsSend = { address, body -> SmsProvider.send(this, address, body) },
                callLog = { limit -> CallProvider.recent(this, limit) },
                callDial = { number -> CallProvider.dial(this, number) },
                callDecline = { CallProvider.decline(this) },
                onPcMediaState = { payload -> PcMediaStore.update(parsePcMedia(payload)) },
                // v16 (D-058): the launchable-app inventory, pulled by the desktop on connect.
                appsGet = { AppInventory.payload(this) },
                onShareIncoming = { name, path ->
                    ShareStore.addReceived(name, path)
                    LogStore.log(LogLevel.INFO, "Received a file from the PC")
                },
                // v18 (M13b): tell the PC which port adbd is accepting on, having just proved
                // it locally. Nothing here arms anything — that is M13c.
                announceAdb = { reason -> AdbAnnouncer.announceNow(this, reason) },
                onAdbAck = { ok, endpoint -> AdbAnnouncer.onAck(ok, endpoint) },
                // M13c §2.2: arm first, then announce. AdbArming is a no-op that reports
                // `denied` when the one-time WRITE_SECURE_SETTINGS grant was never made.
                onAdbArm = { reason ->
                    AdbArming.arm(this)
                    AdbAnnouncer.announceNow(this, reason)
                },
            ).also { it.start() }
            presence = PresenceClient(this, scope, server!!).also { it.start() }
            // A BLE hint costs nothing when it fails: no radio, no grant or no certificate all
            // just mean the PC never hears it (M04, D-034).
            blePresence = BlePresenceAdvertiser(this, scope).also { it.start() }
            callWatcher = CallStateWatcher(this).also { it.start() }
            LogStore.log(LogLevel.INFO, "Companion service started")
        }
        return START_STICKY
    }

    override fun onDestroy() {
        callWatcher?.stop()
        callWatcher = null
        presence?.stop()
        presence = null
        blePresence?.stop()
        blePresence = null
        server?.stop()
        server = null
        media?.stop()
        media = null
        scope.cancel()
        LogStore.log(LogLevel.INFO, "Companion service stopped")
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    /** Parses a v13 `pc.media.state` payload into a [PcMedia] snapshot. */
    private fun parsePcMedia(payload: kotlinx.serialization.json.JsonObject): PcMedia? {
        fun str(key: String) = (payload[key] as? kotlinx.serialization.json.JsonPrimitive)?.contentOrNull
        fun long(key: String) = (payload[key] as? kotlinx.serialization.json.JsonPrimitive)?.longOrNull ?: 0L
        if ((payload["none"] as? kotlinx.serialization.json.JsonPrimitive)?.booleanOrNull == true) {
            return PcMedia(null, null, playing = false, positionMs = 0, durationMs = 0, app = null, present = false)
        }
        return PcMedia(
            title = str("title"),
            artist = str("artist"),
            playing = (payload["playing"] as? kotlinx.serialization.json.JsonPrimitive)?.booleanOrNull ?: false,
            positionMs = long("positionMs"),
            durationMs = long("durationMs"),
            app = str("app"),
            present = true,
        )
    }

    /** v10 `continue.url`: open a link the desktop sent, in the phone's default browser. */
    private fun openUrl(url: String): Boolean = runCatching {
        val intent = Intent(Intent.ACTION_VIEW, android.net.Uri.parse(url))
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        startActivity(intent)
        LogStore.log(LogLevel.INFO, "Opened a link sent from the PC")
    }.isSuccess

    /** v8 `sound.set`. Ringer-mode changes need the same DND policy grant. */
    private fun setSoundMode(mode: String): Boolean {
        val notifications = getSystemService(NotificationManager::class.java)
        if (!notifications.isNotificationPolicyAccessGranted) {
            return false
        }
        val audio = getSystemService(android.media.AudioManager::class.java)
        val ringerMode = when (mode) {
            "normal" -> android.media.AudioManager.RINGER_MODE_NORMAL
            "vibrate" -> android.media.AudioManager.RINGER_MODE_VIBRATE
            "silent" -> android.media.AudioManager.RINGER_MODE_SILENT
            else -> return false
        }
        return runCatching {
            audio.ringerMode = ringerMode
            LogStore.log(LogLevel.INFO, "Sound profile set to $mode from the PC")
        }.isSuccess
    }

    /** v7 `dnd.set`. False when the user hasn't given Linc the DND policy grant. */
    private fun setDnd(enabled: Boolean): Boolean {
        val notifications = getSystemService(NotificationManager::class.java)
        if (!notifications.isNotificationPolicyAccessGranted) {
            return false
        }
        return runCatching {
            notifications.setInterruptionFilter(
                if (enabled) NotificationManager.INTERRUPTION_FILTER_PRIORITY
                else NotificationManager.INTERRUPTION_FILTER_ALL)
            LogStore.log(LogLevel.INFO, if (enabled) "Do Not Disturb turned on from the PC" else "Do Not Disturb turned off from the PC")
        }.isSuccess
    }

    private fun buildNotification(): Notification {
        val openApp = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE,
        )
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_linc)
            .setContentTitle(getString(R.string.notification_title))
            .setContentText(getString(R.string.notification_text))
            .setContentIntent(openApp)
            .setOngoing(true)
            .build()
    }

    private fun createNotificationChannel() {
        val channel = NotificationChannel(
            CHANNEL_ID,
            getString(R.string.notification_channel_name),
            NotificationManager.IMPORTANCE_LOW,
        )
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    private companion object {
        const val CHANNEL_ID = "linc.companion"
        const val NOTIFICATION_ID = 1
    }
}
