package app.linc.android

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.os.Bundle
import android.widget.Toast
import app.linc.android.service.CompanionService
import app.linc.android.service.LogLevel
import app.linc.android.service.LogStore
import app.linc.android.service.link.LinkPairing
import app.linc.android.service.link.LinkPairingClient
import app.linc.android.service.link.LinkStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Opened by the phone's camera (or any QR reader) from the code Linc on the PC shows:
 * `linc://pair?...` (v32, linclink.md §2). A link anyone could send, so it asks first — pair only
 * with a PC whose screen shows this code right now — and then pairs over the network with no
 * cable. The PC's identity is checked against the code; nothing is trusted on the link's word.
 */
class PairActivity : Activity() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        LogStore.init(this)
        val invite = LinkPairing.parse(intent?.dataString)
        if (invite == null) {
            Toast.makeText(this, "That isn't a Linc pairing code", Toast.LENGTH_SHORT).show()
            finish()
            return
        }
        AlertDialog.Builder(this)
            .setTitle("Pair with ${invite.name}?")
            .setMessage("Only if Linc on ${invite.name} is showing this code right now. This phone and the PC will then connect on their own, with no cable.")
            .setPositiveButton("Pair") { _, _ -> pair(invite) }
            .setNegativeButton("Cancel") { _, _ -> finish() }
            .setOnCancelListener { finish() }
            .show()
    }

    private fun pair(invite: app.linc.android.service.link.PairingInvite) {
        LinkStore.setEngineOn(this, true) // pairing without a cable is the new engine's
        LinkStore.setUserStopped(this, false)
        // Allowed now: this activity is in the foreground.
        runCatching { startForegroundService(Intent(this, CompanionService::class.java)) }
            .onFailure { LogStore.log(LogLevel.WARN, "Pairing couldn't start Linc's service: ${it.message}") }
        Toast.makeText(this, "Pairing with ${invite.name}…", Toast.LENGTH_SHORT).show()
        scope.launch {
            val name = withContext(Dispatchers.IO) { LinkPairingClient.pair(applicationContext, invite) }
            Toast.makeText(
                applicationContext,
                if (name != null) "Paired with $name. They'll connect on their own from now on." else
                    "Couldn't reach ${invite.name}. Check both are on the same Wi-Fi, then show the code again on the PC.",
                Toast.LENGTH_LONG,
            ).show()
            finish()
        }
    }

    override fun onDestroy() {
        // The pairing itself is bounded (a minute); leaving the screen doesn't need to cancel it.
        super.onDestroy()
    }
}
