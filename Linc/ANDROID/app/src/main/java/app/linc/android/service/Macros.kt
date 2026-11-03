package app.linc.android.service

import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

/**
 * One step of a macro. [kind] is one of [MacroKinds]; [value] is what it acts on (a level, on or
 * off, a package, a link, some text, seconds). A plain string kind, so a macro saved by a newer
 * Linc still opens here: the steps this one doesn't know are dropped.
 */
@Serializable
data class MacroStep(val kind: String, val value: String = "")

/** A named, ordered list of steps run with one tap. */
@Serializable
data class Macro(val id: String, val name: String, val steps: List<MacroStep> = emptyList())

/** What a step needs: a PC on a link new enough for it, or nothing beyond this phone. */
data class MacroKind(
    val kind: String,
    val title: String,
    /** The lowest protocol version the PC must speak, or 0 for a step that stays on the phone. */
    val pcVersion: Int,
    /** What the step's box asks for; empty for a step with nothing to fill in. */
    val valueLabel: String = "",
    val example: String = "",
) {
    val hasValue: Boolean get() = valueLabel.isNotEmpty()
}

/**
 * The kinds of step a phone macro has (the owner, 2026-11-03: "add a macros section in Tools on
 * PC and phone; on the phone add it under Control as one of the options under pages"): what the
 * phone can already ask of the PC, and a few on the phone itself.
 */
object MacroKinds {
    const val PC_LOCK = "pc_lock"
    const val PC_SLEEP = "pc_sleep"
    const val PC_VOLUME = "pc_volume"
    const val PC_MUTE = "pc_mute"
    const val PC_BRIGHTNESS = "pc_brightness"
    const val PC_MEDIA = "pc_media"
    const val PC_MIRROR = "pc_mirror"
    const val PC_AUDIO = "pc_audio"
    const val PC_OPEN = "pc_open"
    const val PC_DARK = "pc_dark"
    const val PC_CURTAIN = "pc_curtain"
    const val SEND_CLIPBOARD = "send_clipboard"
    const val SEND_TEXT = "send_text"
    const val PHONE_APP = "phone_app"
    const val PHONE_DND = "phone_dnd"
    const val WAIT = "wait"

    val all: List<MacroKind> = listOf(
        MacroKind(PC_VOLUME, "PC volume", 17, "Level, 0 to 100", "50"),
        MacroKind(PC_MUTE, "Mute the PC", 17, "on or off", "on"),
        MacroKind(PC_BRIGHTNESS, "PC screen brightness", 17, "Level, 0 to 100", "70"),
        MacroKind(PC_MEDIA, "PC media", 13, "play, pause, next or prev", "pause"),
        MacroKind(PC_MIRROR, "Mirror the PC", 14),
        MacroKind(PC_AUDIO, "PC sound on this phone", 20, "on or off", "on"),
        MacroKind(PC_OPEN, "Open on the PC", 27, "Link, file or app", "https://example.com"),
        MacroKind(PC_DARK, "PC dark mode", 27, "on or off", "on"),
        MacroKind(PC_CURTAIN, "Cover the PC's screen", 28, "on or off", "on"),
        MacroKind(SEND_CLIPBOARD, "Send this phone's clipboard to the PC", 1),
        MacroKind(SEND_TEXT, "Put text on the PC's clipboard", 1, "Text", "See you at 3"),
        MacroKind(PC_LOCK, "Lock the PC", 17),
        MacroKind(PC_SLEEP, "Put the PC to sleep", 17),
        MacroKind(PHONE_APP, "Open an app on this phone", 0, "Package", "com.spotify.music"),
        MacroKind(PHONE_DND, "Do Not Disturb on this phone", 0, "on or off", "on"),
        MacroKind(WAIT, "Wait", 0, "Seconds", "2"),
    )

    fun find(kind: String): MacroKind? = all.firstOrNull { it.kind == kind }

    val mediaActions = listOf("play", "pause", "next", "prev")

    /** The longest one Wait may hold a run (ten minutes). */
    const val MAX_WAIT_SECONDS = 600.0
}

/** Macros: what a step reads as, whether it is complete, the starters, and their file. */
object MacroBook {

    /** The ones a first look gets, the owner's "Presenting" first. */
    val starters: List<Macro> = listOf(
        Macro("presenting", "Presenting", listOf(
            MacroStep(MacroKinds.PHONE_DND, "on"),
            MacroStep(MacroKinds.PC_VOLUME, "50"),
            MacroStep(MacroKinds.PC_MIRROR),
        )),
        Macro("done-presenting", "Done presenting", listOf(MacroStep(MacroKinds.PHONE_DND, "off"))),
        Macro("movie-night", "Movie night", listOf(
            MacroStep(MacroKinds.PC_BRIGHTNESS, "30"),
            MacroStep(MacroKinds.PC_VOLUME, "70"),
            MacroStep(MacroKinds.PC_MEDIA, "play"),
        )),
        Macro("leaving", "Leaving the desk", listOf(
            MacroStep(MacroKinds.PC_MEDIA, "pause"),
            MacroStep(MacroKinds.WAIT, "1"),
            MacroStep(MacroKinds.PC_LOCK),
        )),
    )

