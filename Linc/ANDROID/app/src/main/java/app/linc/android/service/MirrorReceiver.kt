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

        val codec = runCatching { startCodec(liveSurface, width, height) }.getOrElse { error ->
            LogStore.log(LogLevel.WARN, "PC mirror: couldn't start the decoder: ${error.message}")
            drain(input)
            return
        }

        _state.value = MirrorState(streaming = true, width = width, height = height)
        LogStore.log(LogLevel.INFO, "PC mirror: showing the PC screen (${width}x$height)")

        val bufferInfo = MediaCodec.BufferInfo()
        try {
            while (true) {
                val frame = VideoFrame.read(input) ?: break
                feed(codec, frame)
                render(codec, bufferInfo)
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

    /** Queue one packet's bytes to the decoder. Blocks briefly for a free input buffer. */
    private fun feed(codec: MediaCodec, frame: VideoFrame) {
        val index = codec.dequeueInputBuffer(TIMEOUT_US)
        if (index < 0) return // no input buffer free right now; drop this packet, keep flowing
        val buffer = codec.getInputBuffer(index) ?: return
        buffer.clear()
        buffer.put(frame.data)
        val flags = if (frame.keyframe) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0
        codec.queueInputBuffer(index, 0, frame.data.size, frame.timestampUs, flags)
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

    private const val TIMEOUT_US = 10_000L
    private const val SURFACE_WAIT_MS = 4_000L
}
