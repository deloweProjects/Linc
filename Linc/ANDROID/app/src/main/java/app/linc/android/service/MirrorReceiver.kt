package app.linc.android.service

import android.media.MediaCodec
import android.media.MediaFormat
import android.view.Surface
import java.io.DataInputStream
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/** What the reverse mirror is doing, for the Mirror screen to reflect. */
data class MirrorState(
    val streaming: Boolean = false,
    val width: Int = 0,
    val height: Int = 0,
)

/**
 * Receives the PC's screen over the pc-video channel (5) and decodes it to a Surface with
 * MediaCodec (M05, D-029). The desktop is the server on this channel: after the JSON config
 * frame, [receive] reads framed H.264 packets (12-byte header, [VideoFrame]) and feeds them
 * straight to the decoder.
 *
 * **The decode loop deliberately does NOT queue or clock-pace frames — that is D-023's lesson
 * made concrete.** Naive frame queueing corrupts P-frames (a decoder needs each frame in order,
 * as soon as it arrives, to build the next), and presenting on a wall clock accumulates latency
 * that never recovers. So every packet is queued to the decoder the instant it is read, and
 * every decoded frame is released to the Surface the instant it comes out
 * (`releaseOutputBuffer(index, true)`), with no timestamp gating. The network already paces the
 * stream at the source's frame rate; the phone's job is only to keep up.
 *
 * **A lossy link is handled, not ignored.** Over Wi-Fi packets are late and decoder input
 * buffers do run dry, and H.264 makes that expensive: one missing frame corrupts every frame
 * that references it. So a packet this decoder could not take is treated as lost — the phone
 * asks the PC for a fresh IDR (`pc.mirror.keyframe`, v19) and drops everything until one
 * arrives, rather than feeding the decoder frames it cannot resolve. A decoder that faults
 * outright is rebuilt in place, because a faulted MediaCodec never recovers on its own and
 * the mirror would otherwise stay black for the rest of the session.
 *
 * **The Surface is attached before streaming starts** (the Mirror screen sends
 * `pc.mirror.start` only after its SurfaceView is ready), so the decoder can be configured with
 * a real Surface from the first packet and the opening keyframe is never missed.
 */
object MirrorReceiver {

    private val _state = MutableStateFlow(MirrorState())
    val state: StateFlow<MirrorState> = _state

    /** True while a text field is focused on the PC (v14 `pc.textfocus`) — drives the soft keyboard. */
    val textFocus = MutableStateFlow(false)

    @Volatile private var surface: Surface? = null
    @Volatile private var surfaceReady = CountDownLatch(1)

    /** Called by the Mirror screen when its SurfaceView is ready / torn down. */
    fun attachSurface(newSurface: Surface) {
        surface = newSurface
        surfaceReady.countDown()
    }

    fun detachSurface() {
        surface = null
        surfaceReady = CountDownLatch(1)
    }

    /**
     * Serve the channel-5 stream. Runs on the dial-back thread and blocks until the stream
     * ends (the desktop stops, or the socket closes). [readConfig] has already consumed the
     * length-prefixed JSON config frame; here we only read video packets.
     */
    fun receive(input: DataInputStream, width: Int, height: Int) {
        val liveSurface = awaitSurface() ?: run {
            LogStore.log(LogLevel.WARN, "PC mirror: no display surface, ignoring stream")
            drain(input)
            return
        }

        val started = runCatching { startCodec(liveSurface, width, height) }
        val first = started.getOrNull()
        if (first == null) {
            LogStore.log(LogLevel.WARN,
                "PC mirror: couldn't start the decoder: ${started.exceptionOrNull()?.message}")
            drain(input)
            return
        }
        var codec: MediaCodec = first

        _state.value = MirrorState(streaming = true, width = width, height = height)
        LogStore.log(LogLevel.INFO, "PC mirror: showing the PC screen (${width}x$height)")

        // A fresh stream always opens on a keyframe, so nothing is being waited for yet.
        var awaitingKeyframe = false
        var lastKeyframeRequest = 0L
        val bufferInfo = MediaCodec.BufferInfo()
        try {
            while (true) {
                val frame = VideoFrame.read(input) ?: break

                // After a gap, every P-frame references pictures this decoder never saw.
                // Feeding them produces the green smear that used to be the visible symptom
                // of a weak Wi-Fi link, so they are discarded until the PC's next IDR.
                if (awaitingKeyframe && !frame.keyframe) {
                    lastKeyframeRequest = requestKeyframe(lastKeyframeRequest)
                    continue
                }
                awaitingKeyframe = false

                try {
                    if (!feed(codec, frame)) {
                        // The decoder could not take this frame in time — it is genuinely
                        // lost, so resynchronise rather than pretending the stream is intact.
                        awaitingKeyframe = true
                        lastKeyframeRequest = requestKeyframe(lastKeyframeRequest)
                        continue
                    }
                    render(codec, bufferInfo)
                } catch (error: MediaCodec.CodecException) {
                    // A decoder that faulted stays faulted; the only way back is a new one.
                    // Without this the mirror went black for the rest of the session.
                    LogStore.log(LogLevel.WARN, "PC mirror: decoder fault, restarting it (${error.message})")
                    runCatching { codec.stop() }
                    runCatching { codec.release() }
                    val live = surface ?: break
                    val restarted = runCatching { startCodec(live, width, height) }.getOrNull() ?: break
                    codec = restarted
                    awaitingKeyframe = true
                    lastKeyframeRequest = requestKeyframe(0L)
                }
            }
        } catch (_: Exception) {
            // Socket closed or decoder faulted — both are just "the mirror ended".
        } finally {
            runCatching { codec.stop() }
            runCatching { codec.release() }
            _state.value = MirrorState(streaming = false)
            LogStore.log(LogLevel.INFO, "PC mirror: stopped")
        }
    }