    /** What a step says in the list: "PC volume 50", "Wait 2 s". */
    fun describe(step: MacroStep): String = when (step.kind) {
        MacroKinds.PC_LOCK -> "Lock the PC"
        MacroKinds.PC_SLEEP -> "Put the PC to sleep"
        MacroKinds.PC_VOLUME -> "PC volume ${step.value}"
        MacroKinds.PC_MUTE -> if (step.value == "off") "Unmute the PC" else "Mute the PC"
        MacroKinds.PC_BRIGHTNESS -> "PC brightness ${step.value}"
        MacroKinds.PC_MEDIA -> "PC media: ${step.value}"
        MacroKinds.PC_MIRROR -> "Mirror the PC"
        MacroKinds.PC_AUDIO -> "PC sound on this phone ${step.value}"
        MacroKinds.PC_OPEN -> "Open ${step.value} on the PC"
        MacroKinds.PC_DARK -> "PC dark mode ${step.value}"
        MacroKinds.PC_CURTAIN -> if (step.value == "off") "Uncover the PC's screen" else "Cover the PC's screen"
        MacroKinds.SEND_CLIPBOARD -> "Send this phone's clipboard to the PC"
        MacroKinds.SEND_TEXT -> "Put \"${step.value}\" on the PC's clipboard"
        MacroKinds.PHONE_APP -> "Open ${step.value}"
        MacroKinds.PHONE_DND -> "Do Not Disturb ${step.value}"
        MacroKinds.WAIT -> "Wait ${step.value} s"
        else -> step.kind
    }

    /** Why a step can't run as it is, or null when it can. */
    fun problem(step: MacroStep): String? {
        val v = step.value.trim()
        return when (step.kind) {
            MacroKinds.PC_VOLUME, MacroKinds.PC_BRIGHTNESS ->
                if (v.toIntOrNull()?.let { it in 0..100 } == true) null else "Give a level from 0 to 100"
            MacroKinds.PC_MUTE, MacroKinds.PC_AUDIO, MacroKinds.PC_DARK, MacroKinds.PC_CURTAIN, MacroKinds.PHONE_DND ->
                if (v == "on" || v == "off") null else "Say on or off"
            MacroKinds.PC_MEDIA -> if (v in MacroKinds.mediaActions) null else "Say play, pause, next or prev"
            MacroKinds.PC_OPEN -> if (v.isNotEmpty()) null else "Give a link, a file or an app"
            MacroKinds.SEND_TEXT -> if (v.isNotEmpty()) null else "Give the text to send"
            MacroKinds.PHONE_APP -> if (PACKAGE.matches(v)) null else "Pick an app, or give its package like com.spotify.music"
            MacroKinds.WAIT -> if (waitSeconds(step) != null) null else "Give seconds, from 0.1 to 600"
            MacroKinds.PC_LOCK, MacroKinds.PC_SLEEP, MacroKinds.PC_MIRROR, MacroKinds.SEND_CLIPBOARD -> null
            else -> "This Linc doesn't know this step"
        }
    }

    /** A step from the "Add step" box, tidied ("ON" → "on"). */
    fun fromInput(kind: String, text: String): MacroStep {
        val t = text.trim()
        val tidy = when (kind) {
            MacroKinds.PC_MUTE, MacroKinds.PC_AUDIO, MacroKinds.PC_DARK, MacroKinds.PC_CURTAIN, MacroKinds.PHONE_DND,
            MacroKinds.PC_MEDIA -> t.lowercase()
            MacroKinds.WAIT -> t.replace(',', '.')
            else -> t
        }
        return MacroStep(kind, if (MacroKinds.find(kind)?.hasValue == false) "" else tidy)
    }

    /** A wait step's seconds, or null when it isn't a number in range. */
    fun waitSeconds(step: MacroStep): Double? =
        step.value.trim().toDoubleOrNull()?.takeIf { it >= 0.1 && it <= MacroKinds.MAX_WAIT_SECONDS }

    /** The lowest version the PC must speak for this macro to run, 0 when it needs no PC. */
    fun pcVersion(macro: Macro): Int = macro.steps.maxOfOrNull { MacroKinds.find(it.kind)?.pcVersion ?: 0 } ?: 0

    /** "Step 2 (PC volume 50) failed: …", the line a failed run leaves. */
    fun failureText(index: Int, step: MacroStep, reason: String): String =
        "Step ${index + 1} (${describe(step)}) failed: $reason"

    /** A list with one item moved (the ↑ ↓ buttons); out of range, no change. */
    fun <T> moved(list: List<T>, from: Int, to: Int): List<T> {
        if (from !in list.indices || to !in list.indices || from == to) return list
        return list.toMutableList().apply { add(to, removeAt(from)) }
    }

    private val PACKAGE = Regex("^[A-Za-z][A-Za-z0-9_]*(\\.[A-Za-z0-9_]+)+$")

    private val json = Json { ignoreUnknownKeys = true; prettyPrint = true }

    @Serializable
    private data class MacroFile(val version: Int = 1, val macros: List<Macro> = emptyList())

    fun encode(macros: List<Macro>): String = json.encodeToString(MacroFile(macros = macros))

    /**
     * The macros in a file, or null when it isn't one (the caller then keeps it rather than
     * writing over it). Steps of a kind this Linc doesn't know are left out; their macro stays.
     */
    fun decode(text: String): List<Macro>? = try {
        json.decodeFromString<MacroFile>(text).macros
            .filter { it.id.isNotBlank() }
            .map { m -> m.copy(name = m.name.ifBlank { "Macro" }, steps = m.steps.filter { MacroKinds.find(it.kind) != null }) }
    } catch (_: SerializationException) {
        null
    } catch (_: IllegalArgumentException) {
        null
    }
}
