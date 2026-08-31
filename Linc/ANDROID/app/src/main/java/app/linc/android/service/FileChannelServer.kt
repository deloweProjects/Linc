package app.linc.android.service

import android.os.Environment
import app.linc.android.protocol.Framing
import app.linc.android.protocol.ProtocolJson
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.File
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.add
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.longOrNull
import kotlinx.serialization.json.put

/**
 * Serves the files channel (2) over any transport (PROTOCOL.md v10, D-024): a
 * request/response loop of list/pull/push, reading real paths with `java.io.File`.
 * That needs the All-files-access grant (D-021, shared with wallpaper); without it
 * every op replies `{"error":"not-granted"}` and the desktop falls back to ADB sync.
 */
object FileChannelServer {

    fun serve(input: DataInputStream, output: DataOutputStream) {
        while (true) {
            val requestJson = Framing.read(input) ?: return
            val request = runCatching { ProtocolJson.parseToJsonElement(requestJson).jsonObject }.getOrNull()
                ?: continue
            val op = (request["op"] as? JsonPrimitive)?.contentOrNull
            val path = (request["path"] as? JsonPrimitive)?.contentOrNull

            if (!Environment.isExternalStorageManager()) {
                Framing.write(output, errorJson("not-granted"))
                continue
            }
            when (op) {
                "list" -> handleList(output, path)
                "pull" -> handlePull(output, path)
                "push" -> handlePush(input, output, path,
                    (request["size"] as? JsonPrimitive)?.longOrNull ?: -1)
                else -> Framing.write(output, errorJson("bad-op"))
            }
        }
    }

    private fun handleList(output: DataOutputStream, path: String?) {
        val dir = path?.let(::File)
        if (dir == null || !dir.isDirectory) {
            Framing.write(output, errorJson("not-a-directory"))
            return
        }
        val json = buildJsonObject {
            put("entries", buildJsonArray {
                dir.listFiles().orEmpty()
                    .sortedWith(compareByDescending<File> { it.isDirectory }.thenBy { it.name.lowercase() })
                    .forEach { file ->
                        add(buildJsonObject {
                            put("name", file.name)
                            put("size", file.length())
                            put("dir", file.isDirectory)
                            put("modified", file.lastModified())
                        })
                    }
            })
        }
        Framing.write(output, json.toString())
    }

    private fun handlePull(output: DataOutputStream, path: String?) {
        val file = path?.let(::File)
        if (file == null || !file.isFile) {
            Framing.write(output, errorJson("not-found"))
            return
        }
        Framing.write(output, """{"size":${file.length()}}""")
        file.inputStream().use { it.copyTo(output) }
        output.flush()
    }

    private fun handlePush(input: DataInputStream, output: DataOutputStream, path: String?, size: Long) {
        val file = path?.let(::File)
        if (file == null || size < 0) {
            Framing.write(output, errorJson("bad-request"))
            return
        }
        val ok = runCatching {
            file.parentFile?.mkdirs()
            file.outputStream().use { out ->
                var remaining = size
                val buffer = ByteArray(64 * 1024)
                while (remaining > 0) {
                    val read = input.read(buffer, 0, minOf(buffer.size.toLong(), remaining).toInt())
                    if (read < 0) throw java.io.EOFException()
                    out.write(buffer, 0, read)
                    remaining -= read
                }
            }
        }.isSuccess
        Framing.write(output, if (ok) """{"ok":true}""" else errorJson("write-failed"))
    }

    private fun errorJson(code: String) = """{"error":"$code"}"""
}
