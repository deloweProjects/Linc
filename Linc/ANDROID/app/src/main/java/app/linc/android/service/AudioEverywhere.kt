package app.linc.android.service

import android.annotation.SuppressLint
import android.content.Context
import android.media.AudioDeviceCallback
import android.media.AudioDeviceInfo
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.media.AudioTimestamp
import android.media.MediaRecorder
import android.os.Handler
import android.os.Looper
import app.linc.android.protocol.Envelope
import app.linc.android.protocol.MessageType
import kotlin.concurrent.thread
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.longOrNull
import kotlinx.serialization.json.put

/** One speaker in the mixer, on the PC or on this phone, as the PC reports it (v26). */
data class EverywhereDevice(
    val id: String,
    val name: String,
    /** "pc" or "phone". */
    val kind: String,
    val bluetooth: Boolean,
    val enabled: Boolean,
    val volume: Int,
    val muted: Boolean,
    /** What the microphone measured, ms. */
    val autoMs: Double,
    /** The user's own nudge, ms. */
    val manualMs: Double,
    val hiddenUs: Long,
    val calibrated: Boolean,
    /** "playing", "starting", "off", "error", "rerouted". */
    val status: String,
    /** How far from the others it is right now, ms. */
    val offByMs: Double,
    val problem: String?,
)

data class EverywhereState(
    val on: Boolean = false,
    val error: String? = null,
    /** How far behind the PC every speaker plays, so the slowest can keep up. */
    val delayMs: Int = 0,
    val marginMs: Int = 0,
    /** "calibrate" or "clicks" while a test runs. */
    val testing: String? = null,
    val note: String? = null,
    val devices: List<EverywhereDevice> = emptyList(),
    /** The phone is recording for an alignment right now. */
    val listening: Boolean = false,
    /**
     * Whose sound plays everywhere: "pc", "phone" (v27, recorded here, channel 8) or "mirror"
     * (v31: this phone's sound taken by the PC the way Mirror screen does, then played on every
     * speaker, this phone's included, as the PC's own sound would be).
     */
    val source: String = "pc",
)

/**
 * Play everywhere (v26, D-096), the phone's side: the mixer the user sees in Tools → Media, the
 * phone's own outputs playing in step, and the microphone that times every speaker.
 *
 * The PC owns the settings (it saves them per device) and the group delay; this applies the
 * phone's share — which outputs play, how loud, and how much latency each hides — and reports
 * back what each output needs and, after an alignment, what the microphone heard.
 */
object AudioEverywhere {

    private val _state = MutableStateFlow(EverywhereState())
    val state: StateFlow<EverywhereState> = _state

    /** The group delay every output aims for, µs. */
    @Volatile var delayUs = 60_000L
        private set

    /**
     * What this phone's outputs aim for. While PC Remote shows the PC's screen, the sound follows
     * the picture there (D-097: lip-sync beats being in step with speakers across the room), as
     * early as the output can manage; otherwise the group delay, in step with the rest.
     */
    fun targetDelayUs(needUs: Long): Long {
        val video = MirrorSession.videoLatency.us
        if (MirrorReceiver.state.value.streaming && video > 0) return maxOf(video, needUs)
        return delayUs
    }

    @Volatile private var appContext: Context? = null
    @Volatile private var sounds: Map<String, List<TestSound>> = emptyMap()
    @Volatile private var soundsUntilUs = 0L
    private val specs = HashMap<String, PhoneOutputSpec>()
    private var reporter: Thread? = null
    private var callbackRegistered = false

    fun ready(): Boolean = CompanionOutbox.canSend(26)

    /** The PC can take this phone's sound the way Mirror screen does and play it everywhere (v31). */
    fun mirrorReady(): Boolean = CompanionOutbox.canSend(31)

    /**
     * This phone is the source (v31 "mirror"): the copies it plays back must not be taken by the
     * PC's capture (it takes media players only, so they play as a game would), and Linc must not
     * take audio focus or show "Playing from your PC" (that would pause the very app being played).
     */
    @Volatile var phoneIsSource = false
        private set
    @Volatile private var sourceChosenAt = 0L

    /** The app context, once the service or the mixer has attached. */
    fun context(): Context? = appContext

