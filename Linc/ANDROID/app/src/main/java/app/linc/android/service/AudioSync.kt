package app.linc.android.service

import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.roundToLong
import kotlin.math.sin
import kotlin.math.sqrt

/*
 * The pure core of "Play everywhere" (v26, D-096), the phone's twin of the desktop's
 * AudioSync.cs: the same playout controller and engine (AudioSyncTest pins the same numbers
 * audiosyncsim does), plus what only the phone does — estimating the PC's clock, and timing test
 * sounds from the microphone. Nothing here touches an Android API, so it all runs on plain JUnit.
 *
 * Every block of the PC's sound carries the PC-clock time it was captured, Tc. Every speaker
 * aims to make it heard at Tc + D, the group delay the PC sets for all of them. A speaker knows
 * when the next frame it writes will be heard (the track's timestamp, plus a hidden latency only
 * the microphone can measure, such as a Bluetooth buffer), compares that with the target and
 * corrects the difference: a jump for a big one, a fraction of a percent fast or slow for a
 * small one.
 */

/**
 * The PC's clock as seen from here, NTP-style. Each probe gives four times — sent (phone), got
 * and answered (PC), back (phone) — hence one offset sample whose error is at most half the
 * round trip, minus however long the PC took to answer. Only the quickest round trips are
 * believed (a probe that waited in a Wi-Fi queue is skewed by its wait), and a straight line
 * through them over the last minute follows the two crystals' drift too.
 */
class ClockEstimator {

    private class Sample(val midUs: Long, val offsetUs: Double, val rttUs: Long)

    private val samples = ArrayDeque<Sample>()
    private var a = 0.0
    private var b = 0.0
    private var refUs = 0L

    /** Enough probes to trust: until then nothing plays. */
    var ready = false
        private set

    /** Half the best round trip: how far off the offset can be, at worst. */
    var uncertaintyUs = Long.MAX_VALUE
        private set

    val driftPpm: Double get() = b * 1_000_000

    @Synchronized
    fun add(t0: Long, t1: Long, t2: Long, t3: Long) {
        val rtt = (t3 - t0) - (t2 - t1)
        if (rtt < 0 || t3 < t0) return
        val offset = ((t1 - t0) + (t2 - t3)) / 2.0
        samples.addLast(Sample((t0 + t3) / 2, offset, rtt))
        while (samples.size > WINDOW || (samples.size > 8 && samples.last().midUs - samples.first().midUs > MAX_AGE_US)) {
            samples.removeFirst()
        }
        fit()
    }

    private fun fit() {
        val best = samples.minOf { it.rttUs }
        val good = samples.filter { it.rttUs <= best + maxOf(1_000L, best / 2) }
        uncertaintyUs = best / 2
        refUs = good.last().midUs
        val span = good.last().midUs - good.first().midUs
        if (good.size >= 6 && span >= 5_000_000) {
            var sx = 0.0; var sy = 0.0; var sxx = 0.0; var sxy = 0.0
            for (s in good) {
                val x = (s.midUs - refUs).toDouble()
                sx += x; sy += s.offsetUs; sxx += x * x; sxy += x * s.offsetUs
            }
            val n = good.size
            val den = n * sxx - sx * sx
            b = if (den != 0.0) ((n * sxy - sx * sy) / den).coerceIn(-MAX_DRIFT, MAX_DRIFT) else 0.0
            a = (sy - b * sx) / n
        } else {
            val sorted = good.map { it.offsetUs }.sorted()
            a = sorted[sorted.size / 2]
            b = 0.0
        }
        ready = samples.size >= 3
    }

    @Synchronized
    fun toPc(phoneUs: Long): Long = (phoneUs + a + b * (phoneUs - refUs)).roundToLong()

    @Synchronized
    fun toPhone(pcUs: Long): Long = ((pcUs - a + b * refUs) / (1 + b)).roundToLong()

    @Synchronized
    fun reset() {
        samples.clear(); a = 0.0; b = 0.0; ready = false; uncertaintyUs = Long.MAX_VALUE
    }

    private companion object {
        const val WINDOW = 64
        const val MAX_AGE_US = 60_000_000L
        const val MAX_DRIFT = 500e-6 // 500 ppm: any more and it isn't drift, it's a bad fit
    }
}

enum class PlayoutAction { INSERT_SILENCE, SKIP, TRACK }

