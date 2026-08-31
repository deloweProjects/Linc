package app.linc.android.service

import android.content.Context
import android.content.pm.PackageManager
import android.graphics.Bitmap
import androidx.core.graphics.drawable.toBitmap
import java.io.ByteArrayOutputStream

/**
 * Supplies the small binaries the desktop fetches over the bulk channel (PROTOCOL.md v5).
 * M12 defines a single kind — `appIcon` (id = package name) — enough to prove the channel
 * end to end; later milestones add album art, wallpaper and thumbnails here.
 */
object BulkResources {

    private const val ICON_SIZE_PX = 128

    /** Returns the resource bytes, or null when the kind/id is unknown (⇒ 0-length reply). */
    fun fetch(context: Context, kind: String, id: String): ByteArray? = when (kind) {
        "appIcon" -> appIconPng(context, id)
        else -> null
    }

    private fun appIconPng(context: Context, packageName: String): ByteArray? = try {
        val bitmap = context.packageManager.getApplicationIcon(packageName)
            .toBitmap(ICON_SIZE_PX, ICON_SIZE_PX)
        ByteArrayOutputStream().use { out ->
            bitmap.compress(Bitmap.CompressFormat.PNG, 100, out)
            out.toByteArray()
        }
    } catch (_: PackageManager.NameNotFoundException) {
        null // no such app installed
    } catch (_: Exception) {
        null // rendering failed; treat as unavailable rather than crash the channel
    }
}