    /** From the companion service: outputs need a context even when the mixer was never opened. */
    fun attach(context: Context) {
        appContext = context.applicationContext
        if (callbackRegistered) return
        callbackRegistered = true
        // A Bluetooth speaker connecting or going away changes what the phone can offer.
        context.getSystemService(AudioManager::class.java)?.registerAudioDeviceCallback(object : AudioDeviceCallback() {
            override fun onAudioDevicesAdded(added: Array<out AudioDeviceInfo>) = outputsChanged()
            override fun onAudioDevicesRemoved(removed: Array<out AudioDeviceInfo>) = outputsChanged()
        }, Handler(Looper.getMainLooper()))
    }

    private fun outputsChanged() {
        if (!SyncedAudio.streaming) return
        applyOutputs()
        sendReport()
    }

    // ---- the user's changes -----------------------------------------------------------------

    fun set(context: Context, on: Boolean) {
        attach(context)
        markPhoneIsSource(false, byUser = true) // on or off, this is the PC's own sound
        send(buildJsonObject { put("on", on) })
        LogStore.log(LogLevel.INFO, "Play everywhere: turning ${if (on) "on" else "off"}")
    }

    fun refresh(context: Context) {
        attach(context)
        send(buildJsonObject { })
    }

    fun setDevice(id: String, enabled: Boolean? = null, volume: Int? = null, muted: Boolean? = null, manualMs: Double? = null) {
        send(buildJsonObject {
            put("device", buildJsonObject {
                put("id", id)
                enabled?.let { put("enabled", it) }
                volume?.let { put("volume", it) }
                muted?.let { put("muted", it) }
                manualMs?.let { put("manualMs", it) }
            })
        })
    }

    fun setMargin(ms: Int) = send(buildJsonObject { put("marginMs", ms) })

    /** The phone became (or stopped being) the source: the PC listens on channel 8, or stops (v27). */
    fun setPhoneSource(on: Boolean) {
        if (!CompanionOutbox.canSend(27)) return
        CompanionOutbox.trySend(Envelope(type = MessageType.AUDIO_EVERYWHERE_SET,
            payload = buildJsonObject { put("source", if (on) "phone" else "pc"); put("on", on) }), requiredVersion = 27)
    }

    /**
     * "Whose sound: This phone" on a v31 PC: the PC takes this phone's sound the way Mirror screen
     * does (scrcpy, no --audio-dup, so this phone's speaker goes quiet) and Play everywhere plays
     * it on every speaker, this phone's included, in step. Off goes back to the PC's own sound.
     */
    fun setMirrorSource(context: Context, on: Boolean) {
        attach(context)
        if (!mirrorReady()) return
        markPhoneIsSource(on, byUser = true)
        CompanionOutbox.trySend(Envelope(type = MessageType.AUDIO_EVERYWHERE_SET,
            payload = buildJsonObject { put("source", if (on) "mirror" else "pc"); put("on", true) }), requiredVersion = 31)
        LogStore.log(LogLevel.INFO, "Play everywhere: ${if (on) "this phone's sound, taken the way Mirror screen does" else "back to the PC's sound"}")
    }

    /**
     * Re-sync: this phone learns the PC's clock afresh and every output jumps back onto its
     * target; a v31 PC also re-aligns its own speakers and drops the group delay to what is
     * needed now.
     */
    fun resync() {
        SyncedAudio.resync()
        if (mirrorReady()) send(buildJsonObject { put("resync", true) })
        LogStore.log(LogLevel.INFO, "Play everywhere: re-sync")
    }

    private fun markPhoneIsSource(value: Boolean, byUser: Boolean) {
        val now = System.currentTimeMillis()
        if (byUser) sourceChosenAt = now
        // A state sent before the PC saw the user's choice mustn't undo it.
        else if (now - sourceChosenAt < 4_000) return
        if (phoneIsSource == value) return
        phoneIsSource = value
        if (!SyncedAudio.streaming) return
        val context = appContext ?: return
        // Tracks carry their usage from birth: reopen them (off the caller's thread: closing one
        // waits for its writer), and give up (or take) the media role.
        thread(name = "linc-everywhere-source", isDaemon = true) {
            if (value) runLogged("Play everywhere: closing the media controls") { PcPlaybackSession.stop(context) }
            else runLogged("Play everywhere: showing the media controls") { PcPlaybackSession.start(context) }
            SyncedAudio.setOutputs(context, emptyList())
            applyOutputs()
        }
    }

    fun calibrate() = send(buildJsonObject { put("calibrate", true) })

    fun clicks() = send(buildJsonObject { put("clicks", true) })

