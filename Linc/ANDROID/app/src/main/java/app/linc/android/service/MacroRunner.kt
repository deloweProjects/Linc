package app.linc.android.service

import android.app.NotificationManager
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import java.io.File
import java.io.IOException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch

/**
 * The phone's macros, kept in app-private storage (`macros.json`). A first look gets
 * [MacroBook.starters]; a file this Linc can't read is kept beside as `macros.json.unreadable`
 * rather than written over.
 */
object MacroStore {
    private const val FILE = "macros.json"

    private val _macros = MutableStateFlow<List<Macro>>(emptyList())
    val macros: StateFlow<List<Macro>> = _macros
    private var loaded = false

    fun init(context: Context) {
        if (loaded) return
        loaded = true
        val file = File(context.filesDir, FILE)
        val read = runCatching { if (file.exists()) file.readText() else null }.getOrNull()
        val macros = read?.let { MacroBook.decode(it) }
        if (read != null && macros == null) {
            runCatching { file.copyTo(File(context.filesDir, "$FILE.unreadable"), overwrite = true) }
            LogStore.log(LogLevel.WARN, "Macros: couldn't read macros.json; kept it as macros.json.unreadable")
        }
        _macros.value = macros ?: MacroBook.starters
    }

    fun save(context: Context, macros: List<Macro>) {
        _macros.value = macros
        val file = File(context.filesDir, FILE)
        val temp = File(context.filesDir, "$FILE.tmp")
        try {
            temp.writeText(MacroBook.encode(macros))
            if (!temp.renameTo(file)) {
                file.delete()
                temp.renameTo(file)
            }
        } catch (e: IOException) {
            LogStore.log(LogLevel.WARN, "Macros: couldn't save (${e.message})")
        }
    }

    fun put(context: Context, macro: Macro) {
        val list = _macros.value
        save(context, if (list.any { it.id == macro.id }) list.map { if (it.id == macro.id) macro else it } else list + macro)
    }

    fun remove(context: Context, id: String) = save(context, _macros.value.filterNot { it.id == id })
}

/**
 * Runs a macro step by step (Control → Macros). Steps for the PC go out on the control link
 * like the buttons they copy (they are fire-and-forget, so "ran" means "sent"); a step that can't
 * go out stops the run, and [run] says which step and why.
 */
object MacroRunner {

    /** The run in progress, or the last one's result; null before any run. */
    data class Run(
        val macroId: String,
        val name: String,
        /** Steps finished so far. */
        val done: Int,
        val total: Int,
        val text: String,
        val running: Boolean,
        val failed: Boolean = false,
    ) {
        val progress: Float get() = if (total == 0) 0f else done.toFloat() / total
    }

    private val _run = MutableStateFlow<Run?>(null)
    val run: StateFlow<Run?> = _run

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var job: Job? = null

    /** Starts [macro]; [onMirror] opens the PC mirror screen for a "Mirror the PC" step. */
    fun start(context: Context, macro: Macro, onMirror: () -> Unit) {
        if (job?.isActive == true) return
        val app = context.applicationContext
        job = scope.launch {
            val steps = macro.steps
            if (steps.isEmpty()) {
                _run.value = Run(macro.id, macro.name, 0, 0, "\"${macro.name}\" has no steps yet.", running = false, failed = true)
                return@launch
            }
            steps.forEachIndexed { i, step ->
                MacroBook.problem(step)?.let {
                    _run.value = Run(macro.id, macro.name, i, steps.size, MacroBook.failureText(i, step, it), running = false, failed = true)
                    return@launch
                }
            }
            try {
                steps.forEachIndexed { i, step ->
                    _run.value = Run(macro.id, macro.name, i, steps.size,
                        "${macro.name} · step ${i + 1} of ${steps.size}: ${MacroBook.describe(step)}", running = true)
                    val failure = runCatching { runStep(app, step, onMirror) }
                        .getOrElse { if (it is CancellationException) throw it else it.message ?: "something went wrong" }
                    if (failure != null) {
                        val text = MacroBook.failureText(i, step, failure)
                        LogStore.log(LogLevel.WARN, "Macro \"${macro.name}\": $text")
                        _run.value = Run(macro.id, macro.name, i, steps.size, text, running = false, failed = true)
                        return@launch
                    }
                }
                _run.value = Run(macro.id, macro.name, steps.size, steps.size,
                    "\"${macro.name}\" ran all ${steps.size} step${if (steps.size == 1) "" else "s"}.", running = false)
            } catch (e: CancellationException) {
                _run.value = _run.value?.copy(text = "Stopped \"${macro.name}\".", running = false)
                throw e
            }
        }
    }

    fun stop() {
        job?.cancel()
    }

    fun clear() {
        if (job?.isActive != true) _run.value = null
    }

    /** One step; the reason it couldn't run, or null when it did. */
    private suspend fun runStep(context: Context, step: MacroStep, onMirror: () -> Unit): String? {
        val kind = MacroKinds.find(step.kind) ?: return "this Linc doesn't know this step."
        if (kind.pcVersion > 0) {
            if (!CompanionOutbox.canSend(1)) return "no PC is connected."
            if (!CompanionOutbox.canSend(kind.pcVersion)) return "the PC's Linc is too old for this step; update it."
        }
        val on = step.value.trim() == "on"
        when (step.kind) {
            MacroKinds.PC_LOCK -> PcControl.lock()
            MacroKinds.PC_SLEEP -> PcControl.sleep()
            MacroKinds.PC_VOLUME -> PcControl.volumeSet(step.value.trim().toInt())
            MacroKinds.PC_MUTE -> PcControl.volumeMute(on)
            MacroKinds.PC_BRIGHTNESS -> PcControl.brightnessSet(step.value.trim().toInt())
            MacroKinds.PC_MEDIA -> PcMediaControl.send(step.value.trim())
            MacroKinds.PC_MIRROR -> onMirror()
            MacroKinds.PC_AUDIO -> MirrorControl.audio(on)
            MacroKinds.PC_OPEN -> PcTools.openOnPc(step.value.trim())
            MacroKinds.PC_DARK -> PcTools.setLook(appsDark = on, systemDark = on)
            MacroKinds.PC_CURTAIN -> PcTools.setCurtain(on)
            MacroKinds.SEND_TEXT -> if (!ClipboardBridge.sendToPc(context, step.value)) return "no PC is connected."
            MacroKinds.SEND_CLIPBOARD -> {
                val clip = context.getSystemService(ClipboardManager::class.java).primaryClip
                val text = clip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(context)?.toString()
                if (text.isNullOrEmpty()) return "the clipboard is empty, or Android didn't let Linc read it (Linc has to be open)."
                if (!ClipboardBridge.sendToPc(context, text)) return "no PC is connected."
            }
            MacroKinds.PHONE_APP -> {
                val intent = context.packageManager.getLaunchIntentForPackage(step.value.trim())
                    ?: return "${step.value} isn't installed, or has nothing to open."
                context.startActivity(intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            }
            MacroKinds.PHONE_DND -> {
                val notifications = context.getSystemService(NotificationManager::class.java)
                if (!notifications.isNotificationPolicyAccessGranted) {
                    return "Linc isn't allowed to change Do Not Disturb. Allow it in Settings › Notifications › Do Not Disturb access."
                }
                notifications.setInterruptionFilter(
                    if (on) NotificationManager.INTERRUPTION_FILTER_PRIORITY else NotificationManager.INTERRUPTION_FILTER_ALL)
            }
            MacroKinds.WAIT -> delay(((MacroBook.waitSeconds(step) ?: 0.0) * 1000).toLong())
        }
        return null
    }
}
