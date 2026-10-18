package app.linc.android.service

import android.annotation.SuppressLint
import android.content.Context
import android.media.AudioAttributes
import android.media.AudioDeviceInfo
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTimestamp
import android.media.AudioTrack
import android.media.MediaCodec
import android.media.MediaFormat
import java.io.DataInputStream
import java.io.DataOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.concurrent.thread

/**
 * The phone's half of Play everywhere's sound (v26, D-096): the PC's stream with clock-stamped
 * blocks, played on each chosen phone output so it is heard at the same moment as the PC's own
 * speakers.
 *
 * - **Clock.** Probes go up the channel's back direction ("LCLK", 0, phone time); each answer
 *   is a config-flagged packet with three longs. [ClockEstimator] turns them into the PC's time.
 * - **Sound.** AAC frames are decoded and the PCM, stamped with its PC capture time (MediaCodec
 *   carries the input timestamp through), is handed to every [PhoneOutput]'s queue.
 * - **Outputs.** One [AudioTrack] per output, each pinned to its device with
 *   `setPreferredDevice`, so the phone's speaker and a Bluetooth speaker can play together.
 */
object SyncedAudio {

    val clock = ClockEstimator()

    /** Phone-clock µs. */
    fun nowUs(): Long = System.nanoTime() / 1000

    private val outputs = LinkedHashMap<String, PhoneOutput>()

    @Volatile var rate = 48_000
        private set

    @Volatile var streaming = false
        private set

    /** How long blocks take from capture on the PC to arrival here (µs): the worst of the last few seconds. */
    @Volatile var arrivalUs = 0L
        private set
    private var arrivalAt = 0L

    fun outputsSnapshot(): List<PhoneOutput> = synchronized(outputs) { outputs.values.toList() }

    /** Open or close outputs to match what the mixer says should play. */
    fun setOutputs(context: Context, wanted: List<PhoneOutputSpec>) {
        val stop = ArrayList<PhoneOutput>()
        synchronized(outputs) {
            for (id in outputs.keys.toList()) {
                if (!streaming || wanted.none { it.id == id }) stop += outputs.remove(id)!!
            }
            if (streaming) {
                for (spec in wanted) if (spec.id !in outputs) outputs[spec.id] = PhoneOutput(context, spec, rate)
            }
        }
        stop.forEach { it.close() }
    }

    fun receive(input: DataInputStream, output: DataOutputStream, sampleRate: Int, channels: Int) {
        val format = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_AAC, sampleRate, channels).apply {
            setByteBuffer("csd-0", ByteBuffer.wrap(AudioReceiver.audioSpecificConfig(sampleRate, channels)))
            setInteger(MediaFormat.KEY_IS_ADTS, 0)
        }
        val codec = runCatching {
            MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_AUDIO_AAC).apply { configure(format, null, null, 0); start() }
        }.getOrElse {
            LogStore.log(LogLevel.WARN, "Play everywhere: couldn't start the decoder: ${it.message}")
            return
        }
        rate = sampleRate
        clock.reset()
        streaming = true
        LogStore.log(LogLevel.INFO, "Play everywhere: the PC's sound is arriving (${sampleRate} Hz, in step)")
        AudioEverywhere.onStreamChanged()

        val prober = thread(name = "linc-everywhere-clock", isDaemon = true) { probe(output) }
        val info = MediaCodec.BufferInfo()
        try {
            while (true) {
                val frame = VideoFrame.read(input) ?: break
                if (frame.config) {
                    if (frame.data.size == 24) {
                        val t3 = nowUs()
                        val b = ByteBuffer.wrap(frame.data).order(ByteOrder.BIG_ENDIAN)
                        clock.add(b.getLong(0), b.getLong(8), b.getLong(16), t3)
                    }
                    continue
                }
                val index = codec.dequeueInputBuffer(20_000)
                if (index >= 0) {
                    codec.getInputBuffer(index)?.apply { clear(); put(frame.data) }
                    codec.queueInputBuffer(index, 0, frame.data.size, frame.timestampUs, 0)
                }
                while (true) {
                    val out = codec.dequeueOutputBuffer(info, 0)
                    if (out < 0) break
                    val pcm = codec.getOutputBuffer(out)
                    if (pcm != null && info.size > 0) {
                        pcm.position(info.offset)
                        pcm.limit(info.offset + info.size)
                        val shorts = ShortArray(info.size / 2)
                        pcm.order(ByteOrder.nativeOrder()).asShortBuffer().get(shorts)
                        deliver(SoundBlock(if (channels == 1) toStereo(shorts) else shorts, info.presentationTimeUs))
                    }
                    codec.releaseOutputBuffer(out, false)
                }
            }
        } catch (_: Exception) {
            // The channel closed or the decoder faulted: the sound has ended either way.
        } finally {
            streaming = false
            prober.interrupt()
            runCatching { codec.stop() }
            runCatching { codec.release() }
            synchronized(outputs) { outputs.values.toList().also { outputs.clear() } }.forEach { it.close() }
            LogStore.log(LogLevel.INFO, "Play everywhere: the PC's sound stopped")
            AudioEverywhere.onStreamChanged()
        }
    }

    private fun deliver(block: SoundBlock) {
        if (clock.ready) {
            val now = nowUs()
            val age = clock.toPc(now) - block.captureUs
            if (age >= arrivalUs || now - arrivalAt > 5_000_000) { arrivalUs = maxOf(0L, age); arrivalAt = now }
        }
        val delay = AudioEverywhere.delayUs
        for (o in outputsSnapshot()) o.engine.enqueue(block, delay + 1_000_000)
    }

    private fun toStereo(mono: ShortArray) = ShortArray(mono.size * 2) { mono[it / 2] }

    /** Probe the PC's clock: quickly at first, then once a second to follow drift. */
    private fun probe(output: DataOutputStream) {
        val started = nowUs()
        val buffer = ByteBuffer.allocate(16).order(ByteOrder.BIG_ENDIAN)
        var lastLog = started
        try {
            while (streaming) {
                buffer.clear()
                buffer.putInt(CLOCK_MAGIC).putInt(0).putLong(nowUs())
                synchronized(output) { output.write(buffer.array()); output.flush() }
                val now = nowUs()
                if (clock.ready && now - lastLog > 30_000_000) {
                    lastLog = now
                    LogStore.log(LogLevel.INFO, "Play everywhere: PC clock to within ${clock.uncertaintyUs / 1000.0} ms, drifting ${"%.0f".format(clock.driftPpm)} ppm")
                    AudioEverywhere.reportClock(clock.uncertaintyUs, clock.driftPpm)
                }
                Thread.sleep(if (now - started < 5_000_000) 200 else 1_000)
            }
        } catch (_: InterruptedException) {
        } catch (e: Exception) {
            if (streaming) LogStore.log(LogLevel.WARN, "Play everywhere: couldn't time the PC's clock (${e.message})")
        }
    }

    private const val CLOCK_MAGIC = 0x4C434C4B // "LCLK"
}