    fun resetAlignment() = send(buildJsonObject { put("resetAlignment", true) })

    private fun send(payload: JsonObject) =
        CompanionOutbox.trySend(Envelope(type = MessageType.AUDIO_EVERYWHERE_SET, payload = payload), requiredVersion = 26)

    // ---- from the PC ------------------------------------------------------------------------

    fun onState(payload: JsonObject) {
        fun str(o: JsonObject, k: String) = (o[k] as? JsonPrimitive)?.takeIf { it.isString }?.contentOrNull
        fun bool(o: JsonObject, k: String) = (o[k] as? JsonPrimitive)?.booleanOrNull ?: false
        fun num(o: JsonObject, k: String) = (o[k] as? JsonPrimitive)?.doubleOrNull ?: 0.0
        val devices = (payload["devices"] as? JsonArray).orEmpty().mapNotNull { e ->
            val o = runCatching { e.jsonObject }.getOrNull() ?: return@mapNotNull null
            EverywhereDevice(
                id = str(o, "id") ?: return@mapNotNull null,
                name = str(o, "name") ?: "",
                kind = str(o, "kind") ?: "pc",
                bluetooth = bool(o, "bluetooth"),
                enabled = bool(o, "enabled"),
                volume = (o["volume"] as? JsonPrimitive)?.intOrNull ?: 100,
                muted = bool(o, "muted"),
                autoMs = num(o, "autoMs"),
                manualMs = num(o, "manualMs"),
                hiddenUs = (o["hiddenUs"] as? JsonPrimitive)?.longOrNull ?: 0,
                calibrated = bool(o, "calibrated"),
                status = str(o, "status") ?: "",
                offByMs = num(o, "offByMs"),
                problem = str(o, "problem"),
            )
        }
        val delayMs = (payload["delayMs"] as? JsonPrimitive)?.intOrNull ?: 0
        if (delayMs > 0) delayUs = delayMs * 1000L
        val source = str(payload, "source") ?: "pc"
        val isOn = bool(payload, "on")
        if (source == "mirror" && isOn) markPhoneIsSource(true, byUser = false)
        else if (!isOn || source == "pc") markPhoneIsSource(false, byUser = false)
        _state.value = _state.value.copy(
            on = bool(payload, "on"),
            error = str(payload, "error"),
            delayMs = delayMs,
            marginMs = (payload["marginMs"] as? JsonPrimitive)?.intOrNull ?: 0,
            testing = str(payload, "testing"),
            source = source,
            note = str(payload, "note"),
            devices = devices,
        )
        applyOutputs()
    }

    /** Test sounds for this phone's outputs, and — for an alignment — the microphone listens to them all. */
    fun onTest(payload: JsonObject) {
        val seq = (payload["seq"] as? JsonPrimitive)?.intOrNull ?: return
        val mode = (payload["mode"] as? JsonPrimitive)?.contentOrNull ?: return
        val list = (payload["sounds"] as? JsonArray).orEmpty().mapNotNull { e ->
            val o = runCatching { e.jsonObject }.getOrNull() ?: return@mapNotNull null
            TestSound(
                deviceId = (o["id"] as? JsonPrimitive)?.contentOrNull ?: return@mapNotNull null,
                atUs = (o["atUs"] as? JsonPrimitive)?.longOrNull ?: return@mapNotNull null,
                kind = if ((o["kind"] as? JsonPrimitive)?.contentOrNull == "click") TestSoundKind.CLICK else TestSoundKind.CHIRP,
            )
        }
        if (list.isEmpty()) return
        sounds = list.groupBy { it.deviceId }
        soundsUntilUs = list.maxOf { it.atUs } + 500_000
        applyOutputs()
        if (mode == "calibrate") {
            val from = (payload["listenFromUs"] as? JsonPrimitive)?.longOrNull ?: (list.minOf { it.atUs } - 300_000)
            val to = (payload["listenToUs"] as? JsonPrimitive)?.longOrNull ?: soundsUntilUs
            thread(name = "linc-everywhere-listen", isDaemon = true) { listen(seq, list, from, to) }
        }
    }

    /**
     * The slider's 0–100 as a gain, on a decibel scale the ear hears as even steps (100 = full,
     * 50 ≈ −20 dB, 10 ≈ −36 dB): a straight or squared slider put most of the useful range in the
     * last quarter, which is why it seemed to do little and then too much.
     */
    fun volumeGain(volume: Int): Float = when {
        volume <= 0 -> 0f
        volume >= 100 -> 1f
        else -> Math.pow(10.0, (volume - 100) * 0.4 / 20).toFloat()
    }

