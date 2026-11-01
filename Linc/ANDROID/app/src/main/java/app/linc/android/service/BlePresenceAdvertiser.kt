package app.linc.android.service

import android.Manifest
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
import android.bluetooth.le.AdvertiseCallback
import android.bluetooth.le.AdvertiseData
import android.bluetooth.le.AdvertiseSettings
import android.bluetooth.le.ScanCallback
import android.bluetooth.le.ScanFilter
import android.bluetooth.le.ScanResult
import android.bluetooth.le.ScanSettings
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import androidx.core.content.ContextCompat
import java.nio.ByteBuffer
import java.security.MessageDigest
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/**
 * Bluetooth-LE presence, both ways (M04, D-034; two-way since build 13, owner 2026-11-01: "use
 * Bluetooth as a helper alongside the main guessing… heyy I'm failing, what's your status").
 *
 * - **This phone's beacon** tells a paired PC "this phone is here" plus one status byte (linked,
 *   Wi-Fi, wireless debugging, on a PC hotspot…), so the PC can start looking at once, say why it
 *   can't reach the phone, and start its hotspot when the phone has no Wi-Fi to share.
 * - **The PC's beacon** (heard while there's no link) tells this phone "your PC is here" plus the
 *   PC's status: the phone then announces itself on the network at once, and offers to join the
 *   PC's hotspot when the PC says it is on.
 *
 * No content ever rides Bluetooth: each beacon carries a rotating id derived from the pairing
 * certificate, so only the paired pair recognises it and nobody else can follow the phone. Format:
 * PROTOCOL.md "BLE presence beacon"; the PC's half is BlePresenceService.cs (shared spec, no shared
 * code, D-006).
 *
 * Every failure path is a no-op: no BLE hardware, Bluetooth off, permission not granted, or no
 * certificate yet all simply mean no beacon. Bluetooth switched on later is noticed at once.
 */
