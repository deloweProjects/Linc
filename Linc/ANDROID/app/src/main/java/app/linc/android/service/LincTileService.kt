package app.linc.android.service

import android.app.PendingIntent
import android.content.Intent
import android.graphics.drawable.Icon
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import app.linc.android.MainActivity
import app.linc.android.R
import app.linc.android.service.CompanionStateHolder.ServiceState
import app.linc.android.service.link.LinkStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

/**
 * The Linc tile in Quick Settings (owner, 2026-11-03: "add a Linc tile in phone, like when it's on
 * Linc is started"). Lit while Linc runs, saying whether a PC is linked; a tap starts or stops Linc
 * exactly as the Start/Stop button on the Connection sheet does (stopping by hand is remembered, so
 * a restart or opening Linc leaves it stopped). Long-press opens Linc (Android's own behaviour).
 */
class LincTileService : TileService() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var watching: Job? = null

    override fun onStartListening() {
        super.onStartListening()
        watching?.cancel()
        watching = scope.launch { CompanionStateHolder.state.collect { show(it) } }
    }

    override fun onStopListening() {
        watching?.cancel()
        watching = null
        super.onStopListening()
    }

    override fun onDestroy() {
        watching?.cancel()
        super.onDestroy()
    }

    override fun onClick() {
        super.onClick()
        if (CompanionStateHolder.state.value is ServiceState.Stopped) {
            LinkStore.setUserStopped(this, false)
            val started = runCatching { startForegroundService(Intent(this, CompanionService::class.java)) }.isSuccess
            if (!started) openLinc() // Android refused a start from here: Linc's own screen starts it
        } else {
            LinkStore.setUserStopped(this, true)
            stopService(Intent(this, CompanionService::class.java))
        }
    }

    private fun show(state: ServiceState) {
        val tile = qsTile ?: return
        tile.icon = Icon.createWithResource(this, R.drawable.ic_stat_linc)
        tile.label = "Linc"
        tile.state = if (state is ServiceState.Stopped) Tile.STATE_INACTIVE else Tile.STATE_ACTIVE
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            tile.subtitle = when (state) {
                is ServiceState.Stopped -> "Off"
                is ServiceState.Connected -> state.host?.takeIf { it.isNotBlank() } ?: "Linked"
                else -> "Looking for your PC"
            }
        }
        tile.updateTile()
    }

    private fun openLinc() {
        val intent = Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (Build.VERSION.SDK_INT >= 34) {
            startActivityAndCollapse(PendingIntent.getActivity(this, 0, intent, PendingIntent.FLAG_IMMUTABLE))
        } else {
            @Suppress("DEPRECATION")
            startActivityAndCollapse(intent)
        }
    }
}