data class PlayoutDecision(val action: PlayoutAction, val frames: Long, val ppm: Double)

/**
 * One speaker's control loop (same constants as the desktop's PlayoutController). Not locked, or
 * more than [HARD_US] out: jump — silence for an early speaker, a skip for a late one. Otherwise
 * a smoothed proportional-integral loop sets the play rate within ±0.5%, which no one hears.
 */
class PlayoutController(private val sampleRate: Int) {

    var locked = false
        private set
    var smoothedErrorUs = 0.0
        private set
    private var integral = 0.0

    fun reset() {
        locked = false; smoothedErrorUs = 0.0; integral = 0.0
    }

    fun decide(errorUs: Double, dtSeconds: Double): PlayoutDecision {
        if (!locked || abs(errorUs) > HARD_US) {
            locked = true
            smoothedErrorUs = 0.0
            integral = 0.0
            val frames = (abs(errorUs) * sampleRate / 1_000_000.0).roundToLong()
            if (frames == 0L) return PlayoutDecision(PlayoutAction.TRACK, 0, 0.0)
            return if (errorUs < 0) PlayoutDecision(PlayoutAction.INSERT_SILENCE, frames, 0.0)
            else PlayoutDecision(PlayoutAction.SKIP, frames, 0.0)
        }
        smoothedErrorUs += SMOOTHING * (errorUs - smoothedErrorUs)
        val p = KP * smoothedErrorUs
        if (abs(p) < MAX_PPM) integral = (integral + smoothedErrorUs * maxOf(0.0, dtSeconds)).coerceIn(-INTEGRAL_LIMIT, INTEGRAL_LIMIT)
        return PlayoutDecision(PlayoutAction.TRACK, 0, (p + KI * integral).coerceIn(-MAX_PPM, MAX_PPM))
    }

    companion object {
        const val HARD_US = 40_000.0
        const val MAX_PPM = 5_000.0
        const val KP = 0.5
        const val KI = 0.02
        private const val SMOOTHING = 0.2
        private const val INTEGRAL_LIMIT = 1_000_000.0
    }
}

/** One block of the PC's sound: interleaved 16-bit stereo, captured at [captureUs] (PC clock). */
class SoundBlock(val pcm: ShortArray, val captureUs: Long) {
    val frames: Int get() = pcm.size / 2
}

class PlayoutStats {
    var silenceFrames = 0L
    var skippedFrames = 0L
    var underrun = false
    var ppm = 0.0
}

/**
 * One speaker's playout, the twin of the desktop's PlayoutEngine for the case the phone always
 * has: the track runs at the stream's own rate, so samples are copied untouched and the
 * controller's ppm is applied by dropping or repeating one frame now and then (at 100 ppm, one
 * in 10 000 — a 21 µs step no one hears).
 */
class PlayoutEngine(private val sampleRate: Int) {

    val controller = PlayoutController(sampleRate)

    private val lock = Any()
    private val queue = ArrayDeque<SoundBlock>()
    private var head: SoundBlock? = null
    private var headPos = 0
    private var slip = 0.0
    private var resetPending = false
    private var lastDelayUs = -1L
    /** Frames left of the fade-in after a jump: sound that starts mid-wave would click. */
    private var fadeLeft = 0
    private val fadeFrames = sampleRate * FADE_MS / 1000

    @Volatile var droppedBlocks = 0L
        private set

    val queuedUs: Long
        get() = synchronized(lock) {
            var frames = (head?.frames ?: 0).toLong() - headPos
            for (b in queue) frames += b.frames
            frames * 1_000_000 / sampleRate
        }

    fun enqueue(block: SoundBlock, maxQueuedUs: Long) = synchronized(lock) {
        queue.addLast(block)
        var total = 0L
        for (b in queue) total += b.frames
        val max = maxQueuedUs * sampleRate / 1_000_000
        while (total > max && queue.size > 1) {
            total -= queue.removeFirst().frames
            droppedBlocks++
            resetPending = true
        }
    }

    fun clear() = synchronized(lock) {
        queue.clear(); head = null; headPos = 0; slip = 0.0; controller.reset()
    }

    fun realign() = synchronized(lock) { resetPending = true }

