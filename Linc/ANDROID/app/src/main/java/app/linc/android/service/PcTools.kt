package app.linc.android.service

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Environment
import android.provider.MediaStore
import android.provider.OpenableColumns
import app.linc.android.protocol.Envelope
import app.linc.android.protocol.Framing
import app.linc.android.protocol.MessageType
import app.linc.android.protocol.ProtocolJson
import java.io.DataInputStream
import java.io.DataOutputStream
import java.nio.ByteBuffer
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.longOrNull
import kotlinx.serialization.json.put

/** The PC's look (v27 `pc.look.state`). */
data class PcLookState(
    val appsDark: Boolean = false,
    val systemDark: Boolean = true,
    val transparency: Boolean = true,
    val accent: String = "#0078D7",
    val autoAccent: Boolean = false,
    val wallpaper: String? = null,
    val error: String? = null,
    val known: Boolean = false,
)

data class PcInfoState(
    val name: String = "", val os: String = "", val cpuPct: Int = -1, val memUsedMb: Long = 0, val memTotalMb: Long = 0,
    val uptimeMin: Long = 0, val disks: List<Triple<String, Double, Double>> = emptyList(), val battery: Pair<Int, Boolean>? = null,
    // Task Manager's Performance figures (v28, optional: an older PC leaves them out).
    val cpuName: String? = null, val cores: Int = 0, val threads: Int = 0, val cpuMhz: Int = 0,
    val processes: Int = 0, val handles: Int = 0, val diskPct: Int = -1, val diskReadKBps: Long = -1, val diskWriteKBps: Long = -1,
    val netSendKbps: Long = -1, val netRecvKbps: Long = -1, val netName: String? = null,
    val gpuPct: Int = -1, val gpuName: String? = null, val memCommittedMb: Long = 0, val memCommitLimitMb: Long = 0,
    /** When it arrived (phone clock, ms), so the graphs can space the points. */
    val at: Long = 0,
)

/** The last couple of minutes of [PcInfoState]s, for the PC page's graphs. */
data class PcHistory(
    val cpu: List<Float> = emptyList(), val memory: List<Float> = emptyList(), val disk: List<Float> = emptyList(),
    val netSend: List<Float> = emptyList(), val netRecv: List<Float> = emptyList(), val gpu: List<Float> = emptyList(),
)

/** One of the PC's sound outputs (v28), for Sound → Output on the PC. */
data class PcAudioOutput(val id: String, val name: String, val bluetooth: Boolean, val isDefault: Boolean)

/** The curtain over the PC's screen while it is used from the phone (v28). */
data class PcCurtainState(val on: Boolean = false, val available: Boolean = false, val error: String? = null)

/**
 * This phone's sound on the PC over Bluetooth (v29): the PC is the phone's Bluetooth speaker.
 * [error] is not-paired, unsupported or Windows' own status; [busy] while the PC connects.
 */
data class PcBtAudioState(
    val on: Boolean = false,
    val supported: Boolean = true,
    val device: String? = null,
    val error: String? = null,
    val busy: Boolean = false,
)

data class PcApp(val pid: Int, val name: String, val title: String, val memMb: Long)

data class PcFileEntry(val name: String, val path: String, val dir: Boolean, val size: Long, val modified: Long, val drive: Boolean, val place: Boolean, val freeGb: Double?, val totalGb: Double? = null)

data class PcListing(val path: String = "", val parent: String? = null, val entries: List<PcFileEntry> = emptyList(), val error: String? = null, val loading: Boolean = false)

/** A file going to or from the PC: progress 0..1, then done or failed. */
data class PcTransfer(val id: String, val name: String, val upload: Boolean, val progress: Float, val done: Boolean, val error: String?)

/**
 * The phone side of the PC tools (v27, D-097): the PC's look, how it is doing, what it runs, its
 * files and a terminal. Requests go out on the control link; answers come back as their own
 * message types into these flows. Files travel on channel 10 and the terminal on channel 9.
 */
object PcTools {

    fun ready(): Boolean = CompanionOutbox.canSend(27)

    private fun send(type: String, payload: JsonObject = buildJsonObject { }) =
        CompanionOutbox.trySend(Envelope(type = type, payload = payload), requiredVersion = 27)

    // ---- look ---------------------------------------------------------------------------------

    private val _look = MutableStateFlow(PcLookState())
    val look: StateFlow<PcLookState> = _look