/** An output the mixer wants playing: which device, how loud, how late it hides. */
data class PhoneOutputSpec(
    val id: String,
    val name: String,
    val device: AudioDeviceInfo?,
    @Volatile var gain: Float = 1f,
    @Volatile var silenced: Boolean = false,
    @Volatile var hiddenUs: Long = 0,
)

/**
 * One phone output in step with the rest: its own track, pinned to its device, written 10 ms at
 * a time by its own thread. When the next frame will be heard comes from the track's timestamp
 * (frame N played at time T), mapped onto the PC's clock, plus the output's hidden latency.
 */
class PhoneOutput(context: Context, val spec: PhoneOutputSpec, private val rate: Int) {

    val engine = PlayoutEngine(rate)

    @Volatile var status = "starting"
        private set
    @Volatile var offByUs = 0.0
        private set
    @Volatile private var outUs = 0L
    private var outAt = 0L
    @Volatile private var running = true

    /** What this output needs the group delay to be: arrival, its own output, its hidden latency, a margin. */
    val needUs: Long get() = SyncedAudio.arrivalUs + outUs + spec.hiddenUs + NEED_MARGIN_US

    var skippedFrames = 0L
    var underruns = 0L

    private val track: AudioTrack = AudioTrack.Builder()
        .setAudioAttributes(AudioAttributes.Builder()
            .setUsage(AudioAttributes.USAGE_MEDIA)
            .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
            .build())
        .setAudioFormat(AudioFormat.Builder()
            .setSampleRate(rate)
            .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
            .setEncoding(AudioFormat.ENCODING_PCM_FLOAT)
            .build())
        .setBufferSizeInBytes(maxOf(
            AudioTrack.getMinBufferSize(rate, AudioFormat.CHANNEL_OUT_STEREO, AudioFormat.ENCODING_PCM_FLOAT),
            rate * 8 * TRACK_MS / 1000))
        .setTransferMode(AudioTrack.MODE_STREAM)
        .build()

    init {
        spec.device?.let { track.setPreferredDevice(it) } // before the worker starts it playing
    }

    private val worker = thread(name = "linc-everywhere-${spec.id}", isDaemon = true, priority = Thread.MAX_PRIORITY) { run() }

