package app.linc.android.protocol

import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.io.IOException

/**
 * Frame codec: 4-byte unsigned big-endian length prefix + UTF-8 JSON (docs/PROTOCOL.md).
 * DataOutputStream.writeInt is big-endian, matching the spec; lengths are capped at
 * 1 MiB, so the sign bit is never set and signed readInt is safe.
 */
object Framing {
    const val MAX_FRAME_BYTES = 1 * 1024 * 1024

    fun write(out: DataOutputStream, json: String) {
        val bytes = json.toByteArray(Charsets.UTF_8)
        if (bytes.size > MAX_FRAME_BYTES) {
            throw IOException("outgoing frame of ${bytes.size} bytes exceeds $MAX_FRAME_BYTES")
        }
        out.writeInt(bytes.size)
        out.write(bytes)
        out.flush()
    }

    /** Reads one frame, or returns null on clean end-of-stream. */
    fun read(input: DataInputStream): String? {
        val length = try {
            input.readInt()
        } catch (_: EOFException) {
            return null
        }
        if (length <= 0 || length > MAX_FRAME_BYTES) {
            throw IOException("invalid frame length: $length")
        }
        val buffer = ByteArray(length)
        input.readFully(buffer)
        return String(buffer, Charsets.UTF_8)
    }

    /**
     * Writes one raw binary frame (bulk channel, PROTOCOL.md v5). Same 4-byte
     * big-endian length prefix as [write]; a zero-length frame is legal and means
     * "not found / unavailable".
     */
    fun writeBytes(out: DataOutputStream, bytes: ByteArray) {
        if (bytes.size > MAX_FRAME_BYTES) {
            throw IOException("outgoing frame of ${bytes.size} bytes exceeds $MAX_FRAME_BYTES")
        }
        out.writeInt(bytes.size)
        out.write(bytes)
        out.flush()
    }

    /** Reads one raw binary frame, or returns null on clean end-of-stream. */
    fun readBytes(input: DataInputStream): ByteArray? {
        val length = try {
            input.readInt()
        } catch (_: EOFException) {
            return null
        }
        if (length < 0 || length > MAX_FRAME_BYTES) {
            throw IOException("invalid frame length: $length")
        }
        val buffer = ByteArray(length)
        input.readFully(buffer)
        return buffer
    }
}
