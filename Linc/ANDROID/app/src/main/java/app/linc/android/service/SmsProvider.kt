package app.linc.android.service

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.provider.Telephony
import android.telephony.SmsManager
import androidx.core.content.ContextCompat
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.add
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/**
 * The Messages lane (PROTOCOL.md v11, D-025): list recent SMS and send new ones.
 * Sideload-only (D-016); needs the READ_SMS / SEND_SMS runtime grants. Sending uses
 * SmsManager and does NOT require default-SMS-app status. Numbers only (no contacts).
 */
object SmsProvider {

    fun canRead(context: Context) =
        ContextCompat.checkSelfPermission(context, Manifest.permission.READ_SMS) ==
            PackageManager.PERMISSION_GRANTED

    fun canSend(context: Context) =
        ContextCompat.checkSelfPermission(context, Manifest.permission.SEND_SMS) ==
            PackageManager.PERMISSION_GRANTED

    /** Recent messages newest-first, or null when READ_SMS isn't granted. */
    fun recent(context: Context, limit: Int): JsonObject? {
        if (!canRead(context)) {
            return null
        }
        val messages = buildJsonArray {
            val projection = arrayOf(
                Telephony.Sms.ADDRESS, Telephony.Sms.BODY, Telephony.Sms.DATE, Telephony.Sms.TYPE)
            runCatching {
                context.contentResolver.query(
                    Telephony.Sms.CONTENT_URI, projection, null, null,
                    "${Telephony.Sms.DATE} DESC",
                )?.use { cursor ->
                    val addr = cursor.getColumnIndexOrThrow(Telephony.Sms.ADDRESS)
                    val body = cursor.getColumnIndexOrThrow(Telephony.Sms.BODY)
                    val date = cursor.getColumnIndexOrThrow(Telephony.Sms.DATE)
                    val type = cursor.getColumnIndexOrThrow(Telephony.Sms.TYPE)
                    var count = 0
                    while (cursor.moveToNext() && count < limit) {
                        add(buildJsonObject {
                            put("address", cursor.getString(addr) ?: "")
                            put("body", cursor.getString(body) ?: "")
                            put("date", cursor.getLong(date))
                            put("incoming", cursor.getInt(type) == Telephony.Sms.MESSAGE_TYPE_INBOX)
                        })
                        count++
                    }
                }
            }
        }
        return buildJsonObject { put("messages", messages) }
    }

    /** Sends an SMS; false when SEND_SMS isn't granted or the send failed. */
    fun send(context: Context, address: String, body: String): Boolean {
        if (!canSend(context) || address.isBlank() || body.isEmpty()) {
            return false
        }
        return runCatching {
            val manager = context.getSystemService(SmsManager::class.java)
            val parts = manager.divideMessage(body)
            if (parts.size > 1) {
                manager.sendMultipartTextMessage(address, null, parts, null, null)
            } else {
                manager.sendTextMessage(address, null, body, null, null)
            }
            LogStore.log(LogLevel.INFO, "Sent an SMS from the PC")
        }.isSuccess
    }
}
