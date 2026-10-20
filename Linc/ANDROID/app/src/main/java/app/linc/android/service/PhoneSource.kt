package app.linc.android.service

import android.annotation.SuppressLint
import android.content.Context
import android.content.Intent
import android.media.AudioAttributes
import android.media.AudioDeviceInfo
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioPlaybackCaptureConfiguration
import android.media.AudioRecord
import android.media.AudioTimestamp
import android.media.AudioTrack
import android.media.MediaRecorder
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import java.io.DataInputStream
import java.io.DataOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.concurrent.thread
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/** The phone as Play everywhere's source, as the mixer shows it. */
data class PhoneSourceState(
    val on: Boolean = false,
    /** The PC is getting the sound too (pc-phone-audio channel 8). */
    val pcLinked: Boolean = false,
    /** What the phone itself plays apps' sound on (Linc can't delay it, so everything follows it). */
    val nativeName: String = "",
    val delayMs: Int = 0,
    val note: String? = null,
    val listening: Boolean = false,
    /** This phone's other outputs that replay the sound in step: id to name. */
    val replays: List<Pair<String, String>> = emptyList(),
)

/**
 * The phone as the source (D-097): what apps on this phone play — music, video, games — heard
 * on the phone's own output, its other outputs and the PC's speakers at once, in step.
 *
 * - **Capture.** Android's playback capture (a one-time "start recording?" consent, which
 *   Android requires for it) records what apps play. Apps that opt out of capture (some
 *   streaming apps with protected audio) can't be included; Android says so, not Linc.
 * - **The reference.** Android keeps playing the sound on its own output, usually a Bluetooth
 *   device. Linc can't delay that, but it is also the slowest output there is, so it is the
 *   moment everything else aims for: capture time + its latency ([NativeRoute], then measured).
 * - **Replays.** The phone's other outputs (its speaker while Bluetooth plays, say) and the
 *   PC's speakers play Linc's copy aimed at that moment. Linc's own tracks are marked not
 *   capturable, so the copies are never recorded back.
 * - **Without a PC** it all still works: the phone's speaker and Bluetooth together.
 */
object PhoneSource {

    private val _state = MutableStateFlow(PhoneSourceState())
    val state: StateFlow<PhoneSourceState> = _state

    const val RATE = 48_000
    const val ACTION_START = "app.linc.android.action.PHONE_SOURCE_START"
    const val ACTION_STOP = "app.linc.android.action.PHONE_SOURCE_STOP"
    const val EXTRA_RESULT_CODE = "resultCode"
    const val EXTRA_RESULT_DATA = "resultData"
    private const val PREFS = "linc.phonesource"
    private const val CLOCK_MAGIC = 0x4C434C4B // "LCLK"

    /** The PC's clock over channel 8, when the PC is listening. */
    val clock = ClockEstimator()

    @Volatile private var running = false
    @Volatile private var pcOut: DataOutputStream? = null
    @Volatile private var muteLocal = false
    @Volatile private var mutePc = false
    @Volatile private var nativeType = "speaker"
    @Volatile private var nativeId = "phone:speaker"
    @Volatile private var nativeTrimUs = 0L
    private var projection: MediaProjection? = null
    private var record: AudioRecord? = null
    private var appContext: Context? = null
    private val outputs = LinkedHashMap<String, PhoneOutput>()

    /** When Android's own output plays what was captured at t: t + this. */
    val nativeDelayUs: Long get() {
        val hidden = AudioEverywhere.state.value.devices.firstOrNull { it.id == nativeId }?.hiddenUs ?: 0
        return NativeRoute.baseUs(nativeType) + hidden + nativeTrimUs
    }

    /** Local replays: this phone's own clock, aimed at the native output's moment. */
    private val timeline = object : PlayoutTimeline {
        override val ready get() = true
        override fun fromPhoneUs(phoneUs: Long) = phoneUs
        override fun delayUs(needUs: Long) = nativeDelayUs
        override val arrivalUs get() = 10_000L // one capture read
        override fun sounds(id: String) = emptyList<TestSound>()
    }

