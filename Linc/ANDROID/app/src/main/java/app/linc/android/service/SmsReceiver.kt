package app.linc.android.service

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.provider.Telephony
import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/**
 * Relays incoming SMS to the desktop as `sms.received` (PROTOCOL.md v11) while the
 * Messages lane is on. Manifest-registered for `SMS_RECEIVED`. Sideload-only (D-016).
 */
class SmsReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Telephony.Sms.Intents.SMS_RECEIVED_ACTION ||
            !SyncStore.isOn(context, "messages")) {
            return
        }
        val messages = Telephony.Sms.Intents.getMessagesFromIntent(intent) ?: return
        val address = messages.firstOrNull()?.originatingAddress ?: return
        val body = messages.joinToString("") { it.messageBody ?: "" }
        CompanionOutbox.trySend(
            Envelope(
                type = MessageType.SMS_RECEIVED,
                payload = buildJsonObject {
                    put("address", address)
                    put("body", body)
                    put("date", System.currentTimeMillis())
                },
            ),
            requiredVersion = 11,
        )
    }
}