    fun refreshLook() = send(MessageType.PC_LOOK_GET)

    fun setLook(appsDark: Boolean? = null, systemDark: Boolean? = null, accent: String? = null, autoAccent: Boolean? = null, transparency: Boolean? = null) =
        send(MessageType.PC_LOOK_SET, buildJsonObject {
            appsDark?.let { put("appsDark", it) }
            systemDark?.let { put("systemDark", it) }
            accent?.let { put("accent", it) }
            autoAccent?.let { put("autoAccent", it) }
            transparency?.let { put("transparency", it) }
        })

    /** A photo as the PC's wallpaper: scaled so the message stays under the link's 1 MB frame. */
    fun setWallpaper(context: Context, uri: Uri, fit: String): Boolean {
        val bytes = WallpaperImage.jpegFor(context, uri) ?: return false
        send(MessageType.PC_WALLPAPER_SET, buildJsonObject {
            put("jpeg", android.util.Base64.encodeToString(bytes, android.util.Base64.NO_WRAP))
            put("fit", fit)
        })
        return true
    }

    fun onLook(p: JsonObject) {
        _look.value = PcLookState(
            appsDark = p.bool("appsDark"), systemDark = p.bool("systemDark"), transparency = p.bool("transparency"),
            accent = p.str("accent") ?: "#0078D7", autoAccent = p.bool("autoAccent"), wallpaper = p.str("wallpaper"),
            error = p.str("error"), known = true,
        )
    }

    // ---- info and apps ------------------------------------------------------------------------

    private val _info = MutableStateFlow(PcInfoState())
    val info: StateFlow<PcInfoState> = _info
    private val _apps = MutableStateFlow<List<PcApp>?>(null)
    val apps: StateFlow<List<PcApp>?> = _apps

    private val _history = MutableStateFlow(PcHistory())
    val history: StateFlow<PcHistory> = _history

    fun refreshInfo() = send(MessageType.PC_INFO_GET)
    fun refreshApps() = send(MessageType.PC_APPS_GET)
    fun endApp(pid: Int) = send(MessageType.PC_APPS_END, buildJsonObject { put("pid", pid) })
    fun openOnPc(url: String) = send(MessageType.PC_OPEN, buildJsonObject { put("url", url) })

    fun onInfo(p: JsonObject) {
        _info.value = PcInfoState(
            name = p.str("name") ?: "", os = p.str("os") ?: "", cpuPct = p.int("cpuPct") ?: -1,
            memUsedMb = p.long("memUsedMb") ?: 0, memTotalMb = p.long("memTotalMb") ?: 0, uptimeMin = p.long("uptimeMin") ?: 0,
            disks = (p["disks"] as? JsonArray).orEmpty().mapNotNull { e ->
                val o = runCatching { e.jsonObject }.getOrNull() ?: return@mapNotNull null
                Triple(o.str("name") ?: "", o.double("freeGb") ?: 0.0, o.double("totalGb") ?: 0.0)
            },
            battery = (p["battery"] as? JsonObject)?.let { (it.int("percent") ?: 0) to it.bool("charging") },
            cpuName = p.str("cpuName"), cores = p.int("cores") ?: 0, threads = p.int("threads") ?: 0, cpuMhz = p.int("cpuMhz") ?: 0,
            processes = p.int("processes") ?: 0, handles = p.int("handles") ?: 0, diskPct = p.int("diskPct") ?: -1,
            diskReadKBps = p.long("diskReadKBps") ?: -1, diskWriteKBps = p.long("diskWriteKBps") ?: -1,
            netSendKbps = p.long("netSendKbps") ?: -1, netRecvKbps = p.long("netRecvKbps") ?: -1, netName = p.str("netName"),
            gpuPct = p.int("gpuPct") ?: -1, gpuName = p.str("gpuName"),
            memCommittedMb = p.long("memCommittedMb") ?: 0, memCommitLimitMb = p.long("memCommitLimitMb") ?: 0,
            at = System.currentTimeMillis(),
        )
        val i = _info.value
        fun List<Float>.plus(v: Float?) = if (v == null) this else (this + v).takeLast(HISTORY_POINTS)
        val h = _history.value
        _history.value = PcHistory(
            cpu = h.cpu.plus(i.cpuPct.takeIf { it >= 0 }?.toFloat()),
            memory = h.memory.plus(if (i.memTotalMb > 0) 100f * i.memUsedMb / i.memTotalMb else null),
            disk = h.disk.plus(i.diskPct.takeIf { it >= 0 }?.toFloat()),
            netSend = h.netSend.plus(i.netSendKbps.takeIf { it >= 0 }?.toFloat()),
            netRecv = h.netRecv.plus(i.netRecvKbps.takeIf { it >= 0 }?.toFloat()),
            gpu = h.gpu.plus(i.gpuPct.takeIf { it >= 0 }?.toFloat()),
        )
    }

