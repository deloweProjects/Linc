package app.linc.android.protocol

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class FramingTest {

    private fun frame(json: String): ByteArray {
        val bytes = ByteArrayOutputStream()
        Framing.write(DataOutputStream(bytes), json)
        return bytes.toByteArray()
    }

    private fun input(bytes: ByteArray) = DataInputStream(ByteArrayInputStream(bytes))

    @Test
    fun `round trips a message`() {
        val json = """{"v":0,"type":"ping","id":"abc","replyTo":null,"payload":{}}"""
        assertEquals(json, Framing.read(input(frame(json))))
    }

    @Test
    fun `length prefix is 4-byte big-endian`() {
        val bytes = frame("{}")
        assertEquals(6, bytes.size)
        assertEquals(listOf<Byte>(0, 0, 0, 2), bytes.take(4))
    }

    @Test
    fun `reads consecutive frames`() {
        val data = frame("""{"a":1}""") + frame("""{"b":2}""")
        val stream = input(data)
        assertEquals("""{"a":1}""", Framing.read(stream))
        assertEquals("""{"b":2}""", Framing.read(stream))
        assertNull(Framing.read(stream))
    }

    @Test
    fun `returns null on clean end of stream`() {
        assertNull(Framing.read(input(ByteArray(0))))
    }

    @Test
    fun `rejects oversized incoming frame`() {
        val bytes = ByteArrayOutputStream()
        DataOutputStream(bytes).writeInt(Framing.MAX_FRAME_BYTES + 1)
        assertThrows(IOException::class.java) { Framing.read(input(bytes.toByteArray())) }
    }

    @Test
    fun `rejects non-positive frame length`() {
        val bytes = ByteArrayOutputStream()
        DataOutputStream(bytes).writeInt(-5)
        assertThrows(IOException::class.java) { Framing.read(input(bytes.toByteArray())) }
    }

    @Test
    fun `rejects oversized outgoing frame`() {
        val big = "x".repeat(Framing.MAX_FRAME_BYTES + 1)
        assertThrows(IOException::class.java) {
            Framing.write(DataOutputStream(ByteArrayOutputStream()), big)
        }
    }

    @Test
    fun `round trips a binary frame`() {
        val payload = byteArrayOf(0, 1, 2, 127, -1, -128)
        val bytes = ByteArrayOutputStream()
        Framing.writeBytes(DataOutputStream(bytes), payload)
        val read = Framing.readBytes(input(bytes.toByteArray()))
        assertTrue(payload.contentEquals(read))
    }

    @Test
    fun `binary frame allows zero length`() {
        val bytes = ByteArrayOutputStream()
        Framing.writeBytes(DataOutputStream(bytes), ByteArray(0))
        val read = Framing.readBytes(input(bytes.toByteArray()))
        assertEquals(0, read?.size)
    }
}

class EnvelopeTest {

    @Test
    fun `encodes all spec fields including defaults`() {
        val json = Envelope(type = MessageType.PING, id = "abc").toJson()
        val decoded = parseEnvelope(json)
        assertEquals(PROTOCOL_VERSION, decoded.v)
        assertEquals("ping", decoded.type)
        assertEquals("abc", decoded.id)
        assertNull(decoded.replyTo)
        assertTrue(decoded.payload.isEmpty())
        // Spec: replyTo is present (null) even for unsolicited messages.
        assertTrue(json.contains("\"replyTo\":null"))
    }

    @Test
    fun `round trips payload`() {
        val envelope = Envelope(
            type = MessageType.STATUS,
            replyTo = "req-1",
            payload = buildJsonObject {
                put("battery", 87)
                put("charging", true)
            },
        )
        val decoded = parseEnvelope(envelope.toJson())
        assertEquals(JsonPrimitive(87), decoded.payload["battery"])
        assertEquals(JsonPrimitive(true), decoded.payload["charging"])
        assertEquals("req-1", decoded.replyTo)
    }

    @Test
    fun `ignores unknown fields for forward compatibility`() {
        val decoded = parseEnvelope(
            """{"v":0,"type":"hello","id":"x","replyTo":null,"payload":{},"futureField":42}"""
        )
        assertEquals("hello", decoded.type)
    }
}