    /**
     * Fill [output] (interleaved stereo float, [frames] frames). Output frame 0 is heard at
     * [firstHeardUs] (PC clock, hidden latency included); each source frame is aimed at its
     * capture time + [delayUs].
     */
    fun render(output: FloatArray, frames: Int, firstHeardUs: Long, delayUs: Long, dtSeconds: Double): PlayoutStats {
        val stats = PlayoutStats()
        output.fill(0f, 0, frames * 2)
        synchronized(lock) {
            if (lastDelayUs >= 0 && abs(delayUs - lastDelayUs) > GAP_US) resetPending = true
            lastDelayUs = delayUs
            var produced = 0
            var decided = false
            var ppm = 0.0
            while (produced < frames) {
                if (!tryHead()) {
                    stats.underrun = true
                    resetPending = true // whatever comes after a dry spell is placed by a jump
                    fadeLeft = fadeFrames
                    break
                }
                val block = head!!
                val heardUs = firstHeardUs + produced * 1_000_000L / sampleRate
                val targetUs = block.captureUs + headPos * 1_000_000L / sampleRate + delayUs
                val error = (heardUs - targetUs).toDouble()
                if (!decided || resetPending) {
                    if (resetPending) { controller.reset(); resetPending = false }
                    val d = controller.decide(error, if (decided) 0.0 else dtSeconds)
                    decided = true
                    when (d.action) {
                        PlayoutAction.INSERT_SILENCE -> {
                            val n = minOf((frames - produced).toLong(), d.frames).toInt()
                            produced += n
                            stats.silenceFrames += n
                            fadeLeft = fadeFrames
                            if (n < d.frames) controller.reset() // more owed: jump the rest next time
                            continue
                        }
                        PlayoutAction.SKIP -> {
                            stats.skippedFrames += skip(d.frames)
                            fadeLeft = fadeFrames
                            continue
                        }
                        PlayoutAction.TRACK -> { ppm = d.ppm; stats.ppm = ppm }
                    }
                }
                produced += copyWithSlip(output, produced, frames - produced, ppm)
            }
        }
        return stats
    }

    private fun tryHead(): Boolean {
        while (true) {
            val h = head
            if (h != null && headPos < h.frames) return true
            if (h != null) { nextBlock(); continue }
            if (queue.isEmpty()) return false
            head = queue.removeFirst()
            headPos = 0
        }
    }

    private fun nextBlock() {
        val done = head!!
        val expectedUs = done.captureUs + done.frames * 1_000_000L / sampleRate
        head = null
        headPos = 0
        if (queue.isEmpty()) return
        val next = queue.removeFirst()
        head = next
        if (abs(next.captureUs - expectedUs) > GAP_US) resetPending = true
    }

    private fun skip(frames: Long): Long {
        var left = frames
        var skipped = 0L
        while (left > 0 && tryHead()) {
            val take = minOf((head!!.frames - headPos).toLong(), left).toInt()
            headPos += take
            left -= take
            skipped += take
        }
        return skipped
    }

    private fun copyWithSlip(output: FloatArray, at: Int, frames: Int, ppm: Double): Int {
        val block = head!!
        val pcm = block.pcm
        var made = 0
        while (made < frames && headPos < block.frames) {
            slip += ppm / 1_000_000
            if (slip >= 1) { slip -= 1; headPos++; if (headPos >= block.frames) break } // drop one: faster
            var gain = 1f
            if (fadeLeft > 0) { gain = 1f - fadeLeft.toFloat() / fadeFrames; fadeLeft-- }
            output[(at + made) * 2] = pcm[headPos * 2] / 32768f * gain
            output[(at + made) * 2 + 1] = pcm[headPos * 2 + 1] / 32768f * gain
            made++
            if (slip <= -1) { slip += 1; continue } // repeat one: slower
            headPos++
        }
        return made
    }

    companion object {
        const val GAP_US = 2_000L
        const val FADE_MS = 5
    }
}

/**
 * When the frames an [android.media.AudioTrack] is given will actually leave it, from its
 * timestamps (frame N left at time T) — but steadied. Android's timestamps jump when the mixer
 * is disturbed (another app's notification starting on the same output, a route change, the
 * CPU busy opening an app), and taking each one at its word made the playout "correct" a
 * phantom 30–80 ms error with a jump: the scratch heard when something else made a sound.
 *
 * So small differences are averaged in slowly (they are real drift, or jitter); a big one is
 * believed only after it has held for [CONFIRM] reports in a row, which a real change (a
 * Bluetooth headset reconnecting) does and a disturbance doesn't.
 */
