package app.linc.android.service

import android.app.WallpaperManager
import android.content.Context
import android.graphics.Bitmap
import android.os.Environment
import androidx.core.graphics.drawable.toBitmap
import java.io.ByteArrayOutputStream
import java.security.MessageDigest

/**
 * The phone's wallpaper, for the desktop's Home background (PROTOCOL.md v8, D-021).
 * Android 13+ only allows the read with the All-files-access grant
 * (`MANAGE_EXTERNAL_STORAGE`), which the Settings screen lets the user give on sideload
 * builds; without it everything here returns null and the desktop keeps its
 * palette-gradient fallback.
 *
 * M15b Part E: this used to send a 48-px-wide PNG. There was never a blur filter —
 * *downsampling to 48 px was* the blur, which is why the desktop's Home background looked
 * like frosted glass. It is now sent sharp: the longest edge is bounded at
 * [MAX_DIMENSION_PX] and the image is JPEG rather than PNG.
 *
 * Two deliberate choices, both about the wire this shares with mirroring:
 *  - **Bound the LONGEST edge, not the width.** A phone wallpaper is portrait, so bounding
 *    the width would have left the height unbounded on an unusual aspect ratio.
 *  - **JPEG, not PNG.** PNG is lossless and compresses a photograph terribly; at this size
 *    it would be megabytes. JPEG at [JPEG_QUALITY] is a fraction of that for a background
 *    nobody inspects pixel-by-pixel. This is NOT a protocol change: the bulk kind is still
 *    `wallpaper`, the message shapes are untouched, and the desktop decodes through a
 *    format-sniffing decoder that has always handled both.
 *
 * Never upscales: a wallpaper already smaller than the bound is sent at its own size.
 */
object WallpaperProvider {

    /** Longest edge of the image that leaves the phone. A 1080x2400 wallpaper becomes 720x1600. */
    private const val MAX_DIMENSION_PX = 1600

    private const val JPEG_QUALITY = 85

    private val lock = Any()
    private var cachedSystemId = -1
    private var cachedContentId: String? = null
    private var cachedBytes: ByteArray? = null

    /** Content id of the current wallpaper image, or null when unreadable. */
    fun currentId(context: Context): String? {
        if (!Environment.isExternalStorageManager()) {
            return null
        }
        return try {
            val manager = WallpaperManager.getInstance(context)
            val systemId = manager.getWallpaperId(WallpaperManager.FLAG_SYSTEM)
            synchronized(lock) {
                if (systemId != cachedSystemId) {
                    refreshLocked(manager, systemId)
                }
                cachedContentId
            }
        } catch (_: Exception) {
            null // SecurityException on grant races, OEM quirks — fall back silently
        }
    }

    /** Image bytes for a `wallpaperId` handed out in status, or null. */
    fun thumbnail(id: String): ByteArray? = synchronized(lock) {
        if (id == cachedContentId) cachedBytes else null
    }

    private fun refreshLocked(manager: WallpaperManager, systemId: Int) {
        val bitmap = manager.drawable?.toBitmap() ?: return
        val longest = maxOf(bitmap.width, bitmap.height)
        // coerceAtMost(1.0) is the never-upscale rule: a wallpaper already inside the bound
        // is encoded at its own size rather than stretched to meet it.
        val scale = (MAX_DIMENSION_PX.toDouble() / longest).coerceAtMost(1.0)
        val width = (bitmap.width * scale).toInt().coerceAtLeast(1)
        val height = (bitmap.height * scale).toInt().coerceAtLeast(1)
        val scaled = if (width == bitmap.width && height == bitmap.height) {
            bitmap
        } else {
            Bitmap.createScaledBitmap(bitmap, width, height, true)
        }
        val encoded = ByteArrayOutputStream().use { out ->
            scaled.compress(Bitmap.CompressFormat.JPEG, JPEG_QUALITY, out)
            out.toByteArray()
        }
        cachedSystemId = systemId
        cachedBytes = encoded
        cachedContentId = MessageDigest.getInstance("SHA-1").digest(encoded)
            .joinToString("") { "%02x".format(it) }.take(16)
    }
}
