package app.linc.android.service

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.math.abs
import kotlin.math.roundToLong
import kotlin.random.Random

/**
 * "Play everywhere" (v26, D-096): the phone's half of the sync core. The controller numbers are
 * the same ones audiosyncsim checks on the desktop, so the two playouts can't drift apart.
 */
class AudioSyncTest {

    // ---- controller -------------------------------------------------------------------------

    @Test
    fun `controller jumps when unlocked or far out, slews when close`() {
        val c = PlayoutController(48_000)
        val first = c.decide(-25_000.0, 0.01)
        assertEquals(PlayoutAction.INSERT_SILENCE, first.action)
        assertEquals(1200L, first.frames)
        val close = c.decide(3_000.0, 0.01)
        assertEquals(PlayoutAction.TRACK, close.action)
        assertTrue(close.ppm > 0)
        val late = c.decide(55_000.0, 0.01)
        assertEquals(PlayoutAction.SKIP, late.action)
        assertEquals(2640L, late.frames)
        repeat(50) { c.decide(-30_000.0, 0.01) }
        assertTrue(abs(c.decide(-30_000.0, 0.01).ppm) <= PlayoutController.MAX_PPM)
    }

    // ---- the engine, against a simulated phone speaker ------------------------------------------

    /** A track that plays at its own drifting rate, holding [bufferUs], hiding [hiddenUs] from its clock. */
    private class SimTrack(val driftPpm: Double, val bufferUs: Long, val hiddenUs: Long, var assumedHiddenUs: Long = hiddenUs) {
        val rate = 48_000
        val trueRate = rate * (1 + driftPpm / 1_000_000)
        val engine = PlayoutEngine(rate)
        var written = 0L
        var lastFrame = Double.NaN
        val errors = ArrayList<Pair<Long, Double>>()

        fun tick(nowUs: Long, delayUs: Long) {
            val played = nowUs * trueRate / 1_000_000
            val target = (played + bufferUs * rate / 1_000_000.0).toLong()
            val frames = maxOf(0L, target - written).toInt()
            if (frames == 0) return
            val firstHeard = nowUs + ((written - played) * 1_000_000 / rate).toLong() + assumedHiddenUs
            val out = FloatArray(frames * 2)
            engine.render(out, frames, firstHeard, delayUs, 0.01)
            for (i in 0 until frames) {
                val v = out[i * 2]
                if (v == 0f) { lastFrame = Double.NaN; continue }
                val code = v * 32768.0 + 15_000
                val heardUs = (written + i) * 1_000_000 / trueRate + hiddenUs
                val expected = (heardUs - delayUs) * 48_000 / 1_000_000
                val frame = code + Math.round((expected - code) / 30_000) * 30_000.0
                val steady = !lastFrame.isNaN() && frame - lastFrame > 0 && frame - lastFrame < 4
                lastFrame = frame
                if (steady) errors += heardUs.toLong() to (heardUs - (frame * 1_000_000 / 48_000 + delayUs))
            }
            written += frames
        }
    }

    private fun encode(frame: Long): Short = (frame % 30_000 - 15_000).toShort()

    private fun run(tracks: List<SimTrack>, untilUs: Long, silence: LongRange? = null, delayAt: (Long) -> Long = { 250_000 }) {
        var sourceFrame = 0L
        var nextBlock = 0L
        val pending = ArrayDeque<Pair<Long, SoundBlock>>()
        var t = 0L
        while (t <= untilUs) {
            if (t >= nextBlock) {
                if (silence == null || t !in silence) {
                    val pcm = ShortArray(2048)
                    for (i in 0 until 1024) { val v = encode(sourceFrame + i); pcm[i * 2] = v; pcm[i * 2 + 1] = v }
                    pending.addLast(t + 40_000 to SoundBlock(pcm, nextBlock)) // Wi-Fi: 40 ms to arrive
                }
                sourceFrame += 1024
                nextBlock += 1_000_000L * 1024 / 48_000
            }
            while (pending.isNotEmpty() && pending.first().first <= t) {
                val b = pending.removeFirst().second
                tracks.forEach { it.engine.enqueue(b, 2_000_000) }
            }
            if (t % 10_000 == 0L) tracks.forEach { it.tick(t, delayAt(t)) }
            t += 1_000
        }
    }

    @Test
    fun `phone speaker and Bluetooth play the same sample within 1 ms`() {
        val speaker = SimTrack(driftPpm = +80.0, bufferUs = 60_000, hiddenUs = 15_000)
        val bt = SimTrack(driftPpm = -110.0, bufferUs = 60_000, hiddenUs = 160_000)
        // The Bluetooth output needs 40 + 60 + 160 = 260 ms, so the group delay is above that,
        // as the PC would set it.
        run(listOf(speaker, bt), 16_000_000, silence = 7_000_000L until 8_100_000L) { if (it < 12_000_000) 300_000 else 350_000 }
        for (track in listOf(speaker, bt)) {
            val settled = track.errors.filter { (h, _) -> h in 2_000_000 until 7_000_000 || h in 8_500_000 until 12_000_000 || h >= 13_000_000 }
            assertTrue("enough samples", settled.size > 1000)
            val worst = settled.maxOf { abs(it.second) }
            assertTrue("worst ${worst / 1000} ms", worst < 1_000)
        }
    }

    @Test
    fun `a speaker that hides 160 ms is heard 160 ms late until told`() {
        val bt = SimTrack(driftPpm = 0.0, bufferUs = 60_000, hiddenUs = 160_000, assumedHiddenUs = 0)
        run(listOf(bt), 4_000_000)
        val mean = bt.errors.filter { it.first > 2_000_000 }.map { it.second }.average()
        assertEquals(160_000.0, mean, 1_000.0)
    }