class TrackClock(private val sampleRate: Int) {
    private var anchorFrame = 0L
    private var anchorUs = 0.0
    private var started = false
    private var outliers = 0
    private var outlierErrorUs = 0.0

    val ready: Boolean get() = started

    /** Feed a timestamp; true when it moved the clock by a jump (the caller re-aligns). */
    fun update(framePosition: Long, timeUs: Long): Boolean {
        if (!started) {
            anchorFrame = framePosition; anchorUs = timeUs.toDouble(); started = true
            return true
        }
        val error = timeUs - leaveUs(framePosition)
        if (abs(error) <= JUMP_US) {
            outliers = 0
            // Re-anchor at the new frame so the line keeps following the track's real rate.
            anchorUs = leaveUs(framePosition) + error * SMOOTHING
            anchorFrame = framePosition
            return false
        }
        if (outliers > 0 && abs(error - outlierErrorUs) > JUMP_US) outliers = 0 // not the same jump
        outlierErrorUs = error
        if (++outliers < CONFIRM) return false
        outliers = 0
        anchorFrame = framePosition
        anchorUs = timeUs.toDouble()
        return true
    }

    /** When frame [frame] leaves the track, µs on the timestamps' clock. */
    fun leaveUs(frame: Long): Double = anchorUs + (frame - anchorFrame) * 1_000_000.0 / sampleRate

    fun reset() { started = false; outliers = 0 }

    companion object {
        const val JUMP_US = 15_000.0
        const val CONFIRM = 8
        private const val SMOOTHING = 0.05
    }
}

/**
 * How long the mirror's picture takes from the PC's screen to this one (v27, D-097), so PC
 * Remote's sound can be played to match the picture rather than Play everywhere's group delay.
 * A rolling median: one late frame doesn't move it; a slower link does, within a few seconds.
 */
class VideoLatency {
    private val samples = ArrayDeque<Long>()
    private var stable = 0L

    /** One frame shown: [shownPcUs] on the PC clock, [capturedPcUs] its capture time. */
    @Synchronized
    fun add(shownPcUs: Long, capturedPcUs: Long) {
        val v = shownPcUs - capturedPcUs
        if (v !in 0..MAX_US) return
        samples.addLast(v)
        while (samples.size > WINDOW) samples.removeFirst()
        if (samples.size < MIN_SAMPLES) return
        val median = samples.sorted()[samples.size / 2]
        // Only move the target for a real change: every move re-aligns the sound.
        if (stable == 0L || abs(median - stable) > STEP_US) stable = median
    }

    /** The picture's delay, µs; 0 until known. */
    @get:Synchronized val us: Long get() = stable

    @Synchronized fun reset() { samples.clear(); stable = 0 }

    companion object {
        const val WINDOW = 60
        const val MIN_SAMPLES = 15
        const val STEP_US = 20_000L
        const val MAX_US = 2_000_000L
    }
}

enum class TestSoundKind { CHIRP, CLICK }

/** A sound scheduled on one speaker: heard at [atUs] on the PC clock. */
data class TestSound(val deviceId: String, val atUs: Long, val kind: TestSoundKind)

/** The alignment's sounds, the same as the desktop's TestSignal (a 60 ms 1.5→6 kHz chirp; a 4 ms 3 kHz click). */
object TestSignal {
    const val CHIRP_MS = 60
    private const val FROM_HZ = 1_500.0
    private const val TO_HZ = 6_000.0
    const val CLICK_MS = 4
    private const val CLICK_HZ = 3_000.0

    fun chirp(sampleRate: Int): FloatArray {
        val n = sampleRate * CHIRP_MS / 1000
        val duration = n / sampleRate.toDouble()
        val k = (TO_HZ - FROM_HZ) / duration
        val ramp = sampleRate * 5 / 1000
        return FloatArray(n) { i ->
            val t = i / sampleRate.toDouble()
            val edge = when {
                i < ramp -> 0.5 - 0.5 * cos(PI * i / ramp)
                i >= n - ramp -> 0.5 - 0.5 * cos(PI * (n - 1 - i) / ramp)
                else -> 1.0
            }
            (0.5 * sin(2 * PI * (FROM_HZ * t + k * t * t / 2)) * edge).toFloat()
        }
    }

