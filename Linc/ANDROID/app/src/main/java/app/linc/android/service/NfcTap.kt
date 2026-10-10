package app.linc.android.service

import android.app.Activity
import android.net.Uri
import android.nfc.NdefMessage
import android.nfc.NdefRecord
import android.nfc.NfcAdapter
import android.nfc.Tag
import android.nfc.tech.Ndef
import android.nfc.tech.NdefFormatable
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/**
 * NFC tap-to-connect. A cheap NFC sticker by the desk carries `linc://tap` plus an Android
 * Application Record, so tapping the phone on it opens Linc's tap handler (even from the lock
 * screen once unlocked) which starts the companion and dials the PC immediately — no waiting out
 * the presence backoff, no opening the app. PCs almost never have an NFC reader, so the tag is
 * the "PC" end of the tap.
 *
 * Nothing secret is on the tag: it only says "connect now". Which PC, and whether to trust it,
 * is still decided by the pinned TLS certificates, so a copied tag is harmless.
 */
object NfcTap {

    const val SCHEME = "linc"
    const val HOST = "tap"

    sealed interface WriteState {
        data object Idle : WriteState
        data object Waiting : WriteState
        data object Written : WriteState
        data class Failed(val reason: String) : WriteState
    }

    private val _writeState = MutableStateFlow<WriteState>(WriteState.Idle)
    val writeState: StateFlow<WriteState> = _writeState

    fun isSupported(activity: Activity): Boolean = NfcAdapter.getDefaultAdapter(activity) != null

    fun isEnabled(activity: Activity): Boolean = NfcAdapter.getDefaultAdapter(activity)?.isEnabled == true

    /** Starts listening for a tag to write. The next tag held to the phone gets the Linc record. */
    fun beginWrite(activity: Activity) {
        val adapter = NfcAdapter.getDefaultAdapter(activity)
        if (adapter == null) {
            _writeState.value = WriteState.Failed("This phone has no NFC.")
            return
        }
        if (!adapter.isEnabled) {
            _writeState.value = WriteState.Failed("NFC is off. Turn it on in the phone's settings, then try again.")
            return
        }
        _writeState.value = WriteState.Waiting
        val flags = NfcAdapter.FLAG_READER_NFC_A or NfcAdapter.FLAG_READER_NFC_B or
            NfcAdapter.FLAG_READER_NFC_F or NfcAdapter.FLAG_READER_NFC_V or
            NfcAdapter.FLAG_READER_NO_PLATFORM_SOUNDS
        adapter.enableReaderMode(activity, { tag ->
            _writeState.value = write(tag, activity.packageName)
            // Done either way; stop so the next tap behaves normally again.
            activity.runOnUiThread { runCatching { adapter.disableReaderMode(activity) } }
        }, flags, null)
    }

    fun cancelWrite(activity: Activity) {
        runCatching { NfcAdapter.getDefaultAdapter(activity)?.disableReaderMode(activity) }
        if (_writeState.value == WriteState.Waiting) {
            _writeState.value = WriteState.Idle
        }
    }

    fun resetWriteState() {
        _writeState.value = WriteState.Idle
    }

    /** True for the `linc://tap` URI this tag carries. */
    fun isTapUri(uri: Uri?): Boolean = uri?.scheme == SCHEME && uri.host == HOST

    private fun message(packageName: String): NdefMessage = NdefMessage(
        arrayOf(
            NdefRecord.createUri("$SCHEME://$HOST"),
            // The AAR makes Android open Linc for this tag rather than offering a chooser.
            NdefRecord.createApplicationRecord(packageName),
        ),
    )

    private fun write(tag: Tag, packageName: String): WriteState {
        val message = message(packageName)
        Ndef.get(tag)?.let { ndef ->
            return try {
                ndef.connect()
                when {
                    !ndef.isWritable -> WriteState.Failed("That tag is locked (read-only). Use a blank one.")
                    ndef.maxSize < message.toByteArray().size ->
                        WriteState.Failed("That tag is too small (${ndef.maxSize} bytes). Use an NTAG213 or bigger.")
                    else -> {
                        ndef.writeNdefMessage(message)
                        LogStore.log(LogLevel.INFO, "Wrote a Linc tap-to-connect NFC tag")
                        WriteState.Written
                    }
                }
            } catch (e: Exception) {
                WriteState.Failed("Couldn't write the tag — hold it still against the phone and try again.")
            } finally {
                runCatching { ndef.close() }
            }
        }
        NdefFormatable.get(tag)?.let { formatable ->
            return try {
                formatable.connect()
                formatable.format(message)
                LogStore.log(LogLevel.INFO, "Formatted and wrote a Linc tap-to-connect NFC tag")
                WriteState.Written
            } catch (e: Exception) {
                WriteState.Failed("Couldn't format the tag — hold it still and try again.")
            } finally {
                runCatching { formatable.close() }
            }
        }
        return WriteState.Failed("That tag type can't hold a Linc record. Use an NTAG213/215/216 sticker.")
    }
}