    fun intentToStart(context: Context, resultCode: Int, data: Intent): Intent =
        Intent(context, CompanionService::class.java).setAction(ACTION_START)
            .putExtra(EXTRA_RESULT_CODE, resultCode).putExtra(EXTRA_RESULT_DATA, data)

    /** From the companion service, once it runs as a media-projection service (Android requires that first). */
    @SuppressLint("MissingPermission") // playback capture needs RECORD_AUDIO; the consent screen asked for it
    @Synchronized
    fun begin(context: Context, resultCode: Int, data: Intent) {
        if (running) return
        appContext = context.applicationContext
        if (!PcVoiceAccess.hasMicPermission(context)) {
            _state.value = _state.value.copy(note = "Linc needs the microphone permission to record what apps play.")
            return
        }
        val manager = context.getSystemService(MediaProjectionManager::class.java)
        val p = runCatching { manager.getMediaProjection(resultCode, data) }.getOrNull()
        if (p == null) {
            _state.value = _state.value.copy(note = "Android didn't allow recording what apps play.")
            return
        }
        p.registerCallback(object : MediaProjection.Callback() {
            override fun onStop() { stop(context, "Android stopped the recording") }
        }, android.os.Handler(android.os.Looper.getMainLooper()))
        val config = AudioPlaybackCaptureConfiguration.Builder(p)
            .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
            .addMatchingUsage(AudioAttributes.USAGE_GAME)
            .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
            .build()
        val r = runCatching {
            AudioRecord.Builder()
                .setAudioFormat(AudioFormat.Builder().setSampleRate(RATE)
                    .setChannelMask(AudioFormat.CHANNEL_IN_STEREO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
                .setBufferSizeInBytes(RATE * 4 / 5) // 200 ms
                .setAudioPlaybackCaptureConfig(config)
                .build()
        }.getOrNull()
        if (r == null || r.state != AudioRecord.STATE_INITIALIZED) {
            r?.release(); p.stop()
            _state.value = _state.value.copy(note = "Android couldn't start recording what apps play.")
            return
        }
        projection = p
        record = r
        running = true
        pickNative(context)
        nativeTrimUs = prefs(context).getLong("trim:$nativeId", 0)
        LogStore.log(LogLevel.INFO, "Play everywhere: this phone is the source; apps' sound plays on ${_state.value.nativeName} and Linc follows it (${nativeDelayUs / 1000} ms)")
        thread(name = "linc-phone-capture", isDaemon = true, priority = Thread.MAX_PRIORITY) { capture(r) }
        applyOutputs()
        // Tell the PC (when there is one) to listen on channel 8.
        AudioEverywhere.setPhoneSource(true)
        publish()
    }

    fun stop(context: Context, why: String) {
        synchronized(this) {
            if (!running) return
            running = false
            runCatching { record?.stop() }
            record?.release(); record = null
            runCatching { projection?.stop() }
            projection = null
        }
        AudioEverywhere.setPhoneSource(false)
        synchronized(outputs) { outputs.values.toList().also { outputs.clear() } }.forEach { it.close() }
        runCatching { context.startService(Intent(context, CompanionService::class.java).setAction(ACTION_STOP)) }
        _state.value = PhoneSourceState(note = null)
        LogStore.log(LogLevel.INFO, "Play everywhere: the phone stopped being the source ($why)")
    }

    val isOn: Boolean get() = running

    private fun prefs(context: Context) = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    /** Where Android plays media now: the reference. */
    private fun pickNative(context: Context) {
        val manager = context.getSystemService(AudioManager::class.java)
        val all = AudioOutputs.list(context)
        val routed: AudioDeviceInfo? = if (Build.VERSION.SDK_INT >= 33) {
            runCatching {
                manager.getAudioDevicesForAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).build()).firstOrNull()
            }.getOrNull()
        } else null
        val native = all.firstOrNull { routed != null && it.device.id == routed.id }
            ?: all.firstOrNull { it.bluetooth } ?: all.firstOrNull { it.id.startsWith("phone:usb") }
            ?: all.firstOrNull { it.id == "phone:wired" } ?: all.firstOrNull { it.id == "phone:speaker" }
        nativeId = native?.id ?: "phone:speaker"
        nativeType = when {
            native == null -> "speaker"
            native.device.type == AudioDeviceInfo.TYPE_BLUETOOTH_A2DP -> "bt"
            native.bluetooth -> "ble"
            native.id == "phone:wired" -> "wired"
            native.id.startsWith("phone:usb") -> "usb"
            else -> "speaker"
        }
        _state.value = _state.value.copy(nativeName = native?.name ?: "This phone's speaker")
    }

