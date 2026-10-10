package app.linc.android.service

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import app.linc.android.protocol.Envelope
import app.linc.android.protocol.Framing
import app.linc.android.protocol.MessageType
import java.io.DataOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.put

/** What Windows Voice Access is doing on the PC (v21 `pc.voiceaccess.state`). */
data class VoiceAccessState(
    /** The PC has Voice Access at all (Windows 11 22H2+). */
    val available: Boolean = false,
    val on: Boolean = false,
    /** Whose microphone it is hearing: "phone" or "pc". */
    val mic: String = "pc",
    /** A virtual audio cable is installed, so the phone's mic can be used. */
    val cable: Boolean = false,
    /**
     * Setting the cable up (D-071): "ready", "needed" (the PC can install it once the user
     * agrees), "installing" (Windows is asking on the PC), "failed", or "" from a PC too old to
     * install it itself.
     */
    val setup: String = "",
    /** While setting up: how much of the download has arrived, 0..100 (100 = installing). */
    val progress: Int? = null,
    /** v23 (D-075): the phone's mic is streaming to the PC right now. */
    val micOn: Boolean = false,
    /** The Microphone tool is on: the phone is the PC's mic for every app. */
    val tool: Boolean = false,
    /** PC Remote's automatic mode is on (the phone's mic whenever a PC app listens). */
    val auto: Boolean = false,
    /** In automatic mode, a PC app is listening now. */
    val listening: Boolean = false,
    val error: String? = null,
) {
    /** What to tell the user while the PC sets the microphone up. */
    val setupText: String get() = when {
        progress != null && progress < 100 -> "Downloading the microphone for the PC… $progress %"
        else -> "Setting up the microphone… on the PC, approve Windows' prompt (it may be flashing in the taskbar) and click Install Driver."
    }
    /** The PC could set up the phone-as-microphone itself if asked. */
    val canSetUp: Boolean get() = available && !cable && setup in setOf("needed", "failed")
    val settingUp: Boolean get() = setup == "installing"
}

/**
 * Windows Voice Access from the phone, with the phone as the PC's microphone (v21, D-070).
 * [set] asks the PC to start or stop it; when the PC wants the phone's mic it dials pc-mic
 * channel 7, and [stream] sends 16 kHz mono speech audio down it until the channel closes.
 */
object PcVoiceAccess {

    private val _state = MutableStateFlow(VoiceAccessState())
    val state: StateFlow<VoiceAccessState> = _state

    /** True while the phone's microphone is streaming to the PC. */
    private val _micLive = MutableStateFlow(false)
    val micLive: StateFlow<Boolean> = _micLive

    @Volatile private var appContext: Context? = null

    fun ready(): Boolean = CompanionOutbox.canSend(21)

    /**
     * The explanation shown before the PC installs the virtual microphone (D-071). Windows
     * only lets a driver create a microphone, so the PC needs one; VB-CABLE is the free one
     * nearly every "use your phone as a mic" app relies on, fetched from its maker.
     */
    const val SETUP_EXPLANATION =
        "Windows can only hear a microphone that a driver provides, so to use this phone as the " +
            "PC's microphone Linc sets one up on the PC: it downloads VB-CABLE, VB-Audio's free " +
            "virtual audio cable (donationware, vb-audio.com), checks it, and opens VB-Audio's " +
            "installer.\n\nOn the PC: approve Windows' prompt, then click \"Install Driver\". " +
            "You can remove it again from Linc's Settings on the PC."

    fun set(context: Context, on: Boolean, phoneMic: Boolean, installCable: Boolean = false) {
        appContext = context.applicationContext
        CompanionOutbox.trySend(
            Envelope(
                type = MessageType.PC_VOICEACCESS_SET,
                payload = buildJsonObject {
                    put("on", on)
                    put("phoneMic", phoneMic && hasMicPermission(context))
                    // v21 addition (D-071): the user agreed to have the PC set up the cable.
                    if (installCable) put("installCable", true)
                },
            ),
            requiredVersion = 21,
        )
    }

    /** The Microphone tool and PC Remote's automatic mode need a v23 PC. */
    fun micReady(): Boolean = CompanionOutbox.canSend(23)

    /** The Microphone tool: the phone as the PC's microphone for any app (v23, D-075). */
    fun setTool(context: Context, on: Boolean, installCable: Boolean = false) {
        appContext = context.applicationContext
        CompanionOutbox.trySend(
            Envelope(type = MessageType.PC_MIC_SET, payload = buildJsonObject {
                put("on", on && hasMicPermission(context))
                if (installCable) put("installCable", true)
            }),
            requiredVersion = 23,
        )
    }