    /** Points kept per graph: at one a second, the last two minutes. */
    const val HISTORY_POINTS = 120

    // ---- sound output and the screen curtain (v28) ------------------------------------------

    fun v28(): Boolean = CompanionOutbox.canSend(28)

    private val _outputs = MutableStateFlow<List<PcAudioOutput>?>(null)
    val outputs: StateFlow<List<PcAudioOutput>?> = _outputs

    fun refreshOutputs() {
        if (v28()) CompanionOutbox.trySend(Envelope(type = MessageType.PC_AUDIO_OUTPUTS_GET, payload = buildJsonObject { }), requiredVersion = 28)
    }

    fun setOutput(id: String) {
        CompanionOutbox.trySend(Envelope(type = MessageType.PC_AUDIO_OUTPUT_SET, payload = buildJsonObject { put("id", id) }), requiredVersion = 28)
        // Show the choice at once; the PC's answer confirms it.
        _outputs.value = _outputs.value?.map { it.copy(isDefault = it.id == id) }
    }

    fun onOutputs(p: JsonObject) {
        _outputs.value = (p["outputs"] as? JsonArray).orEmpty().mapNotNull { e ->
            val o = runCatching { e.jsonObject }.getOrNull() ?: return@mapNotNull null
            PcAudioOutput(o.str("id") ?: return@mapNotNull null, o.str("name") ?: "", o.bool("bluetooth"), o.bool("default"))
        }
    }

    private val _curtain = MutableStateFlow(PcCurtainState())
    val curtain: StateFlow<PcCurtainState> = _curtain

    fun setCurtain(on: Boolean) {
        CompanionOutbox.trySend(Envelope(type = MessageType.PC_CURTAIN_SET, payload = buildJsonObject { put("on", on) }), requiredVersion = 28)
        _curtain.value = _curtain.value.copy(on = on)
    }

    fun onCurtain(p: JsonObject) {
        _curtain.value = PcCurtainState(p.bool("on"), p.bool("available"), p.str("error"))
    }

    private val _btAudio = MutableStateFlow(PcBtAudioState())
    val btAudio: StateFlow<PcBtAudioState> = _btAudio

    /** A v29 PC can be this phone's Bluetooth speaker: no recording on the phone. */
    fun btAudioAvailable(): Boolean = CompanionOutbox.canSend(29)

    fun refreshBtAudio() {
        if (btAudioAvailable()) CompanionOutbox.trySend(Envelope(type = MessageType.PC_BTAUDIO_GET, payload = buildJsonObject { }), requiredVersion = 29)
    }

    /**
     * Ask the PC to connect to this phone as its Bluetooth speaker (or let go). The names are
     * what the phone is called in Bluetooth, so the PC picks this phone among those paired.
     */
    fun setBtAudio(context: Context, on: Boolean) {
        val names = buildList {
            runCatching { android.provider.Settings.Global.getString(context.contentResolver, android.provider.Settings.Global.DEVICE_NAME) }
                .getOrNull()?.takeIf { it.isNotBlank() }?.let { add(it) }
            add(android.os.Build.MODEL)
        }
        CompanionOutbox.trySend(Envelope(type = MessageType.PC_BTAUDIO_SET, payload = buildJsonObject {
            put("on", on)
            put("names", JsonArray(names.map { JsonPrimitive(it) }))
        }), requiredVersion = 29)
        _btAudio.value = _btAudio.value.copy(busy = on, error = null, on = if (on) _btAudio.value.on else false)
    }

