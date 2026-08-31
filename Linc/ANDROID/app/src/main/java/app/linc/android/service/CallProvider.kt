package app.linc.android.service

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.provider.CallLog
import android.telecom.TelecomManager
import androidx.core.content.ContextCompat
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.add
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/**
 * The Calls lane (PROTOCOL.md v12, D-026): call log, dial-from-PC, and decline.
 * Sideload-only (D-016); call audio stays on the phone (D-017). Needs READ_CALL_LOG /
 * CALL_PHONE / ANSWER_PHONE_CALLS runtime grants.
 */
object CallProvider {

    fun canReadLog(context: Context) =
        ContextCompat.checkSelfPermission(context, Manifest.permission.READ_CALL_LOG) ==
            PackageManager.PERMISSION_GRANTED

    private fun can(context: Context, permission: String) =
        ContextCompat.checkSelfPermission(context, permission) == PackageManager.PERMISSION_GRANTED

    /** Recent calls newest-first, or null when READ_CALL_LOG isn't granted. */
    fun recent(context: Context, limit: Int): JsonObject? {
        if (!canReadLog(context)) {
            return null
        }
        val calls = buildJsonArray {
            val projection = arrayOf(
                CallLog.Calls.NUMBER, CallLog.Calls.TYPE, CallLog.Calls.DATE, CallLog.Calls.DURATION)
            runCatching {
                context.contentResolver.query(
                    CallLog.Calls.CONTENT_URI, projection, null, null,
                    "${CallLog.Calls.DATE} DESC",
                )?.use { cursor ->
                    val num = cursor.getColumnIndexOrThrow(CallLog.Calls.NUMBER)
                    val type = cursor.getColumnIndexOrThrow(CallLog.Calls.TYPE)
                    val date = cursor.getColumnIndexOrThrow(CallLog.Calls.DATE)
                    val dur = cursor.getColumnIndexOrThrow(CallLog.Calls.DURATION)
                    var count = 0
                    while (cursor.moveToNext() && count < limit) {
                        add(buildJsonObject {
                            put("number", cursor.getString(num) ?: "")
                            put("type", typeName(cursor.getInt(type)))
                            put("date", cursor.getLong(date))
                            put("duration", cursor.getLong(dur))
                        })
                        count++
                    }
                }
            }
        }
        return buildJsonObject { put("calls", calls) }
    }

    /** Places a call from the phone; false without CALL_PHONE. */
    fun dial(context: Context, number: String): Boolean {
        if (!can(context, Manifest.permission.CALL_PHONE) || number.isBlank()) {
            return false
        }
        return runCatching {
            context.startActivity(
                Intent(Intent.ACTION_CALL, Uri.fromParts("tel", number, null))
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            LogStore.log(LogLevel.INFO, "Placed a call from the PC")
        }.isSuccess
    }

    /** Rejects the ringing / ends the ongoing call; false without ANSWER_PHONE_CALLS. */
    fun decline(context: Context): Boolean {
        if (!can(context, Manifest.permission.ANSWER_PHONE_CALLS)) {
            return false
        }
        return runCatching {
            @Suppress("MissingPermission")
            context.getSystemService(TelecomManager::class.java).endCall()
            LogStore.log(LogLevel.INFO, "Declined a call from the PC")
        }.isSuccess
    }

    private fun typeName(type: Int) = when (type) {
        CallLog.Calls.INCOMING_TYPE -> "incoming"
        CallLog.Calls.OUTGOING_TYPE -> "outgoing"
        CallLog.Calls.MISSED_TYPE -> "missed"
        CallLog.Calls.REJECTED_TYPE -> "rejected"
        else -> "other"
    }
}
