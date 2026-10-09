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
    val error: String? = null,
)

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

    fun set(context: Context, on: Boolean, phoneMic: Boolean) {
        appContext = context.applicationContext
        CompanionOutbox.trySend(
            Envelope(
                type = MessageType.PC_VOICEACCESS_SET,
                payload = buildJsonObject {
                    put("on", on)
                    put("phoneMic", phoneMic && hasMicPermission(context))
                },
            ),
            requiredVersion = 21,
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
            LogStore.log(LogLevel.INFO, "Voice Access: microphone off")
        }
    }

    private const val RATE = 16_000
    /** 20 ms of 16 kHz mono 16-bit audio. */
    private const val FRAME_BYTES = RATE / 50 * 2
}