    fun onBtAudio(p: JsonObject) {
        _btAudio.value = PcBtAudioState(
            on = p.bool("on"),
            supported = (p["supported"] as? JsonPrimitive)?.booleanOrNull ?: true,
            device = p.str("device"),
            error = p.str("error"),
        )
        LogStore.log(LogLevel.INFO, when {
            p.bool("on") -> "Phone sound: plays on the PC over Bluetooth"
            p.str("error") != null -> "Phone sound: the PC couldn't connect over Bluetooth (${p.str("error")})"
            else -> "Phone sound: not playing on the PC"
        })
    }

    fun onApps(p: JsonObject) {
        _apps.value = (p["apps"] as? JsonArray).orEmpty().mapNotNull { e ->
            val o = runCatching { e.jsonObject }.getOrNull() ?: return@mapNotNull null
            PcApp(o.int("pid") ?: return@mapNotNull null, o.str("name") ?: "", o.str("title") ?: "", o.long("memMb") ?: 0)
        }
    }

    // ---- files --------------------------------------------------------------------------------

    private val _listing = MutableStateFlow(PcListing())
    val listing: StateFlow<PcListing> = _listing
    private val _transfers = MutableStateFlow<List<PcTransfer>>(emptyList())
    val transfers: StateFlow<List<PcTransfer>> = _transfers
    private val _fileNote = MutableStateFlow<String?>(null)
    /** The last file action's outcome, in words. */
    val fileNote: StateFlow<String?> = _fileNote

    /** What a channel-10 dial-back is for: a download's file name, or an upload's source. */
    private class Pending(val name: String, val upload: Uri?, val size: Long)
    private val pending = ConcurrentHashMap<String, Pending>()
    @Volatile private var appContext: Context? = null

    fun list(path: String) {
        _listing.value = _listing.value.copy(loading = true)
        send(MessageType.PC_FILES_LIST, buildJsonObject { put("path", path) })
    }

    fun onListing(p: JsonObject) {
        _listing.value = PcListing(
            path = p.str("path") ?: "",
            parent = p.str("parent"),
            entries = (p["entries"] as? JsonArray).orEmpty().mapNotNull { e ->
                val o = runCatching { e.jsonObject }.getOrNull() ?: return@mapNotNull null
                PcFileEntry(o.str("name") ?: "", o.str("path") ?: return@mapNotNull null, o.bool("dir"), o.long("size") ?: 0,
                    o.long("modified") ?: 0, o.bool("drive"), o.bool("place"), o.double("freeGb"), o.double("totalGb"))
            },
            error = p.str("error"),
        )
    }

    private fun op(op: String, id: String = UUID.randomUUID().toString(), build: kotlinx.serialization.json.JsonObjectBuilder.() -> Unit) =
        send(MessageType.PC_FILES_OP, buildJsonObject { put("op", op); put("id", id); build() })

    fun makeFolder(dir: String, name: String) = op("mkdir") { put("path", dir); put("name", name) }
    fun rename(path: String, name: String) = op("rename") { put("path", path); put("name", name) }
    fun delete(path: String) = op("delete") { put("path", path) }
    fun openFile(path: String) = op("open") { put("path", path) }

    fun download(context: Context, entry: PcFileEntry) {
        appContext = context.applicationContext
        val id = UUID.randomUUID().toString()
        pending[id] = Pending(entry.name, null, entry.size)
        progress(id, entry.name, false, 0f)
        op("download", id) { put("path", entry.path) }
    }

    fun upload(context: Context, dir: String, uri: Uri) {
        appContext = context.applicationContext
        val (name, size) = nameAndSize(context, uri)
        val id = UUID.randomUUID().toString()
        pending[id] = Pending(name, uri, size)
        progress(id, name, true, 0f)
        op("upload", id) { put("dir", dir); put("name", name); put("size", size) }
    }

    fun onDone(p: JsonObject) {
        val op = p.str("op") ?: return
        val id = p.str("id") ?: ""
        val ok = p.bool("ok")
        val error = p.str("error")
        if (op == "download" || op == "upload") {
            val t = _transfers.value.firstOrNull { it.id == id }
            if (t != null) finish(id, if (ok) null else error ?: "It didn't work.")
            pending.remove(id)
        }
        _fileNote.value = if (ok) when (op) {
            "mkdir" -> "Folder made"
            "rename" -> "Renamed"
            "delete" -> "Moved to the PC's Recycle Bin"
            "open" -> "Opened on the PC"
            "upload" -> "Sent to the PC"
            else -> null
        } else error ?: "That didn't work on the PC."
        if (op != "open" && op != "download") list(_listing.value.path) // show the change
    }

