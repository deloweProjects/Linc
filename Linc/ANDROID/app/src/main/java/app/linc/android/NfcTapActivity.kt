package app.linc.android

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.widget.Toast
import app.linc.android.service.CompanionService
import app.linc.android.service.CompanionStateHolder
import app.linc.android.service.LogLevel
import app.linc.android.service.LogStore
import app.linc.android.service.NfcTap
import app.linc.android.service.ReconnectSignal

/**
 * What a tap on the Linc desk tag opens. No UI of its own: make sure the companion is running,
 * tell the presence loop to dial the PC right now, say so in one toast, and get out of the way.
 */
class NfcTapActivity : Activity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        handle(intent)
        finish()
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handle(intent)
        finish()
    }

    private fun handle(intent: Intent?) {
        if (!NfcTap.isTapUri(intent?.data)) {
            return
        }
        val alreadyConnected = CompanionStateHolder.state.value is CompanionStateHolder.ServiceState.Connected
        if (alreadyConnected) {
            Toast.makeText(this, "Already connected to your PC", Toast.LENGTH_SHORT).show()
            return
        }
        // Allowed: we are a foreground activity at this moment, which is exactly the window in
        // which Android permits starting a foreground service.
        runCatching { startForegroundService(Intent(this, CompanionService::class.java)) }
            .onFailure { LogStore.log(LogLevel.WARN, "NFC tap couldn't start the companion: ${it.message}") }
        ReconnectSignal.poke("NFC tap")
        LogStore.log(LogLevel.INFO, "NFC tap: connecting to the PC now")
        Toast.makeText(this, "Connecting to your PC…", Toast.LENGTH_SHORT).show()
    }
}