    private fun run() {
        val chirp = TestSignal.chirp(rate)
        val click = TestSignal.click(rate)
        val chunk = rate / 100
        val buffer = FloatArray(chunk * 2)
        val ts = AudioTimestamp()
        var written = 0L
        var last = SyncedAudio.nowUs()
        var checkedRoute = false
        try {
            track.play()
            while (running) {
                val now = SyncedAudio.nowUs()
                val stamped = track.getTimestamp(ts) && ts.nanoTime > 0
                if (!stamped || !SyncedAudio.clock.ready) {
                    // Until the track says where it is and the PC's clock is known, play silence.
                    buffer.fill(0f)
                } else {
                    if (!checkedRoute) { checkedRoute = true; checkRoute() }
                    val nextLeavesUs = ts.nanoTime / 1000 + (written - ts.framePosition) * 1_000_000 / rate
                    val out = nextLeavesUs - now
                    if (out >= outUs || now - outAt > 5_000_000) { outUs = maxOf(0L, out); outAt = now }
                    val firstHeard = SyncedAudio.clock.toPc(nextLeavesUs) + spec.hiddenUs
                    val stats = engine.render(buffer, chunk, firstHeard, AudioEverywhere.delayUs, (now - last) / 1_000_000.0)
                    skippedFrames += stats.skippedFrames
                    if (stats.underrun) underruns++
                    offByUs = engine.controller.smoothedErrorUs
                    status = "playing"
                    if (spec.silenced) buffer.fill(0f)
                    else if (spec.gain < 0.999f) for (i in buffer.indices) buffer[i] *= spec.gain
                    val sounds = AudioEverywhere.soundsFor(spec.id)
                    if (sounds.isNotEmpty()) TestSignal.mix(buffer, chunk, rate, firstHeard, sounds, chirp, click)
                }
                last = now
                val n = track.write(buffer, 0, buffer.size, AudioTrack.WRITE_BLOCKING)
                if (n < 0) throw IllegalStateException("track write failed ($n)")
                written += n / 2
            }
        } catch (e: Exception) {
            if (running) {
                status = "error"
                LogStore.log(LogLevel.WARN, "Play everywhere: ${spec.name} stopped (${e.message})")
            }
        } finally {
            runCatching { track.stop() }
            runCatching { track.release() }
        }
    }

    /** Pinning a track to a device is a request; say so if Android routed it elsewhere. */
    private fun checkRoute() {
        val routed = track.routedDevice
        val wanted = spec.device
        if (wanted != null && routed != null && routed.id != wanted.id) {
            LogStore.log(LogLevel.WARN, "Play everywhere: asked for ${spec.name} but Android is playing it on ${AudioOutputs.label(routed)}")
            status = "rerouted"
        } else {
            LogStore.log(LogLevel.INFO, "Play everywhere: ${spec.name} is playing")
        }
    }

    fun close() {
        running = false
        runCatching { track.pause(); track.flush() }
        worker.join(500)
    }

    private companion object {
        const val TRACK_MS = 80
        const val NEED_MARGIN_US = 20_000L
    }
}

/** The phone's own outputs that can join in: the speaker, Bluetooth, wired and USB. */
object AudioOutputs {

    data class Output(val id: String, val name: String, val bluetooth: Boolean, val device: AudioDeviceInfo)

    @SuppressLint("NewApi") // the BLE types are only reported where they exist
    fun list(context: Context): List<Output> {
        val manager = context.getSystemService(AudioManager::class.java) ?: return emptyList()
        return manager.getDevices(AudioManager.GET_DEVICES_OUTPUTS).mapNotNull { d ->
            when (d.type) {
                AudioDeviceInfo.TYPE_BUILTIN_SPEAKER -> Output("phone:speaker", "This phone's speaker", false, d)
                AudioDeviceInfo.TYPE_BLUETOOTH_A2DP, AudioDeviceInfo.TYPE_BLE_HEADSET, AudioDeviceInfo.TYPE_BLE_SPEAKER ->
                    Output("phone:bt:${d.productName}", "${d.productName} (Bluetooth, on the phone)", true, d)
                AudioDeviceInfo.TYPE_WIRED_HEADPHONES, AudioDeviceInfo.TYPE_WIRED_HEADSET ->
                    Output("phone:wired", "Headphones on the phone", false, d)
                AudioDeviceInfo.TYPE_USB_HEADSET, AudioDeviceInfo.TYPE_USB_DEVICE ->
                    Output("phone:usb:${d.productName}", "${d.productName} (USB, on the phone)", false, d)
                else -> null
            }
        }.distinctBy { it.id }
    }

    fun label(d: AudioDeviceInfo): String = when (d.type) {
        AudioDeviceInfo.TYPE_BUILTIN_SPEAKER -> "the phone's speaker"
        else -> d.productName.toString()
    }
}