    /** Replays on this phone's other outputs, as the mixer has them (all on unless switched off). */
    fun applyOutputs() {
        val context = appContext ?: return
        if (!running) return
        pickNative(context)
        val enabled = { id: String -> prefs(context).getBoolean("on:$id", true) }
        val wanted = AudioOutputs.list(context).filter { it.id != nativeId && enabled(it.id) }
        val stop = ArrayList<PhoneOutput>()
        synchronized(outputs) {
            for (id in outputs.keys.toList()) if (wanted.none { it.id == id }) stop += outputs.remove(id)!!
            for (o in wanted) if (o.id !in outputs) {
                val spec = PhoneOutputSpec(o.id, o.name, o.device,
                    gain = AudioEverywhere.volumeGain(prefs(context).getInt("vol:${o.id}", 100)),
                    hiddenUs = AudioEverywhere.state.value.devices.firstOrNull { it.id == o.id }?.hiddenUs ?: 0)
                outputs[o.id] = PhoneOutput(context, spec, RATE, timeline)
            }
        }
        stop.forEach { it.close() }
        publish()
    }

    /** The mixer's switch and slider for this phone's own outputs while it is the source. */
    fun setLocal(context: Context, id: String, enabled: Boolean? = null, volume: Int? = null) {
        prefs(context).edit().apply {
            enabled?.let { putBoolean("on:$id", it) }
            volume?.let { putInt("vol:$id", it) }
        }.apply()
        volume?.let { v -> synchronized(outputs) { outputs[id]?.spec?.gain = AudioEverywhere.volumeGain(v) } }
        applyOutputs()
    }

    fun localEnabled(context: Context, id: String) = prefs(context).getBoolean("on:$id", true)
    fun localVolume(context: Context, id: String) = prefs(context).getInt("vol:$id", 100)

    private fun publish() {
        val replays = synchronized(outputs) { outputs.values.map { it.spec.id to it.spec.name } }
        _state.value = _state.value.copy(on = running, pcLinked = pcOut != null, delayMs = (nativeDelayUs / 1000).toInt(), replays = replays)
    }

    // ---- capture --------------------------------------------------------------------------

