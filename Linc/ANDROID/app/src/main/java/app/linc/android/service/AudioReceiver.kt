package app.linc.android.service

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.media.MediaCodec
import android.media.MediaFormat
import java.io.DataInputStream
import java.nio.ByteBuffer
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/**
 * Plays the PC's sound while PC Remote is open (v20, pc-audio channel 6, D-067). The desktop
 * sends raw AAC-LC frames framed exactly like pc-video; this decodes them with MediaCodec and
 * plays them on a low-latency [AudioTrack].
 *
 * **Late sound is dropped, not queued.** The track is written non-blocking: if it is already
 * holding its ~150 ms of sound (a Wi-Fi burst delivered a clump), the overflow is discarded
 * rather than stalling the reader, which would back the whole channel up and leave the sound
 * seconds behind the picture. AAC frames decode independently, so a discarded frame is a
 * 21 ms blip and nothing after it is affected.
 */
object AudioReceiver {

    private val _playing = MutableStateFlow(false)
    val playing: StateFlow<Boolean> = _playing

    fun receive(input: DataInputStream, sampleRate: Int, channels: Int) {
        val format = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_AAC, sampleRate, channels).apply {
            setByteBuffer("csd-0", ByteBuffer.wrap(audioSpecificConfig(sampleRate, channels)))
            setInteger(MediaFormat.KEY_IS_ADTS, 0)
        }
        val codec = runCatching {
            MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_AUDIO_AAC).apply {
                configure(format, null, null, 0)
                start()
            }
        }.getOrElse {
            LogStore.log(LogLevel.WARN, "PC audio: couldn't start the decoder: ${it.message}")
            return
        }

        val channelMask = if (channels == 1) AudioFormat.CHANNEL_OUT_MONO else AudioFormat.CHANNEL_OUT_STEREO
        val minBuffer = AudioTrack.getMinBufferSize(sampleRate, channelMask, AudioFormat.ENCODING_PCM_16BIT)
        val track = AudioTrack.Builder()
            .setAudioAttributes(AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_MEDIA)
                .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                .build())
            .setAudioFormat(AudioFormat.Builder()
                .setSampleRate(sampleRate)
                .setChannelMask(channelMask)
                .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                .build())
            .setBufferSizeInBytes(maxOf(minBuffer, sampleRate * channels * 2 * TRACK_BUFFER_MS / 1000))
            .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
            .setTransferMode(AudioTrack.MODE_STREAM)
            .build()
        track.play()
        _playing.value = true
        LogStore.log(LogLevel.INFO, "PC audio: playing the PC's sound (${sampleRate} Hz)")

        val info = MediaCodec.BufferInfo()
        try {
            while (true) {
                val frame = VideoFrame.read(input) ?: break
                val index = codec.dequeueInputBuffer(FEED_TIMEOUT_US)
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
                        track.write(pcm, info.size, AudioTrack.WRITE_NON_BLOCKING)
                    }
                    codec.releaseOutputBuffer(out, false)
                }
            }
        } catch (_: Exception) {
            // Socket closed or the decoder faulted: either way the sound has ended.
        } finally {
            runCatching { track.stop() }
            runCatching { track.release() }
            runCatching { codec.stop() }
            runCatching { codec.release() }
            _playing.value = false
            LogStore.log(LogLevel.INFO, "PC audio: stopped")
        }
    }

    /** The 2-byte AAC-LC AudioSpecificConfig the decoder needs for a raw (non-ADTS) stream. */
    fun audioSpecificConfig(sampleRate: Int, channels: Int): ByteArray {
        val index = when (sampleRate) {
            96000 -> 0; 88200 -> 1; 64000 -> 2; 48000 -> 3; 44100 -> 4; 32000 -> 5
            24000 -> 6; 22050 -> 7; 16000 -> 8; 12000 -> 9; 11025 -> 10; 8000 -> 11
            else -> 3
        }
        val objectType = 2 // AAC-LC
        return byteArrayOf(
            ((objectType shl 3) or (index shr 1)).toByte(),
            (((index and 1) shl 7) or (channels shl 3)).toByte(),
        )
    }

    private const val FEED_TIMEOUT_US = 20_000L
    private const val TRACK_BUFFER_MS = 150
}
