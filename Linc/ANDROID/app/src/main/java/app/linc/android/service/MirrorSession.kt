package app.linc.android.service

import android.graphics.Bitmap
import android.os.SystemClock
import java.util.Base64
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.longOrNull

/** Where the PC's pointer is, in pixels of the mirrored display (v20 `pc.mirror.cursor`). */
data class PcCursor(val x: Int, val y: Int, val visible: Boolean, val shapeId: Long)

/** A decoded cursor image and its hotspot, in the PC's pixels. */
class CursorImage(val bitmap: Bitmap, val hotX: Int, val hotY: Int)

/** The link as the PC's sender sees it, plus the phone's own round trip (v20 `pc.mirror.stats`). */
data class LinkStats(
    val targetKbps: Int,
    val sentKbps: Int,
    val fps: Double,
    val delayMs: Int,
    val skipPct: Int,
    val dropped: Long,
) {
    /** A Windows-App-style three-step verdict for the connection bar. */
    fun quality(rttMs: Int): Quality = when {
        delayMs > 250 || skipPct > 40 || rttMs > 250 || fps < 8 -> Quality.POOR
        delayMs > 100 || skipPct > 15 || rttMs > 90 || fps < 18 -> Quality.FAIR
        else -> Quality.GOOD
    }

    enum class Quality(val label: String) { GOOD("Good"), FAIR("Fair"), POOR("Poor") }
}

data class PcDisplayInfo(val id: Int, val name: String, val width: Int, val height: Int, val primary: Boolean)

/**
 * The v20 side-channels of PC Remote (D-067): the PC's pointer, link health, round-trip time
 * and the display list. Fed by [SocketServer] from control-channel messages; watched by
 * MirrorActivity. Kept apart from [MirrorReceiver], which owns only the video decode.
 */
object MirrorSession {

    private val _cursor = MutableStateFlow<PcCursor?>(null)
    val cursor: StateFlow<PcCursor?> = _cursor

    private val _stats = MutableStateFlow<LinkStats?>(null)
    val stats: StateFlow<LinkStats?> = _stats

    /** Last measured control-channel round trip in ms, or -1 before the first pong. */
    private val _rttMs = MutableStateFlow(-1)
    val rttMs: StateFlow<Int> = _rttMs

    private val _displays = MutableStateFlow<List<PcDisplayInfo>>(emptyList())
    val displays: StateFlow<List<PcDisplayInfo>> = _displays

    /** Cursor images by the PC's handle id; the PC sends each shape once per session. */
    private val shapes = ConcurrentHashMap<Long, CursorImage>()

    fun shape(id: Long): CursorImage? = shapes[id]

    fun onCursor(payload: JsonObject) {
        val x = payload.int("x") ?: return
        val y = payload.int("y") ?: return
        val visible = (payload["visible"] as? JsonPrimitive)?.booleanOrNull ?: true
        val id = (payload["id"] as? JsonPrimitive)?.longOrNull ?: 0L
        (payload["shape"] as? JsonObject)?.let { shape ->
            runCatching { decodeShape(shape) }.getOrNull()?.let { shapes[id] = it }
        }
        _cursor.value = PcCursor(x, y, visible, id)
    }

    fun onStats(payload: JsonObject) {
        _stats.value = LinkStats(
            targetKbps = payload.int("targetKbps") ?: 0,
            sentKbps = payload.int("sentKbps") ?: 0,
            fps = (payload["fps"] as? JsonPrimitive)?.doubleOrNull ?: 0.0,
            delayMs = payload.int("delayMs") ?: 0,
            skipPct = payload.int("skipPct") ?: 0,
            dropped = (payload["dropped"] as? JsonPrimitive)?.longOrNull ?: 0L,
        )
    }

    /** The PC echoed a ping; `t` is the phone's own clock from when it was sent. */
    fun onPong(payload: JsonObject) {
        val sent = (payload["t"] as? JsonPrimitive)?.longOrNull ?: return
        val rtt = SystemClock.elapsedRealtime() - sent
        if (rtt in 0..10_000) {
            // Smooth lightly so one Wi-Fi hiccup does not flip the verdict.
            val previous = _rttMs.value
            _rttMs.value = if (previous < 0) rtt.toInt() else ((previous * 2 + rtt) / 3).toInt()
        }
    }

    fun onDisplays(payload: JsonObject) {
        val list = (payload["displays"] as? JsonArray) ?: return
        _displays.value = list.mapNotNull { element ->
            val d = runCatching { element.jsonObject }.getOrNull() ?: return@mapNotNull null
            PcDisplayInfo(
                id = d.int("id") ?: return@mapNotNull null,
                name = (d["name"] as? JsonPrimitive)?.contentOrNull ?: "Display",
                width = d.int("width") ?: 0,
                height = d.int("height") ?: 0,
                primary = (d["primary"] as? JsonPrimitive)?.booleanOrNull ?: false,
            )
        }
    }

    /** A new mirror session: forget the old pointer and stats (shapes stay valid per PC). */
    fun reset() {
        _cursor.value = null
        _stats.value = null
    }

    private fun decodeShape(shape: JsonObject): CursorImage? {
        val w = shape.int("w") ?: return null
        val h = shape.int("h") ?: return null
        val bytes = Base64.getDecoder().decode((shape["bgra"] as? JsonPrimitive)?.contentOrNull ?: return null)
        val pixels = bgraToArgb(bytes, w, h) ?: return null
        val bitmap = Bitmap.createBitmap(pixels, w, h, Bitmap.Config.ARGB_8888)
        return CursorImage(bitmap, shape.int("hx") ?: 0, shape.int("hy") ?: 0)
    }

    /**
     * The PC sends each pixel as little-endian 32-bit B,G,R,A — which read back as a
     * little-endian int is exactly Android's 0xAARRGGBB colour int. Null if the size is wrong.
     */
    fun bgraToArgb(bytes: ByteArray, width: Int, height: Int): IntArray? {
        if (width <= 0 || height <= 0 || bytes.size != width * height * 4) return null
        val out = IntArray(width * height)
        for (i in out.indices) {
            val o = i * 4
            out[i] = (bytes[o].toInt() and 0xFF) or
                ((bytes[o + 1].toInt() and 0xFF) shl 8) or
                ((bytes[o + 2].toInt() and 0xFF) shl 16) or
                ((bytes[o + 3].toInt() and 0xFF) shl 24)
        }
        return out
    }

    private fun JsonObject.int(key: String): Int? = (this[key] as? JsonPrimitive)?.intOrNull
}