    private fun capture(r: AudioRecord) {
        runCatching { android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_AUDIO) }
        val frames = RATE / 100
        val chunk = ShortArray(frames * 2)
        val ts = AudioTimestamp()
        var read = 0L
        var stampFrame = -1L
        var stampUs = 0L
        val header = ByteArray(12)
        val payload = ByteBuffer.allocate(frames * 4).order(ByteOrder.LITTLE_ENDIAN)
        try {
            r.startRecording()
            while (running) {
                var got = 0
                while (got < chunk.size && running) {
                    val n = r.read(chunk, got, chunk.size - got)
                    if (n <= 0) throw IllegalStateException("capture read failed ($n)")
                    got += n
                }
                if (r.getTimestamp(ts, AudioTimestamp.TIMEBASE_MONOTONIC) == AudioRecord.SUCCESS) {
                    stampFrame = ts.framePosition; stampUs = ts.nanoTime / 1000
                }
                // When the first frame of this chunk was mixed: from the capture's own stamp, or
                // failing that, now minus the chunk (the same small bias for every output).
                val firstUs = if (stampFrame >= 0) stampUs + (read - stampFrame) * 1_000_000 / RATE
                else SyncedAudio.nowUs() - frames * 1_000_000L / RATE
                read += frames
                val pcm = chunk.copyOf()
                if (muteLocal) java.util.Arrays.fill(pcm, 0)
                val block = SoundBlock(pcm, firstUs)
                for (o in synchronized(outputs) { outputs.values.toList() }) o.engine.enqueue(block, nativeDelayUs + 1_000_000)
                sendToPc(chunk, firstUs, header, payload)
            }
        } catch (e: Exception) {
            if (running) LogStore.log(LogLevel.WARN, "Play everywhere: recording what apps play stopped (${e.message})")
        }
    }

    /** To the PC, stamped with when it must be heard (PC clock), so the PC simply plays it then. */
    private fun sendToPc(chunk: ShortArray, firstUs: Long, header: ByteArray, payload: ByteBuffer) {
        val out = pcOut ?: return
        if (!clock.ready) return
        val heardPcUs = clock.toPc(firstUs + nativeDelayUs)
        payload.clear()
        if (mutePc) repeat(chunk.size) { payload.putShort(0) } else for (s in chunk) payload.putShort(s)
        ByteBuffer.wrap(header).order(ByteOrder.BIG_ENDIAN).putLong(heardPcUs and 0x3FFF_FFFF_FFFF_FFFFL).putInt(payload.position())
        try {
            synchronized(out) { out.write(header); out.write(payload.array(), 0, payload.position()); out.flush() }
        } catch (_: Exception) {
            pcOut = null // the PC hung up; the channel's own loop reports it
        }
    }

    // ---- channel 8: the PC listening ----------------------------------------------------------

    /** Serve pc-phone-audio channel 8 (the PC dialled it after `audio.everywhere.set {source: "phone"}`). */
    fun serve(input: DataInputStream, output: DataOutputStream) {
        clock.reset()
        pcOut = output
        publish()
        LogStore.log(LogLevel.INFO, "Play everywhere: the PC is listening to this phone's sound")
        val prober = thread(name = "linc-phone-clock", isDaemon = true) {
            val started = SyncedAudio.nowUs()
            val probe = ByteBuffer.allocate(28).order(ByteOrder.BIG_ENDIAN)
            try {
                while (pcOut === output) {
                    probe.clear()
                    probe.putLong(Long.MIN_VALUE) // config flag: a probe, not sound
                    probe.putInt(16).putInt(CLOCK_MAGIC).putInt(0).putLong(SyncedAudio.nowUs())
                    synchronized(output) { output.write(probe.array()); output.flush() }
                    Thread.sleep(if (SyncedAudio.nowUs() - started < 5_000_000) 200 else 1_000)
                }
            } catch (_: Exception) { }
        }
        try {
            while (true) {
                val frame = VideoFrame.read(input) ?: break
                if (frame.config && frame.data.size == 24) {
                    val b = ByteBuffer.wrap(frame.data).order(ByteOrder.BIG_ENDIAN)
                    clock.add(b.getLong(0), b.getLong(8), b.getLong(16), SyncedAudio.nowUs())
                }
            }
        } catch (_: Exception) {
        } finally {
            if (pcOut === output) pcOut = null
            prober.interrupt()
            publish()
            LogStore.log(LogLevel.INFO, "Play everywhere: the PC stopped listening to this phone")
        }
    }

    // ---- alignment ----------------------------------------------------------------------------

    /**
     * Line the copies up with what Android itself plays, by ear — the phone's microphone. A
     * chirp is played the way any app's sound is (so it is captured and copied too); the
     * microphone hears Android's own output and the copy, and the gap between them is how far
     * the reference time is off. One round alone first, to learn where Android's own chirp
     * lands; then three with the copies. Pause any music first: the mic must hear only chirps.
     */
    @SuppressLint("MissingPermission")
    fun calibrate(context: Context) {
        if (!running || _state.value.listening) return
        if (!PcVoiceAccess.hasMicPermission(context)) return
        thread(name = "linc-phone-align", isDaemon = true) {
            _state.value = _state.value.copy(listening = true, note = "Listening… keep the room quiet.")
            val useLocal = synchronized(outputs) { outputs.isNotEmpty() }
            if (!useLocal && pcOut == null) {
                _state.value = _state.value.copy(listening = false, note = "Nothing to line up yet: switch on another output, or connect the PC.")
                return@thread
            }
            // Only one copy at a time, so the microphone hears two chirps, never three.
            muteLocal = !useLocal
            mutePc = useLocal
            val result = runCatching { measure() }.getOrElse { LogStore.log(LogLevel.WARN, "Play everywhere: alignment failed (${it.message})"); null }
            muteLocal = false; mutePc = false
            val note = if (result == null) "Couldn't hear the test sounds clearly. Turn the volume up, pause any music, and try again."
            else {
                nativeTrimUs -= result
                prefs(context).edit().putLong("trim:$nativeId", nativeTrimUs).apply()
                LogStore.log(LogLevel.INFO, "Play everywhere: the copies were ${"%+.1f".format(result / 1000.0)} ms off ${_state.value.nativeName}; corrected")
                synchronized(outputs) { outputs.values.forEach { it.engine.realign() } }
                "Lined up with ${_state.value.nativeName}."
            }
            _state.value = _state.value.copy(listening = false, note = note, delayMs = (nativeDelayUs / 1000).toInt())
        }
    }

    /** The copies' median error against Android's own output, µs (positive = copies late); null if unclear. */
    @SuppressLint("MissingPermission")
    private fun measure(): Long? {
        val chirp = TestSignal.chirp(RATE)
        val mic = AudioRecord(MediaRecorder.AudioSource.VOICE_RECOGNITION, RATE, AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT, RATE)
        if (mic.state != AudioRecord.STATE_INITIALIZED) { mic.release(); return null }
        val player = AudioTrack.Builder()
            .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA)
                .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                .setAllowedCapturePolicy(AudioAttributes.ALLOW_CAPTURE_BY_ALL).build())
            .setAudioFormat(AudioFormat.Builder().setSampleRate(RATE).setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                .setEncoding(AudioFormat.ENCODING_PCM_FLOAT).build())
            .setTransferMode(AudioTrack.MODE_STATIC)
            .setBufferSizeInBytes(chirp.size * 4)
            .build()
        player.write(chirp, 0, chirp.size, AudioTrack.WRITE_BLOCKING)
        val rounds = 4
        val spacingMs = 900L
        val total = ((rounds * spacingMs + 800) * RATE / 1000).toInt()
        val rec = ShortArray(total)
        val plays = LongArray(rounds)
        val ts = AudioTimestamp()
        var frame0Us = 0L
        try {
            mic.startRecording()
            var got = 0
            val begin = SyncedAudio.nowUs()
            var next = 0
            while (got < total) {
                val elapsedMs = (SyncedAudio.nowUs() - begin) / 1000
                if (next < rounds && elapsedMs >= 300 + next * spacingMs) {
                    // Round 0 alone (copies muted), then with the copy.
                    if (next == 0) { mutePc = true; muteLocal = true }
                    else if (next == 1) { mutePc = synchronized(outputs) { outputs.isNotEmpty() }; muteLocal = !mutePc }
                    player.stop(); player.reloadStaticData(); player.play()
                    plays[next] = SyncedAudio.nowUs()
                    next++
                }
                val n = mic.read(rec, got, minOf(RATE / 50, total - got))
                if (n <= 0) break
                got += n
                if (mic.getTimestamp(ts, AudioTimestamp.TIMEBASE_MONOTONIC) == AudioRecord.SUCCESS)
                    frame0Us = ts.nanoTime / 1000 - ts.framePosition * 1_000_000 / RATE
            }
        } finally {
            runCatching { mic.stop() }; mic.release()
            runCatching { player.stop() }; player.release()
        }
        if (frame0Us == 0L) return null
        val audio = FloatArray(rec.size) { rec[it] / 32768f }
        fun index(us: Long) = ((us - frame0Us) * RATE / 1_000_000).toInt()
        // Where Android's own chirp lands, after each play() call.
        val alone = ChirpDetector.find(audio, index(plays[0]), index(plays[0] + 600_000), chirp, RATE) ?: return null
        val nativeLagFrames = alone.index - index(plays[0])
        val errors = ArrayList<Long>()
        for (r in 1 until rounds) {
            val nativeAt = index(plays[r]) + nativeLagFrames
            val pair = ChirpDetector.findPair(audio, index(plays[r]), index(plays[r] + 800_000), chirp, RATE, nativeAt) ?: continue
            errors += (pair.second.index - pair.first.index).toLong() * 1_000_000 / RATE
        }
        val verdict = CalibrationMath.combine("native", errors, emptyList())
        return if (verdict.ok) verdict.errorUs else null
    }
}