    fun soundsFor(id: String): List<TestSound> {
        val s = sounds
        if (s.isEmpty()) return emptyList()
        if (SyncedAudio.clock.ready && SyncedAudio.clock.toPc(SyncedAudio.nowUs()) > soundsUntilUs) {
            sounds = emptyMap()
            return emptyList()
        }
        return s[id].orEmpty()
    }

    // ---- the phone's outputs ------------------------------------------------------------------

    /** Start the stream's outputs (or stop them), and its report loop. */
    fun onStreamChanged() {
        applyOutputs()
        synchronized(this) {
            if (SyncedAudio.streaming && reporter?.isAlive != true) {
                reporter = thread(name = "linc-everywhere-report", isDaemon = true) {
                    try {
                        while (SyncedAudio.streaming) {
                            sendReport()
                            Thread.sleep(2_000)
                        }
                    } catch (_: InterruptedException) { }
                }
            }
        }
    }

    private fun applyOutputs() {
        val context = appContext ?: return
        val state = _state.value
        val testing = state.testing != null || sounds.isNotEmpty()
        val wanted = ArrayList<PhoneOutputSpec>()
        synchronized(specs) {
            for (o in AudioOutputs.list(context)) {
                // An output the PC hasn't heard of yet plays (the PC turns new phone outputs on).
                val d = state.devices.firstOrNull { it.id == o.id }
                if (d != null && !d.enabled) continue
                val spec = specs.getOrPut(o.id) { PhoneOutputSpec(o.id, o.name, o.device) }
                spec.gain = volumeGain(d?.volume ?: 100)
                spec.silenced = (d?.muted ?: false) || testing
                val hidden = d?.hiddenUs ?: 0
                if (hidden != spec.hiddenUs) {
                    spec.hiddenUs = hidden
                    SyncedAudio.outputsSnapshot().firstOrNull { it.spec.id == o.id }?.engine?.realign()
                }
                wanted += spec
            }
        }
        SyncedAudio.setOutputs(context, if (state.on || SyncedAudio.streaming) wanted else emptyList())
    }

    private fun sendReport(extra: JsonObject? = null) {
        val context = appContext ?: return
        val running = SyncedAudio.outputsSnapshot().associateBy { it.spec.id }
        val payload = buildJsonObject {
            put("outputs", buildJsonArray {
                for (o in AudioOutputs.list(context)) add(buildJsonObject {
                    put("id", o.id)
                    put("name", o.name)
                    put("bluetooth", o.bluetooth)
                    val r = running[o.id]
                    put("needUs", r?.needUs ?: 0)
                    put("status", r?.status ?: "off")
                    put("offByUs", r?.offByUs ?: 0.0)
                })
            })
            put("clockReady", SyncedAudio.clock.ready)
            extra?.forEach { (k, v) -> put(k, v) }
        }
        CompanionOutbox.trySend(Envelope(type = MessageType.AUDIO_EVERYWHERE_REPORT, payload = payload), requiredVersion = 26)
    }

    fun reportClock(uncertaintyUs: Long, driftPpm: Double) = sendReport(buildJsonObject {
        put("clock", buildJsonObject { put("uncertaintyUs", uncertaintyUs); put("driftPpm", driftPpm) })
    })

    // ---- the microphone ---------------------------------------------------------------------