    // ---- clock ------------------------------------------------------------------------------

    @Test
    fun `clock estimator finds offset and drift through jittery, lopsided round trips`() {
        val clock = ClockEstimator()
        val rnd = Random(7)
        val offset = 123_456_789L
        val drift = 60e-6
        fun pcAt(phoneUs: Long) = (phoneUs * (1 + drift)).roundToLong() + offset
        var phone = 1_000_000_000L
        repeat(80) {
            // Out and back take 2–4 ms on a good link, sometimes 60 ms when Wi-Fi queues one way.
            val out = 2_000L + rnd.nextLong(2_000) + if (rnd.nextInt(4) == 0) 60_000L else 0L
            val back = 2_000L + rnd.nextLong(2_000) + if (rnd.nextInt(5) == 0) 40_000L else 0L
            val t0 = phone
            val t1 = pcAt(t0 + out)
            val t2 = t1 + 300
            val t3 = t0 + out + 300 + back
            clock.add(t0, t1, t2, t3)
            phone += 500_000
        }
        assertTrue(clock.ready)
        val probe = phone + 1_000_000
        assertEquals(pcAt(probe).toDouble(), clock.toPc(probe).toDouble(), 2_000.0)
        assertEquals(probe.toDouble(), clock.toPhone(clock.toPc(probe)).toDouble(), 2.0)
        assertEquals(60.0, clock.driftPpm, 25.0)
    }

    // ---- microphone timing ------------------------------------------------------------------

    @Test
    fun `chirp is found to the sample under noise and an echo`() {
        val rate = 48_000
        val chirp = TestSignal.chirp(rate)
        val rnd = Random(3)
        val rec = FloatArray(rate) { (rnd.nextDouble(-1.0, 1.0) * 0.08).toFloat() }
        val at = 20_017
        for (i in chirp.indices) {
            rec[at + i] += chirp[i] * 0.15f
            if (at + 700 + i < rec.size) rec[at + 700 + i] += chirp[i] * 0.07f // a wall 2.5 m away
        }
        val hit = ChirpDetector.find(rec, 5_000, 40_000, chirp, rate)
        assertNotNull(hit)
        assertEquals(at, hit!!.index)
        assertTrue(hit.snr >= ChirpDetector.MIN_SNR)
    }

    @Test
    fun `silence has no chirp in it`() {
        val rnd = Random(5)
        val rec = FloatArray(48_000) { (rnd.nextDouble(-1.0, 1.0) * 0.05).toFloat() }
        assertNull(ChirpDetector.find(rec, 0, 40_000, TestSignal.chirp(48_000), 48_000))
    }

    @Test
    fun `test sounds are mixed where they are due`() {
        val out = FloatArray(4800 * 2)
        TestSignal.mix(out, 4800, 48_000, 1_000_000, listOf(TestSound("x", 1_050_000, TestSoundKind.CHIRP)),
            TestSignal.chirp(48_000), TestSignal.click(48_000))
        val first = out.indexOfFirst { it != 0f } / 2
        assertTrue("got $first", first in 2400..2402)
    }

    @Test
    fun `calibration needs two agreeing rounds`() {
        val ok = CalibrationMath.combine("bt", listOf(41_000, 43_500, 140_000), listOf(12.0, 15.0, 7.0))
        assertTrue(ok.ok)
        assertEquals(43_500L, ok.errorUs)
        val unclear = CalibrationMath.combine("bt", listOf(10_000, 60_000, 140_000), listOf(7.0, 7.0, 7.0))
        assertFalse(unclear.ok)
    }

    // ---- track timestamps -------------------------------------------------------------------

    @Test
    fun `track clock ignores a disturbance but follows a real change`() {
        val c = TrackClock(48_000)
        val rnd = Random(11)
        var frame = 0L
        fun time(f: Long, shift: Long = 0) = 1_000_000L + f * 1_000_000 / 48_000 + shift + rnd.nextLong(-500, 500)
        c.update(frame, time(frame))
        repeat(100) { frame += 480; assertFalse(c.update(frame, time(frame))) }
        // Another app's sound starts: a few reports 60 ms off, then back.
        repeat(3) { frame += 480; assertFalse(c.update(frame, time(frame, 60_000))) }
        repeat(20) { frame += 480; assertFalse(c.update(frame, time(frame))) }
        assertEquals(time(frame).toDouble(), c.leaveUs(frame), 1_500.0)
        // A Bluetooth headset reconnects: 120 ms later from now on.
        var jumped = false
        repeat(TrackClock.CONFIRM + 2) { frame += 480; jumped = jumped or c.update(frame, time(frame, 120_000)) }
        assertTrue(jumped)
        assertEquals(time(frame, 120_000).toDouble(), c.leaveUs(frame), 1_500.0)
    }

    // ---- lip-sync ---------------------------------------------------------------------------

    @Test
    fun `video latency is a steady median that only moves for a real change`() {
        val v = VideoLatency()
        val rnd = Random(2)
        repeat(40) { v.add(1_000_000 + 90_000 + rnd.nextLong(-8_000, 8_000), 1_000_000) }
        assertEquals(90_000.0, v.us.toDouble(), 8_000.0)
        val first = v.us
        repeat(10) { v.add(1_000_000 + 400_000, 1_000_000) } // a burst of late frames
        assertEquals(first, v.us)
        repeat(60) { v.add(1_000_000 + 150_000, 1_000_000) } // the link really got slower
        assertEquals(150_000L, v.us)
    }
}
