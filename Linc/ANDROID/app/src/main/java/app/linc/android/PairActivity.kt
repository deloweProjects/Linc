package app.linc.android

import android.app.Activity
import android.app.AlertDialog
import android.os.Bundle
import android.widget.Toast
import app.linc.android.service.LogStore
import app.linc.android.service.link.LinkPairing
import app.linc.android.service.link.LinkPairingClient
import app.linc.android.service.link.LinkStore
import app.linc.android.service.link.PairingInvite
import app.linc.android.ui.restartCompanionIfRunning
import app.linc.android.ui.startCompanion

/**
 * Opened by the phone's camera (or any QR reader) from the code Linc on the PC shows:
 * `linc://pair?...` (v32, linclink.md §2). A link anyone could send, so it asks first, showing
 * the six digits the PC shows under its code (a link not scanned from the owner's own PC shows
 * digits their PC isn't showing), and then pairs over the network with no cable. The PC's
 * identity is checked against the code; nothing is trusted on the link's word.
 *
 * The screen closes the moment the owner answers; the pairing runs on in the background and says
 * how it went with a toast.
 */
class PairActivity : Activity() {
    private var dialog: AlertDialog? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        LogStore.init(this)
        val invite = LinkPairing.parse(intent?.dataString)
        if (invite == null) {
            Toast.makeText(this, "That isn't a Linc pairing code", Toast.LENGTH_SHORT).show()
            finish()
            return
        }
        val engineOff = !LinkStore.engineOn(this)
        val code = LinkPairing.checkCode(invite.pcCertSha256, invite.token)
        val message = buildString {
            append("Check that Linc on ${invite.name} shows this same code under its QR code:\n\n")
            append(code)
            append("\n\nPair only if it does. This phone and the PC will then connect on their own, with no cable.")
            if (engineOff) append("\n\nThis also turns on the new connection engine (Settings › Connection).")
        }
        dialog = AlertDialog.Builder(this)
            .setTitle("Pair with ${invite.name}?")
            .setMessage(message)
            .setPositiveButton("Pair") { _, _ -> pair(invite, engineOff) }
            .setNegativeButton("Cancel") { _, _ -> finish() }
            .setOnCancelListener { finish() }
            .show()
    }

    private fun pair(invite: PairingInvite, engineWasOff: Boolean) {
        if (engineWasOff) {
            // The owner agreed above. A service already running without the engine has no
            // listener for the PC to dial, so it restarts with it.
            LinkStore.setEngineOn(this, true)
            restartCompanionIfRunning(this)
        }
        startCompanion(this) // allowed now: this screen is in the foreground
        Toast.makeText(this, "Pairing with ${invite.name}…", Toast.LENGTH_SHORT).show()
        val app = applicationContext
        LinkPairingClient.pairInBackground(app, invite) { name ->
            Toast.makeText(
                app,
                if (name != null) "Paired with $name. They'll connect on their own from now on." else
                    "Couldn't reach ${invite.name}. Check both are on the same Wi-Fi, then show the code again on the PC.",
                Toast.LENGTH_LONG,
            ).show()
        }
        finish() // nothing of this see-through screen stays over whatever the owner does next
    }

    override fun onDestroy() {
        dialog?.dismiss()
        dialog = null
        super.onDestroy()
    }
}