    /**
     * Record from [fromUs] to [toUs] (PC clock) and find each chirp. What the microphone hears
     * versus when the sound was due is each speaker's error; any constant the microphone itself
     * adds is the same for every speaker, so it can't put them out of step with each other.
     */
    @SuppressLint("MissingPermission") // checked first
    private fun listen(seq: Int, list: List<TestSound>, fromUs: Long, toUs: Long) {
        val context = appContext
        val clock = SyncedAudio.clock
        fun fail(why: String) {
            LogStore.log(LogLevel.WARN, "Play everywhere: alignment didn't run: $why")
            sendReport(buildJsonObject { put("calibration", buildJsonObject { put("seq", seq); put("error", why); put("results", JsonArray(emptyList())) }) })
        }
        if (context == null || !PcVoiceAccess.hasMicPermission(context)) return fail("Linc isn't allowed to use the microphone.")
        if (!clock.ready) return fail("The phone hasn't learnt the PC's clock yet.")
        _state.value = _state.value.copy(listening = true)
        val rate = 48_000
        val manager = context.getSystemService(AudioManager::class.java)
        val source = if (manager?.getProperty(AudioManager.PROPERTY_SUPPORT_AUDIO_SOURCE_UNPROCESSED) == "true")
            MediaRecorder.AudioSource.UNPROCESSED else MediaRecorder.AudioSource.VOICE_RECOGNITION
        val record = runLogged("Play everywhere: opening the microphone") {
            AudioRecord(source, rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT,
                maxOf(AudioRecord.getMinBufferSize(rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT), rate))
        }.getOrNull()
        if (record == null || record.state != AudioRecord.STATE_INITIALIZED) {
            record?.release()
            _state.value = _state.value.copy(listening = false)
            return fail("The microphone is busy (is Voice Access or a call using it?).")
        }
        var samples = ShortArray(rate * 20)
        var count = 0
        val chunk = ShortArray(rate / 50)
        val ts = AudioTimestamp()
        var stampFrame = -1L
        var stampUs = 0L
        var lastReadUs = 0L
        try {
            // Wait until shortly before the first sound, so the buffer only holds what matters.
            while (clock.toPc(SyncedAudio.nowUs()) < fromUs - 200_000) Thread.sleep(20)
            record.startRecording()
            val deadline = SyncedAudio.nowUs() + 60_000_000
            while (clock.toPc(SyncedAudio.nowUs()) < toUs && SyncedAudio.nowUs() < deadline) {
                val n = record.read(chunk, 0, chunk.size)
                if (n <= 0) break
                lastReadUs = SyncedAudio.nowUs()
                if (count + n > samples.size) samples = samples.copyOf(samples.size * 2)
                System.arraycopy(chunk, 0, samples, count, n)
                count += n
                if (record.getTimestamp(ts, AudioTimestamp.TIMEBASE_MONOTONIC) == AudioRecord.SUCCESS) {
                    stampFrame = ts.framePosition
                    stampUs = ts.nanoTime / 1000
                }
            }
        } catch (e: Exception) {
            LogStore.log(LogLevel.WARN, "Play everywhere: recording failed (${e.message})")
        } finally {
            runCatching { record.stop() }
            record.release()
        }
        // When frame 0 was captured: from the microphone's own timestamp, or failing that from
        // when the last read returned (a constant later, the same for every speaker).
        val frame0Us = if (stampFrame >= 0) stampUs - stampFrame * 1_000_000 / rate
        else lastReadUs - count * 1_000_000L / rate
        val audio = FloatArray(count) { samples[it] / 32768f }
        val chirp = TestSignal.chirp(rate)

        val errors = HashMap<String, MutableList<Long>>()
        val snrs = HashMap<String, MutableList<Double>>()
        for (sound in list.filter { it.kind == TestSoundKind.CHIRP }) {
            val expectedPhone = clock.toPhone(sound.atUs)
            val at = ((expectedPhone - frame0Us) * rate / 1_000_000).toInt()
            val hit = ChirpDetector.find(audio, at - rate / 5, at + rate * 6 / 10, chirp, rate) ?: continue
            val heardPhone = frame0Us + hit.index.toLong() * 1_000_000 / rate
            errors.getOrPut(sound.deviceId) { ArrayList() } += clock.toPc(heardPhone) - sound.atUs
            snrs.getOrPut(sound.deviceId) { ArrayList() } += hit.snr
        }
        val names = _state.value.devices.associate { it.id to it.name }
        val results = list.map { it.deviceId }.distinct().map { id ->
            CalibrationMath.combine(id, errors[id].orEmpty(), snrs[id].orEmpty()).also {
                LogStore.log(LogLevel.INFO, "Play everywhere: heard ${names[id] ?: id} " +
                    if (it.ok) "${"%+.1f".format(it.errorUs / 1000.0)} ms off (${errors[id]?.size ?: 0} of 3 chirps)" else "unclearly (${errors[id]?.size ?: 0} of 3 chirps)")
            }
        }
        _state.value = _state.value.copy(listening = false)
        sendReport(buildJsonObject {
            put("calibration", buildJsonObject {
                put("seq", seq)
                put("results", buildJsonArray {
                    for (r in results) add(buildJsonObject {
                        put("id", r.deviceId); put("ok", r.ok); put("errorUs", r.errorUs); put("snr", r.snr)
                    })
                })
            })
        })
    }
}