    fun click(sampleRate: Int): FloatArray {
        val n = sampleRate * CLICK_MS / 1000
        return FloatArray(n) { i ->
            val hann = 0.5 - 0.5 * cos(2 * PI * i / (n - 1))
            (0.6 * sin(2 * PI * CLICK_HZ * i / sampleRate) * hann).toFloat()
        }
    }

    /** Mix [sounds] into [output] (interleaved stereo): output frame i is heard at [firstHeardUs] + i/rate. */
    fun mix(output: FloatArray, frames: Int, sampleRate: Int, firstHeardUs: Long, sounds: List<TestSound>, chirp: FloatArray, click: FloatArray) {
        for (sound in sounds) {
            val signal = if (sound.kind == TestSoundKind.CHIRP) chirp else click
            val start = ((firstHeardUs - sound.atUs) * sampleRate.toDouble() / 1_000_000).roundToLong()
            if (start >= signal.size || start + frames <= 0) continue
            val from = maxOf(0L, -start).toInt()
            val to = minOf(frames.toLong(), signal.size - start).toInt()
            for (i in from until to) {
                val v = signal[(start + i).toInt()]
                output[i * 2] += v
                output[i * 2 + 1] += v
            }
        }
    }
}

/** Where a chirp starts in a recording, and how clearly it stood out. */
data class ChirpHit(val index: Int, val snr: Double)

/**
 * Finds the chirp in what the microphone heard, by cross-correlation through an FFT: the chirp
 * matches itself at exactly one lag, so the peak is sharp even under talk or music. The
 * strongest peak is the sound itself; reflections arrive later and weaker.
 */
object ChirpDetector {
    const val MIN_SNR = 6.0
    const val MIN_APART_MS = 3

    /** Search [from, to) in [recording] for where [template] starts. Null when nothing stands out. */
    fun find(recording: FloatArray, from: Int, to: Int, template: FloatArray, sampleRate: Int): ChirpHit? {
        val c = correlate(recording, from, to, template) ?: return null
        val best = c.peak(-1, 0)
        val snr = c.snr(best, sampleRate / 200)
        return if (snr >= MIN_SNR) ChirpHit(c.start + best, snr) else null
    }

    /**
     * The same chirp heard twice — once played by the phone's own output, once by a speaker
     * replaying it — as (the one nearer [nativeAt], the other). The second must stand out too
     * and be at least [MIN_APART_MS] from the first; closer than that, the two are already in
     * step, and the pair comes back with both at the same place.
     */
    fun findPair(recording: FloatArray, from: Int, to: Int, template: FloatArray, sampleRate: Int, nativeAt: Int): Pair<ChirpHit, ChirpHit>? {
        val c = correlate(recording, from, to, template) ?: return null
        val apart = sampleRate * MIN_APART_MS / 1000
        val first = c.peak(-1, 0)
        val firstSnr = c.snr(first, sampleRate / 200)
        if (firstSnr < MIN_SNR) return null
        val second = c.peak(first, apart)
        val secondSnr = c.snr(second, sampleRate / 200, exclude = first)
        val a = ChirpHit(c.start + first, firstSnr)
        if (second < 0 || secondSnr < MIN_SNR) return a to a
        val b = ChirpHit(c.start + second, secondSnr)
        return if (abs(a.index - nativeAt) <= abs(b.index - nativeAt)) a to b else b to a
    }

    private class Correlation(val start: Int, val values: DoubleArray, val lags: Int) {
        /** The strongest lag, skipping within [guard] of [skip] (none when skip < 0). */
        fun peak(skip: Int, guard: Int): Int {
            var best = -1
            var peak = 0.0
            for (k in 0 until lags) {
                if (skip >= 0 && abs(k - skip) <= guard) continue
                val v = abs(values[k])
                if (v > peak) { peak = v; best = k }
            }
            return best
        }

        /** Peak over the RMS of everything away from it (and from [exclude]). */
        fun snr(at: Int, guard: Int, exclude: Int = -1): Double {
            if (at < 0) return 0.0
            var sum = 0.0
            var count = 0
            for (k in 0 until lags) {
                if (abs(k - at) <= guard || (exclude >= 0 && abs(k - exclude) <= guard)) continue
                sum += values[k] * values[k]; count++
            }
            val rms = if (count > 0) sqrt(sum / count) else 0.0
            return if (rms > 0) abs(values[at]) / rms else 0.0
        }
    }

