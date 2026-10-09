package app.linc.android.service

/**
 * The floating PC keyboard's layout and key semantics (v21, D-068) — a US PC layout, five
 * rows of 15.5 units, with an Fn layer for F1–F12 / Home / End. Pure, so it is unit-tested;
 * FloatingKeyboard only draws it and turns touches into [press] calls.
 *
 * **Typed characters go as text, not keys.** A plain or shifted character is sent as Unicode
 * (`pc.input text`), so "@" is "@" whatever keyboard layout the PC uses. Only when Ctrl, Alt or
 * Win is latched does a character become a virtual-key chord (Ctrl+C, Ctrl+/, Win+E), because
 * shortcuts are defined on keys, not characters.
 */
object PcKeyboardLayout {

    sealed class Action {
        /** A character key: [normal] / [shifted]. */
        data class Char(val normal: kotlin.Char, val shifted: kotlin.Char) : Action()
        /** A non-character key, as a Windows virtual-key code. [repeats] while held. */
        data class Key(val vk: Int, val repeats: Boolean = false) : Action()
        data class Modifier(val vk: Int) : Action()
        object Fn : Action()
        /** Voice typing: speak, and the words are typed on the PC. */
        object Voice : Action()
        object Hide : Action()
    }

    data class KeyCap(val label: String, val action: Action, val width: Float = 1f, val shiftLabel: String? = null)

    const val ROW_UNITS = 15.5f

    private fun chars(pairs: String): List<KeyCap> =
        pairs.chunked(2).map { KeyCap(it[0].toString(), Action.Char(it[0], it[1]), shiftLabel = it[1].toString()) }

    private fun letters(row: String): List<KeyCap> =
        row.map { KeyCap(it.toString(), Action.Char(it, it.uppercaseChar())) }

    private val BACKSPACE = KeyCap("⌫", Action.Key(0x08, repeats = true), 1.5f)

    fun rows(fn: Boolean): List<List<KeyCap>> = listOf(
        if (!fn) {
            listOf(KeyCap("Esc", Action.Key(0x1B))) + chars("`~1!2@3#4$5%6^7&8*9(0)-_=+") + BACKSPACE
        } else {
            listOf(KeyCap("Esc", Action.Key(0x1B))) +
                (1..12).map { KeyCap("F$it", Action.Key(0x6F + it)) } +
                KeyCap("PrtSc", Action.Key(0x2C)) + BACKSPACE
        },
        listOf(KeyCap("Tab", Action.Key(0x09), 1.5f)) + letters("qwertyuiop") + chars("[{]}") +
            KeyCap("\\", Action.Char('\\', '|'), shiftLabel = "|") + KeyCap("Del", Action.Key(0x2E, repeats = true)),
        listOf(KeyCap("Fn", Action.Fn, 1.75f)) + letters("asdfghjkl") + chars(";:'\"") +
            KeyCap("Enter", Action.Key(0x0D), 2.75f),
        listOf(KeyCap("Shift", Action.Modifier(ModifierKeys.VK_SHIFT), 2.25f)) + letters("zxcvbnm") + chars(",<.>/?") +
            (if (fn) KeyCap("Home", Action.Key(0x24), 1.25f) else KeyCap("PgUp", Action.Key(0x21), 1.25f)) +
            KeyCap("↑", Action.Key(0x26, repeats = true)) +
            (if (fn) KeyCap("End", Action.Key(0x23)) else KeyCap("PgDn", Action.Key(0x22))),
        listOf(
            KeyCap("Ctrl", Action.Modifier(ModifierKeys.VK_CONTROL), 1.5f),
            KeyCap("⊞", Action.Modifier(ModifierKeys.VK_LWIN), 1.25f),
            KeyCap("Alt", Action.Modifier(ModifierKeys.VK_MENU), 1.25f),
            KeyCap("🎤", Action.Voice),
            KeyCap("", Action.Key(0x20), 6.5f),
            KeyCap("⌄", Action.Hide),
            KeyCap("←", Action.Key(0x25, repeats = true)),
            KeyCap("↓", Action.Key(0x28, repeats = true)),
            KeyCap("→", Action.Key(0x27, repeats = true)),
        ),
    )

    /**
     * What a key press sends. [sendKey] is (virtualKey, down) and [sendText] types Unicode —
     * MirrorControl in the app, recorders in tests. Modifier/Fn/Hide keys are handled by the
     * view and do nothing here.
     */
    fun press(action: Action, sendKey: (Int, Boolean) -> Unit, sendText: (String) -> Unit) {
        when (action) {
            is Action.Key -> ModifierKeys.chord(action.vk, sendKey)
            is Action.Char -> {
                val latches = ModifierKeys.latches.value
                val shortcut = listOf(ModifierKeys.VK_CONTROL, ModifierKeys.VK_MENU, ModifierKeys.VK_LWIN)
                    .any { (latches[it] ?: ModifierKeys.Latch.OFF) != ModifierKeys.Latch.OFF }
                val vk = ModifierKeys.virtualKeyFor(action.normal) ?: oemKey(action.normal)
                if (shortcut && vk != null) {
                    ModifierKeys.chord(vk, sendKey)
                } else {
                    val shifted = (latches[ModifierKeys.VK_SHIFT] ?: ModifierKeys.Latch.OFF) != ModifierKeys.Latch.OFF
                    sendText((if (shifted) action.shifted else action.normal).toString())
                    ModifierKeys.consumeOnce()
                }
            }
            else -> Unit
        }
    }

    /** US-layout virtual keys for punctuation, for shortcuts like Ctrl+/ or Ctrl+-. */
    fun oemKey(c: kotlin.Char): Int? = when (c) {
        ';' -> 0xBA; '=' -> 0xBB; ',' -> 0xBC; '-' -> 0xBD; '.' -> 0xBE; '/' -> 0xBF
        '`' -> 0xC0; '[' -> 0xDB; '\\' -> 0xDC; ']' -> 0xDD; '\'' -> 0xDE
        else -> null
    }
}
