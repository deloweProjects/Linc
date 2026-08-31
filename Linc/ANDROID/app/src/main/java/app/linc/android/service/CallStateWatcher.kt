package app.linc.android.service

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.telephony.TelephonyCallback
import android.telephony.TelephonyManager
import androidx.core.content.ContextCompat
import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/**
 * Watches the phone's call state and relays ringing → `call.incoming` (PROTOCOL.md v12)
 * while the Calls lane is on. Uses TelephonyCallback (API 31+); needs READ_PHONE_STATE.
 * The incoming number is withheld by the OS here, so `number` is empty (D-026).
 */
class CallStateWatcher(private val context: Context) {

    private val telephony by lazy { context.getSystemService(TelephonyManager::class.java) }
    private var callback: TelephonyCallback? = null
    private var wasRinging = false

    private inner class Callback : TelephonyCallback(), TelephonyCallback.CallStateListener {
        override fun onCallStateChanged(state: Int) {
            if (!SyncStore.isOn(context, "calls")) {
                return
            }
            when (state) {
                TelephonyManager.CALL_STATE_RINGING -> { wasRinging = true; send(true) }
                TelephonyManager.CALL_STATE_IDLE -> if (wasRinging) { wasRinging = false; send(false) }
            }
        }
    }

    fun start() {
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.READ_PHONE_STATE) !=
            PackageManager.PERMISSION_GRANTED) {
            return
        }
        runCatching {
            val cb = Callback()
            telephony.registerTelephonyCallback(context.mainExecutor, cb)
            callback = cb
        }
    }

    fun stop() {
        callback?.let { cb -> runCatching { telephony.unregisterTelephonyCallback(cb) } }
        callback = null
    }

    private fun send(ringing: Boolean) {
        CompanionOutbox.trySend(
            Envelope(
                type = MessageType.CALL_INCOMING,
                payload = buildJsonObject {
                    put("number", "")
                    put("ringing", ringing)
                },
            ),
            requiredVersion = 12,
        )
    }
}
