package app.linc.android.service

import android.Manifest
import android.bluetooth.BluetoothManager
import android.bluetooth.le.AdvertiseCallback
import android.bluetooth.le.AdvertiseData
import android.bluetooth.le.AdvertiseSettings
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.content.ContextCompat
import java.nio.ByteBuffer
import java.security.MessageDigest
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/**
 * Bluetooth-LE presence beacon (M04, D-034): tells a paired PC "this phone is physically here"
 * so it can say Nearby and start looking over Wi-Fi immediately — including when the phone's
 * Wi-Fi is off entirely, which is exactly when mDNS can tell it nothing.
 *
 * **No data ever rides BLE.** The advertisement carries only a rotating id derived from the
 * pairing certificate, so only a PC that has already paired can recognise it, and nobody else
 * can follow the phone from one rotation to the next. Format: docs/PROTOCOL.md "BLE presence
 * beacon"; the desktop's matching half is BlePresenceService.cs, and the two derivations must
 * stay in step (D-006 — shared spec, no shared code).
 *
 * Every failure path is a no-op: no BLE hardware, Bluetooth off, permission not granted, or no
 * certificate yet all simply mean no beacon, and everything else about Linc is unaffected.
 */
class BlePresenceAdvertiser(
    private val context: Context,
    private val scope: CoroutineScope,
) {
    private companion object {
        /** The Bluetooth SIG's company id reserved for testing and development. */
        const val MANUFACTURER_ID = 0xFFFF

        /** Marks a Linc beacon inside the 0xFFFF space, which anyone may use. */
        val MAGIC = byteArrayOf(0x4C, 0x43) // "LC"

        /** Must match BlePresenceService.RotationPeriod on the desktop. */
        const val ROTATION_SECONDS = 300L
        const val ID_LENGTH = 8
    }

    private var loopJob: Job? = null

    @Volatile
    private var callback: AdvertiseCallback? = null

    /** True once the radio has accepted an advertisement — surfaced on the Settings screen. */
    @Volatile
    var isAdvertising: Boolean = false
        private set

    fun start() {
        if (loopJob != null) return
        loopJob = scope.launch {
            while (isActive) {
                republish()
                // Sleep to the next slot BOUNDARY rather than a flat period: a fixed delay
                // drifts later every cycle, so the advertised id would lag the current slot by
                // an ever-growing margin. The desktop tolerates one slot either way, and this
                // keeps us comfortably inside that.
                val now = System.currentTimeMillis() / 1000
                delay((ROTATION_SECONDS - (now % ROTATION_SECONDS)) * 1000)
            }
        }
    }

    fun stop() {
        loopJob?.cancel()
        loopJob = null
        stopAdvertising()
    }

    fun hasPermission(): Boolean =
        Build.VERSION.SDK_INT < Build.VERSION_CODES.S ||
            ContextCompat.checkSelfPermission(context, Manifest.permission.BLUETOOTH_ADVERTISE) ==
            PackageManager.PERMISSION_GRANTED

    private fun republish() {
        stopAdvertising()
        if (!hasPermission()) return

        val advertiser = runCatching {
            val manager = context.getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager
            manager?.adapter?.takeIf { it.isEnabled }?.bluetoothLeAdvertiser
        }.getOrNull() ?: return

        val payload = beaconPayload() ?: return

        val settings = AdvertiseSettings.Builder()
            // Low power is right for a presence hint: the PC only needs to hear it within a few
            // seconds of the phone arriving, and this runs for as long as the service does.
            .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_POWER)
            .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_LOW)
            .setConnectable(false) // nothing to connect to — this is a hint, not a transport
            .setTimeout(0)
            .build()

        val data = AdvertiseData.Builder()
            .setIncludeDeviceName(false) // the phone's name is not the PC's business over the air
            .setIncludeTxPowerLevel(false)
            .addManufacturerData(MANUFACTURER_ID, payload)
            .build()

        val newCallback = object : AdvertiseCallback() {
            override fun onStartSuccess(settingsInEffect: AdvertiseSettings) {
                isAdvertising = true
            }

            override fun onStartFailure(errorCode: Int) {
                isAdvertising = false
                LogStore.log(LogLevel.WARN, "Bluetooth presence unavailable (code $errorCode)")
            }
        }
        runCatching {
            advertiser.startAdvertising(settings, data, newCallback)
            callback = newCallback
        }.onFailure {
            // SecurityException if the grant was revoked between the check and the call.
            isAdvertising = false
        }
    }

    private fun stopAdvertising() {
        val current = callback ?: return
        callback = null
        isAdvertising = false
        if (!hasPermission()) return
        runCatching {
            val manager = context.getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager
            manager?.adapter?.bluetoothLeAdvertiser?.stopAdvertising(current)
        }
    }

    /** Magic ‖ rotating id, or null when there is no certificate to derive one from yet. */
    private fun beaconPayload(): ByteArray? {
        val certificate = runCatching {
            TlsIdentity.certificateDer()
        }.getOrNull() ?: return null

        val slot = System.currentTimeMillis() / 1000 / ROTATION_SECONDS
        return MAGIC + rotatingId(certificate, slot)
    }

    /**
     * The first 8 bytes of SHA-256(certificate DER ‖ slot as 8 big-endian bytes). The desktop
     * computes the identical value for every phone whose certificate it has pinned and matches
     * by equality — so a mismatch here means it simply never recognises this phone.
     */
    private fun rotatingId(certificateDer: ByteArray, slot: Long): ByteArray {
        val material = ByteBuffer.allocate(certificateDer.size + 8)
            .put(certificateDer)
            .putLong(slot) // ByteBuffer is big-endian by default
            .array()
        return MessageDigest.getInstance("SHA-256").digest(material).copyOf(ID_LENGTH)
    }
}