class BlePresenceAdvertiser(
    private val context: Context,
    private val scope: CoroutineScope,
    /** The PC was just heard with no link: look for it on the network now. */
    private val onPcNear: (String) -> Unit = {},
) {
    companion object {
        /** The Bluetooth SIG's company id reserved for testing and development. */
        const val MANUFACTURER_ID = 0xFFFF

        /** Marks the phone's beacon inside the 0xFFFF space, which anyone may use. */
        val MAGIC = byteArrayOf(0x4C, 0x43) // "LC"

        /** Marks the PC's beacon. */
        val PC_MAGIC = byteArrayOf(0x4C, 0x50) // "LP"

        /** Must match BlePresenceService.RotationPeriod on the desktop. */
        const val ROTATION_SECONDS = 300L
        const val ID_LENGTH = 8

        // The phone's status byte (PhoneBeaconFlags on the PC).
        const val LINKED = 1
        const val WIFI_ON = 2
        const val ON_WIFI = 4
        const val WIRELESS_DEBUGGING = 8
        const val ON_PC_HOTSPOT = 16
        const val KNOWS_PC_HOTSPOT = 32

        // The PC's status byte (PcBeaconFlags on the PC).
        const val PC_LINKED = 1
        const val PC_HOTSPOT_ON = 2
        const val PC_LOOKING = 4

        /** What the PC said last over Bluetooth, for Settings: its status byte and when (elapsed ms). */
        data class PcHeard(val flags: Int, val atMillis: Long)

        private val _pcHeard = MutableStateFlow<PcHeard?>(null)
        val pcHeard: StateFlow<PcHeard?> = _pcHeard

        /** Is this phone's beacon on the air (for Settings)? */
        @Volatile
        var advertising: Boolean = false
            private set

        /** "Nearby devices": advertise, scan, and ask Android to turn Bluetooth on. */
        val PERMISSIONS = arrayOf(
            Manifest.permission.BLUETOOTH_ADVERTISE,
            Manifest.permission.BLUETOOTH_SCAN,
            Manifest.permission.BLUETOOTH_CONNECT,
        )

        fun hasPermission(context: Context, permission: String = Manifest.permission.BLUETOOTH_ADVERTISE): Boolean =
            ContextCompat.checkSelfPermission(context, permission) == PackageManager.PERMISSION_GRANTED

        fun bluetoothOn(context: Context): Boolean = runCatching {
            (context.getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager)?.adapter?.isEnabled == true
        }.getOrDefault(false)

        /** A plain sentence for the PC's status byte. */
        fun describePc(flags: Int): String = buildList {
            add(if (flags and PC_LINKED != 0) "has a link" else "has no link")
            if (flags and PC_HOTSPOT_ON != 0) add("its hotspot is on")
            if (flags and PC_LOOKING != 0) add("it's looking for this phone")
        }.joinToString(", ")

        /**
         * The first 8 bytes of SHA-256(certificate DER ‖ slot as 8 big-endian bytes). The desktop
         * computes the identical value for every phone whose certificate it has pinned and matches
         * by equality — so a mismatch here means it simply never recognises this phone.
         */
        fun rotatingId(certificateDer: ByteArray, slot: Long): ByteArray {
            val material = ByteBuffer.allocate(certificateDer.size + 8)
                .put(certificateDer)
                .putLong(slot) // ByteBuffer is big-endian by default
                .array()
            return MessageDigest.getInstance("SHA-256").digest(material).copyOf(ID_LENGTH)
        }

        /** Pure: is this the paired PC's beacon (any neighbouring slot)? Returns its status byte, or null. */
        fun matchPc(payload: ByteArray, certificateDer: ByteArray, slot: Long): Int? {
            if (payload.size < PC_MAGIC.size + ID_LENGTH || payload[0] != PC_MAGIC[0] || payload[1] != PC_MAGIC[1]) return null
            val id = payload.copyOfRange(PC_MAGIC.size, PC_MAGIC.size + ID_LENGTH)
            if ((-1L..1L).none { id.contentEquals(rotatingId(certificateDer, slot + it)) }) return null
            return if (payload.size > PC_MAGIC.size + ID_LENGTH) payload[PC_MAGIC.size + ID_LENGTH].toInt() and 0xFF else 0
        }
    }

    private var loopJob: Job? = null

    @Volatile
    private var callback: AdvertiseCallback? = null
    private var published: Pair<Long, Int>? = null
    private var scanning: ScanCallback? = null
    private var receiver: BroadcastReceiver? = null
    private var lastPcFlags: Int? = null
    private var lastNearCall = 0L

    fun start() {
        if (loopJob != null) return
        // Bluetooth switched on (or off): act now instead of at the next 5-minute rotation, which
        // is why a phone whose Bluetooth came on later stayed unheard (owner, 2026-11-01).
        receiver = object : BroadcastReceiver() {
            override fun onReceive(c: Context, intent: Intent) {
                val state = intent.getIntExtra(BluetoothAdapter.EXTRA_STATE, -1)
                if (state == BluetoothAdapter.STATE_ON || state == BluetoothAdapter.STATE_OFF) {
                    scope.launch { refresh(force = true) }
                }
            }
        }.also {
            runCatching { ContextCompat.registerReceiver(context, it, IntentFilter(BluetoothAdapter.ACTION_STATE_CHANGED), ContextCompat.RECEIVER_EXPORTED) }
        }
        loopJob = scope.launch {
            while (isActive) {
                refresh(force = false)
                delay(5_000) // the status byte follows the phone within 5 s; a change re-advertises
            }
        }
    }

    fun stop() {
        loopJob?.cancel()
        loopJob = null
        receiver?.let { runCatching { context.unregisterReceiver(it) } }
        receiver = null
        stopAdvertising()
        stopScan()
    }

    private fun refresh(force: Boolean) {
        val slot = System.currentTimeMillis() / 1000 / ROTATION_SECONDS
        val status = status()
        if (force || published != slot to status || callback == null) republish(slot, status)
        val linked = status and LINKED != 0
        if (!linked && bluetoothOn(context)) startScan() else stopScan()
    }

    /** This phone's status byte. */
    private fun status(): Int {
        var flags = 0
        if (CompanionStateHolder.state.value is CompanionStateHolder.ServiceState.Connected) flags = flags or LINKED
        if (WifiNow.radioOn(context)) flags = flags or WIFI_ON
        if (WifiNow.network(context) != null) flags = flags or ON_WIFI
        if (AdbArming.isArmed(context)) flags = flags or WIRELESS_DEBUGGING
        if (WifiNow.onPcHotspot(context)) flags = flags or ON_PC_HOTSPOT
        if (PcHotspotJoin.held(context) != null) flags = flags or KNOWS_PC_HOTSPOT
        return flags
    }

    private fun adapter() = runCatching {
        (context.getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager)?.adapter?.takeIf { it.isEnabled }
    }.getOrNull()

    private fun republish(slot: Long, status: Int) {
        stopAdvertising()
        if (!hasPermission(context)) return
        val advertiser = adapter()?.bluetoothLeAdvertiser ?: return
        val certificate = runCatching { TlsIdentity.certificateDer() }.getOrNull() ?: return
        val payload = MAGIC + rotatingId(certificate, slot) + byteArrayOf(status.toByte())

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
                advertising = true
            }

            override fun onStartFailure(errorCode: Int) {
                advertising = false
                LogStore.log(LogLevel.WARN, "Bluetooth presence unavailable (code $errorCode)")
            }
        }
        runCatching {
            advertiser.startAdvertising(settings, data, newCallback)
            callback = newCallback
            published = slot to status
        }.onFailure {
            // SecurityException if the grant was revoked between the check and the call.
            advertising = false
        }
    }

    private fun stopAdvertising() {
        val current = callback ?: return
        callback = null
        published = null
        advertising = false
        if (!hasPermission(context)) return
        runCatching { adapter()?.bluetoothLeAdvertiser?.stopAdvertising(current) }
    }

    // ---- listening for the PC ----

    private fun startScan() {
        if (scanning != null || !hasPermission(context, Manifest.permission.BLUETOOTH_SCAN)) return
        val scanner = adapter()?.bluetoothLeScanner ?: return
        val filter = ScanFilter.Builder()
            .setManufacturerData(MANUFACTURER_ID, PC_MAGIC, byteArrayOf(-1, -1))
            .build()
        val settings = ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_POWER).build()
        val cb = object : ScanCallback() {
            override fun onScanResult(callbackType: Int, result: ScanResult) {
                val payload = result.scanRecord?.getManufacturerSpecificData(MANUFACTURER_ID) ?: return
                onPcBeacon(payload)
            }

            override fun onScanFailed(errorCode: Int) {
                scanning = null
                LogStore.log(LogLevel.INFO, "Couldn't listen for your PC over Bluetooth (code $errorCode)")
            }
        }
        runCatching {
            scanner.startScan(listOf(filter), settings, cb)
            scanning = cb
        }
    }

    private fun stopScan() {
        val cb = scanning ?: return
        scanning = null
        runCatching { adapter()?.bluetoothLeScanner?.stopScan(cb) }
    }

    private fun onPcBeacon(payload: ByteArray) {
        val certificate = runCatching { TlsIdentity.certificateDer() }.getOrNull() ?: return
        val flags = matchPc(payload, certificate, System.currentTimeMillis() / 1000 / ROTATION_SECONDS) ?: return
        _pcHeard.value = PcHeard(flags, android.os.SystemClock.elapsedRealtime())
        if (CompanionStateHolder.state.value is CompanionStateHolder.ServiceState.Connected) return
        if (flags != lastPcFlags) {
            lastPcFlags = flags
            LogStore.log(LogLevel.INFO, "Your PC is nearby (Bluetooth): ${describePc(flags)}.")
        }
        val now = android.os.SystemClock.elapsedRealtime()
        if (now - lastNearCall > 20_000) {
            lastNearCall = now
            onPcNear("PC nearby over Bluetooth")
        }
        if (flags and PC_HOTSPOT_ON != 0 && !WifiNow.onPcHotspot(context)) {
            PcHotspotJoin.offerJoin(context, why = "Your PC is nearby with its hotspot on")
        }
    }
}