    fun clearNote() { _fileNote.value = null }

    /** Channel 10: the PC says which transfer this is, then the bytes go whichever way it is. */
    fun serveFiles(input: DataInputStream, output: DataOutputStream) {
        val header = Framing.read(input)?.let { runCatching { ProtocolJson.parseToJsonElement(it).jsonObject }.getOrNull() } ?: return
        val id = header.str("id") ?: return
        val p = pending[id] ?: return
        val context = appContext ?: return
        try {
            if (header.str("op") == "download") {
                val size = header.long("size") ?: p.size
                receive(context, id, header.str("name") ?: p.name, size, input)
            } else {
                val source = p.upload ?: return
                context.contentResolver.openInputStream(source)?.use { stream ->
                    val buffer = ByteArray(64 * 1024)
                    var sent = 0L
                    while (true) {
                        val n = stream.read(buffer)
                        if (n < 0) break
                        output.write(buffer, 0, n)
                        sent += n
                        if (p.size > 0) progress(id, p.name, true, sent.toFloat() / p.size)
                    }
                    output.flush()
                }
                // The PC says when it has saved it (pc.files.done); closing here ends the stream.
            }
        } catch (e: Exception) {
            finish(id, "The transfer broke off (${e.message}).")
        }
    }

    /** Into Download/Linc on the phone, through MediaStore (no storage permission needed). */
    private fun receive(context: Context, id: String, name: String, size: Long, input: DataInputStream) {
        val values = ContentValues().apply {
            put(MediaStore.Downloads.DISPLAY_NAME, name)
            put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/Linc")
            put(MediaStore.Downloads.IS_PENDING, 1)
        }
        val resolver = context.contentResolver
        val target = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: run { finish(id, "The phone has no room for it."); return }
        try {
            resolver.openOutputStream(target)?.use { out ->
                val buffer = ByteArray(64 * 1024)
                var got = 0L
                while (size < 0 || got < size) {
                    val n = input.read(buffer, 0, if (size < 0) buffer.size else minOf(buffer.size.toLong(), size - got).toInt())
                    if (n < 0) break
                    out.write(buffer, 0, n)
                    got += n
                    if (size > 0) progress(id, name, false, got.toFloat() / size)
                }
                if (size > 0 && got < size) throw java.io.EOFException("only ${got / 1024} of ${size / 1024} KB arrived")
            }
            resolver.update(target, ContentValues().apply { put(MediaStore.Downloads.IS_PENDING, 0) }, null, null)
            finish(id, null)
            LogStore.log(LogLevel.INFO, "PC tools: downloaded $name from the PC")
        } catch (e: Exception) {
            resolver.delete(target, null, null)
            finish(id, "The download broke off (${e.message}).")
        }
    }

    private fun progress(id: String, name: String, upload: Boolean, value: Float) {
        val others = _transfers.value.filter { it.id != id }
        _transfers.value = listOf(PcTransfer(id, name, upload, value, false, null)) + others.take(20)
    }

    private fun finish(id: String, error: String?) {
        _transfers.value = _transfers.value.map { if (it.id == id) it.copy(progress = 1f, done = true, error = error) else it }
    }

    private fun nameAndSize(context: Context, uri: Uri): Pair<String, Long> = runCatching {
        context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use {
            if (it.moveToFirst()) (it.getString(0) ?: "file") to (if (it.isNull(1)) -1L else it.getLong(1)) else null
        }
    }.getOrNull() ?: ("file" to -1L)

    // ---- terminal -----------------------------------------------------------------------------

    private val _shells = MutableStateFlow(listOf("powershell", "cmd"))
    val shells: StateFlow<List<String>> = _shells
    private val _termError = MutableStateFlow<String?>(null)
    val termError: StateFlow<String?> = _termError
    private val _termOpen = MutableStateFlow(false)
    val termOpen: StateFlow<Boolean> = _termOpen

    /** The screen of the running terminal (one at a time). */
    @Volatile var screen = TerminalEmulator(80, 30)
        private set
    private val _screenVersion = MutableStateFlow(0L)
    val screenVersion: StateFlow<Long> = _screenVersion
    private val toPc = LinkedBlockingQueue<ByteArray>()

    fun refreshShells() = send(MessageType.PC_TERM_GET)

