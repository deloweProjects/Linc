package app.linc.android.service

import android.content.Context
import android.graphics.Bitmap
import android.provider.MediaStore
import java.io.ByteArrayOutputStream
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.add
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/**
 * Recent camera/gallery images for the desktop's Home strip (PROTOCOL.md v10, D-024).
 * `recent` lists ids/paths/timestamps from MediaStore; `thumbnail` renders a small JPEG
 * served over the bulk channel (kind `photo`). Full images come over the files channel.
 */
object PhotosProvider {

    private const val THUMB_SIZE_PX = 256

    fun recent(context: Context, limit: Int): JsonObject {
        val photos = buildJsonArray {
            val projection = arrayOf(
                MediaStore.Images.Media._ID,
                MediaStore.Images.Media.DATA,
                MediaStore.Images.Media.DATE_TAKEN)
            runCatching {
                context.contentResolver.query(
                    MediaStore.Images.Media.EXTERNAL_CONTENT_URI,
                    projection,
                    null, null,
                    "${MediaStore.Images.Media.DATE_TAKEN} DESC",
                )?.use { cursor ->
                    val idCol = cursor.getColumnIndexOrThrow(MediaStore.Images.Media._ID)
                    val dataCol = cursor.getColumnIndexOrThrow(MediaStore.Images.Media.DATA)
                    val dateCol = cursor.getColumnIndexOrThrow(MediaStore.Images.Media.DATE_TAKEN)
                    var count = 0
                    while (cursor.moveToNext() && count < limit) {
                        val path = cursor.getString(dataCol) ?: continue
                        add(buildJsonObject {
                            put("id", cursor.getLong(idCol).toString())
                            put("path", path)
                            put("takenAt", cursor.getLong(dateCol))
                        })
                        count++
                    }
                }
            }
        }
        return buildJsonObject { put("photos", photos) }
    }

    /** A small JPEG thumbnail for a MediaStore image id, or null. */
    fun thumbnail(context: Context, id: String): ByteArray? = runCatching {
        val uri = android.content.ContentUris.withAppendedId(
            MediaStore.Images.Media.EXTERNAL_CONTENT_URI, id.toLong())
        val bitmap: Bitmap = context.contentResolver.loadThumbnail(
            uri, android.util.Size(THUMB_SIZE_PX, THUMB_SIZE_PX), null)
        ByteArrayOutputStream().use { out ->
            bitmap.compress(Bitmap.CompressFormat.JPEG, 80, out)
            out.toByteArray()
        }
    }.getOrNull()
}
