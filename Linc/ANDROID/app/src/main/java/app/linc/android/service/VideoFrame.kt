package app.linc.android.service

import java.io.DataInputStream
import java.io.EOFException

/**
 * One packet on the pc-video channel (5) — protocol v14, M05. This is the read side of the
 * desktop's `VideoFraming`, and the two MUST agree byte-for-byte (mirrorsim asserts the write
 * side against exactly this layout).
 *
 * 12-byte big-endian header: an 8-byte word whose top two bits are flags (bit 63 = codec
 * config, bit 62 = keyframe) and whose low 62 bits are the presentation timestamp in
 * microseconds, followed by a 4-byte payload length; then the Annex-B H.264 payload.
 */
class VideoFrame(
    val timestampUs: Long,
    val config: Boolean,
    val keyframe: Boolean,
    val data: ByteArray,
) {
    companion object {
        private const val CONFIG_FLAG = 1L shl 63
        private const val KEYFRAME_FLAG = 1L shl 62
        private const val TIMESTAMP_MASK = (CONFIG_FLAG or KEYFRAME_FLAG).inv()
        private const val MAX_PACKET = 8 * 1024 * 1024

        /** Reads one packet, or returns null at a clean end of stream. */
        fun read(input: DataInputStream): VideoFrame? {
            val word = try {
                input.readLong()
            } catch (_: EOFException) {
                return null
            }
            val length = input.readInt()
            if (length < 0 || length > MAX_PACKET) {
                throw IllegalStateException("PC video packet length out of range: $length")
            }
            val data = ByteArray(length)
            input.readFully(data)
            return VideoFrame(
                timestampUs = word and TIMESTAMP_MASK,
                config = (word and CONFIG_FLAG) != 0L,
                keyframe = (word and KEYFRAME_FLAG) != 0L,
                data = data,
            )
        }
    }
}