    /**
     * Ask the PC for an IDR, at most once every [KEYFRAME_REQUEST_GAP_MS]. Returns the time
     * of the last request. Rate-limiting matters: a bad second on the link can lose dozens of
     * frames, and one request per lost frame would answer a congested link with a burst of
     * the largest frames the encoder makes — the opposite of what it needs.
     */
    private fun requestKeyframe(lastRequestMs: Long): Long {
        val now = android.os.SystemClock.elapsedRealtime()
        if (now - lastRequestMs < KEYFRAME_REQUEST_GAP_MS) return lastRequestMs
        MirrorControl.keyframe()
        return now
    }

    private fun startCodec(surface: Surface, width: Int, height: Int): MediaCodec {
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
            // Low-latency decode where the device supports it (Android 11+). The stream carries
            // SPS/PPS in-band with every keyframe, so no csd-0/csd-1 is set here.
            setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
        }
        val codec = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        codec.configure(format, surface, null, 0)
        codec.start()
        return codec
    }

    /**
     * Queue one packet's bytes to the decoder. Returns false when no input buffer came free
     * in time, which means the packet is lost.
     *
     * **Waiting matters here.** This used to give up after a single non-blocking attempt and
     * drop the packet silently, which on a healthy link is rare but on a stuttering one
     * happens in runs — and a silently dropped P-frame corrupts everything that references
     * it, with nothing anywhere asking for a keyframe to recover. So it waits properly, and
     * a real failure is reported so the caller can resynchronise.
     */
    private fun feed(codec: MediaCodec, frame: VideoFrame): Boolean {
        val index = codec.dequeueInputBuffer(FEED_TIMEOUT_US)
        if (index < 0) return false
        val buffer = codec.getInputBuffer(index) ?: return false
        buffer.clear()
        buffer.put(frame.data)
        val flags = if (frame.keyframe) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0
        codec.queueInputBuffer(index, 0, frame.data.size, frame.timestampUs, flags)
        return true
    }

    /** Release every ready output frame straight to the Surface, no clock pacing (D-023). */
    private fun render(codec: MediaCodec, info: MediaCodec.BufferInfo) {
        while (true) {
            val index = codec.dequeueOutputBuffer(info, 0)
            if (index < 0) break
            codec.releaseOutputBuffer(index, true)
        }
    }

    private fun awaitSurface(): Surface? {
        if (!surfaceReady.await(SURFACE_WAIT_MS, TimeUnit.MILLISECONDS)) return null
        return surface
    }

    /** Consume and discard the stream when it cannot be shown, so the socket closes cleanly. */
    private fun drain(input: DataInputStream) {
        try { while (VideoFrame.read(input) != null) { /* discard */ } } catch (_: Exception) {}
    }

    /** How long to wait for a decoder input buffer before calling the packet lost. */
    private const val FEED_TIMEOUT_US = 100_000L
    private const val SURFACE_WAIT_MS = 4_000L
    private const val KEYFRAME_REQUEST_GAP_MS = 500L
}