    private fun correlate(recording: FloatArray, from: Int, to: Int, template: FloatArray): Correlation? {
        val start = from.coerceAtLeast(0)
        val end = minOf(to + template.size, recording.size)
        if (end - start < template.size * 2) return null
        var n = 1
        while (n < end - start + template.size) n = n shl 1
        val xr = DoubleArray(n); val xi = DoubleArray(n)
        val tr = DoubleArray(n); val ti = DoubleArray(n)
        for (i in start until end) xr[i - start] = recording[i].toDouble()
        for (i in template.indices) tr[i] = template[i].toDouble()
        fft(xr, xi, false); fft(tr, ti, false)
        // X · conj(T): the correlation of the recording against the template.
        for (k in 0 until n) {
            val re = xr[k] * tr[k] + xi[k] * ti[k]
            val im = xi[k] * tr[k] - xr[k] * ti[k]
            xr[k] = re; xi[k] = im
        }
        fft(xr, xi, true)
        val lags = minOf(to, recording.size - template.size) - start
        if (lags <= 0) return null
        return Correlation(start, xr, lags)
    }

    /** In-place iterative radix-2 FFT; [inverse] also divides by n. */
    fun fft(re: DoubleArray, im: DoubleArray, inverse: Boolean) {
        val n = re.size
        var j = 0
        for (i in 1 until n) {
            var bit = n shr 1
            while (j and bit != 0) { j = j xor bit; bit = bit shr 1 }
            j = j xor bit
            if (i < j) {
                var t = re[i]; re[i] = re[j]; re[j] = t
                t = im[i]; im[i] = im[j]; im[j] = t
            }
        }
        var len = 2
        while (len <= n) {
            val ang = 2 * PI / len * (if (inverse) 1 else -1)
            val wr = cos(ang); val wi = sin(ang)
            var i = 0
            while (i < n) {
                var cr = 1.0; var ci = 0.0
                for (k in 0 until len / 2) {
                    val ar = re[i + k]; val ai = im[i + k]
                    val br = re[i + k + len / 2] * cr - im[i + k + len / 2] * ci
                    val bi = re[i + k + len / 2] * ci + im[i + k + len / 2] * cr
                    re[i + k] = ar + br; im[i + k] = ai + bi
                    re[i + k + len / 2] = ar - br; im[i + k + len / 2] = ai - bi
                    val nr = cr * wr - ci * wi
                    ci = cr * wi + ci * wr
                    cr = nr
                }
                i += len
            }
            len = len shl 1
        }
        if (inverse) for (i in 0 until n) { re[i] /= n; im[i] /= n }
    }
}

/** One device's verdict from its rounds of chirps (the same rule as the desktop's Calibration). */
data class DeviceCalibration(val deviceId: String, val ok: Boolean, val errorUs: Long, val snr: Double)

object CalibrationMath {
    const val AGREE_US = 4_000L

    /**
     * The median of the rounds' errors, believed only when at least two rounds agree with it to
     * within [AGREE_US] — one clean hearing out of three could be a door slamming at the right moment.
     */
    fun combine(deviceId: String, errorsUs: List<Long>, snrs: List<Double>): DeviceCalibration {
        if (errorsUs.isEmpty()) return DeviceCalibration(deviceId, false, 0, 0.0)
        val sorted = errorsUs.sorted()
        val median = sorted[sorted.size / 2]
        val agreeing = errorsUs.count { abs(it - median) <= AGREE_US }
        val snr = if (snrs.isEmpty()) 0.0 else snrs.sorted()[snrs.size / 2]
        return DeviceCalibration(deviceId, agreeing >= 2, median, snr)
    }
}

/**
 * When the phone is the source (D-097), its own output — usually a Bluetooth device, the
 * slowest in the room — plays apps' sound on its own; Linc can't delay it, so it sets the time
 * everything else aims for. These are where that time starts before the microphone has measured
 * it: what a phone's speaker or a typical A2DP device adds after the mixer.
 */
object NativeRoute {
    fun baseUs(type: String): Long = when (type) {
        "bt" -> 180_000
        "ble" -> 100_000
        "wired" -> 25_000
        "usb" -> 40_000
        else -> 40_000 // the phone's speaker
    }
}