    fun onTermState(p: JsonObject) {
        (p["shells"] as? JsonArray)?.mapNotNull { (it as? JsonPrimitive)?.contentOrNull }?.let { _shells.value = it }
        _termError.value = p.str("error")
    }

    fun openTerminal(shell: String, cols: Int, rows: Int) {
        screen = TerminalEmulator(cols, rows)
        _screenVersion.value++
        toPc.clear()
        _termError.value = null
        send(MessageType.PC_TERM_OPEN, buildJsonObject { put("shell", shell); put("cols", cols); put("rows", rows) })
    }

    fun type(text: String) { if (text.isNotEmpty()) toPc.offer(frame(1, text.toByteArray(Charsets.UTF_8))) }

    fun resize(cols: Int, rows: Int) {
        synchronized(screen) { screen.resize(cols, rows) }
        _screenVersion.value++
        toPc.offer(frame(2, ByteBuffer.allocate(4).putShort(cols.toShort()).putShort(rows.toShort()).array()))
    }

    fun closeTerminal() { toPc.offer(CLOSE) }

    private val CLOSE = ByteArray(0)

    private fun frame(type: Int, payload: ByteArray): ByteArray =
        ByteBuffer.allocate(5 + payload.size).put(type.toByte()).putInt(payload.size).put(payload).array()

    /** Channel 9: the console's VT text in, keystrokes and resizes out. */
    fun serveTerminal(input: DataInputStream, output: DataOutputStream) {
        _termOpen.value = true
        val writer = Thread({
            try {
                while (true) {
                    val next = toPc.poll(500, TimeUnit.MILLISECONDS) ?: continue
                    if (next === CLOSE) break
                    output.write(next); output.flush()
                }
            } catch (_: Exception) { }
            runCatching { input.close() }
        }, "linc-term-writer").apply { isDaemon = true; start() }
        try {
            val header = ByteArray(5)
            while (true) {
                input.readFully(header)
                val length = ByteBuffer.wrap(header, 1, 4).int
                if (length !in 0..(1 shl 20)) break
                val payload = ByteArray(length)
                input.readFully(payload)
                when (header[0].toInt()) {
                    1 -> { synchronized(screen) { screen.feed(payload) }; _screenVersion.value++ }
                    3 -> { synchronized(screen) { screen.feed("\r\n[The shell closed.]\r\n") }; _screenVersion.value++; break }
                }
            }
        } catch (_: Exception) {
        } finally {
            _termOpen.value = false
            toPc.offer(CLOSE)
            writer.interrupt()
            _screenVersion.value++
        }
    }

    // ---- json helpers -------------------------------------------------------------------------

    private fun JsonObject.str(k: String) = (this[k] as? JsonPrimitive)?.takeIf { it.isString }?.contentOrNull
    private fun JsonObject.bool(k: String) = (this[k] as? JsonPrimitive)?.booleanOrNull ?: false
    private fun JsonObject.int(k: String) = (this[k] as? JsonPrimitive)?.intOrNull
    private fun JsonObject.long(k: String) = (this[k] as? JsonPrimitive)?.longOrNull
    private fun JsonObject.double(k: String) = (this[k] as? JsonPrimitive)?.doubleOrNull
}

/** A photo, made small enough to send as a wallpaper: ≤ 2560 px, JPEG, ≤ ~700 KB. */
object WallpaperImage {
    fun jpegFor(context: Context, uri: Uri): ByteArray? = runCatching {
        val bounds = android.graphics.BitmapFactory.Options().apply { inJustDecodeBounds = true }
        context.contentResolver.openInputStream(uri)?.use { android.graphics.BitmapFactory.decodeStream(it, null, bounds) }
        var sample = 1
        while (maxOf(bounds.outWidth, bounds.outHeight) / sample > 2560) sample *= 2
        val bitmap = context.contentResolver.openInputStream(uri)?.use {
            android.graphics.BitmapFactory.decodeStream(it, null, android.graphics.BitmapFactory.Options().apply { inSampleSize = sample })
        } ?: return null
        var quality = 90
        while (true) {
            val out = java.io.ByteArrayOutputStream()
            bitmap.compress(android.graphics.Bitmap.CompressFormat.JPEG, quality, out)
            if (out.size() <= 700 * 1024 || quality <= 50) return out.toByteArray()
            quality -= 10
        }
        @Suppress("UNREACHABLE_CODE") null
    }.getOrNull()
}