    /**
     * PC Remote with "Phone" chosen: while it is on, any PC app that wants a microphone hears
     * this phone (v23, D-075). Sent when the picture comes up and when PC Remote closes.
     */
    fun setAuto(context: Context, on: Boolean, installCable: Boolean = false) {
        appContext = context.applicationContext
        CompanionOutbox.trySend(
            Envelope(type = MessageType.PC_MIC_AUTO, payload = buildJsonObject {
                put("on", on && hasMicPermission(context))
                if (installCable) put("installCable", true)
            }),
            requiredVersion = 23,
        )
    }

    fun refresh(context: Context) {
        appContext = context.applicationContext
        CompanionOutbox.trySend(
            Envelope(type = MessageType.PC_VOICEACCESS_GET, payload = buildJsonObject {}),
            requiredVersion = 21,
        )
    }

    fun onState(payload: JsonObject) {
        fun bool(key: String) = (payload[key] as? JsonPrimitive)?.booleanOrNull ?: false
        _state.value = VoiceAccessState(
            available = bool("available"),
            on = bool("on"),
            mic = (payload["mic"] as? JsonPrimitive)?.contentOrNull ?: "pc",
            cable = bool("cable"),
            setup = (payload["setup"] as? JsonPrimitive)?.contentOrNull ?: "",
            progress = (payload["progress"] as? JsonPrimitive)?.contentOrNull?.toIntOrNull(),
            micOn = bool("micOn"),
            tool = bool("tool"),
            auto = bool("auto"),
            listening = bool("listening"),
            error = (payload["error"] as? JsonPrimitive)?.contentOrNull,
        )
    }

    fun hasMicPermission(context: Context): Boolean =
        context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED

    /**
     * Serve pc-mic channel 7: a config frame, then 20 ms packets of 16-bit PCM framed like
     * pc-video (12-byte header). Runs on the dial-back thread until the PC closes the channel.
     */
    @SuppressLint("MissingPermission") // checked just below; the PC only asks after we said phoneMic
    fun stream(output: DataOutputStream) {
        val context = appContext
        if (context == null || !hasMicPermission(context)) {
            LogStore.log(LogLevel.WARN, "Voice Access: the PC asked for the microphone, but it isn't allowed")
            return
        }
        val minBuffer = AudioRecord.getMinBufferSize(RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        val record = AudioRecord(
            MediaRecorder.AudioSource.VOICE_RECOGNITION, RATE,
            AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT,
            maxOf(minBuffer, FRAME_BYTES * 4),
        )
        val pcm = ByteArray(FRAME_BYTES)
        try {
            Framing.write(output, """{"codec":"pcm16","sampleRate":$RATE,"channels":1}""")
            micForeground(context, true)
            record.startRecording()
            _micLive.value = true
            LogStore.log(LogLevel.INFO, "Voice Access: the PC is hearing this phone's microphone")
            val started = android.os.SystemClock.elapsedRealtime()
            while (true) {
                val n = record.read(pcm, 0, pcm.size)
                if (n <= 0) break
                val header = ByteBuffer.allocate(12).order(ByteOrder.BIG_ENDIAN)
                header.putLong((android.os.SystemClock.elapsedRealtime() - started) * 1000)
                header.putInt(n)
                output.write(header.array())
                output.write(pcm, 0, n)
                output.flush()
            }
        } catch (_: Exception) {
            // The PC closed the channel: Voice Access is off, or it switched back to its own mic.
        } finally {
            runCatching { record.stop() }
            record.release()
            _micLive.value = false
            micForeground(context, false)
            LogStore.log(LogLevel.INFO, "Voice Access: microphone off")
        }
    }

    /** Ask the companion service to carry (or drop) the microphone foreground type. */
    private fun micForeground(context: Context, on: Boolean) {
        runCatching {
            context.startService(
                android.content.Intent(context, CompanionService::class.java)
                    .setAction(CompanionService.ACTION_MIC).putExtra(CompanionService.EXTRA_ON, on),
            )
        }
    }

    private const val RATE = 16_000
    /** 20 ms of 16 kHz mono 16-bit audio. */
    private const val FRAME_BYTES = RATE / 50 * 2
}
