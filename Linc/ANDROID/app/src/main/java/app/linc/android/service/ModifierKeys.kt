package app.linc.android.service

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/**
 * Sticky Ctrl / Alt / Shift / Win for PC Remote's extra-keys row (v20, D-067), the way Windows
 * App's keyboard bar does them: tap a modifier and it applies to the next key — from the row
 * *or* the phone's own keyboard — then releases; tap it again to lock it on; once more to
 * release. So Ctrl, then "c" on Gboard, is Ctrl+C on the PC.
 */
object ModifierKeys {

    enum class Latch { OFF, ONCE, LOCKED }

    const val VK_SHIFT = 0x10
    const val VK_CONTROL = 0x11
    const val VK_MENU = 0x12
    const val VK_LWIN = 0x5B

    /** In the order they are pressed on the PC: Win, Ctrl, Alt, Shift. */
    val ALL = listOf(VK_LWIN, VK_CONTROL, VK_MENU, VK_SHIFT)

    private val _latches = MutableStateFlow<Map<Int, Latch>>(emptyMap())
    val latches: StateFlow<Map<Int, Latch>> = _latches

    /** Any modifier latched? Then typed characters must go as key chords, not Unicode text. */
    fun any(): Boolean = _latches.value.values.any { it != Latch.OFF }

    /** Tap a modifier on the row: off → once → locked → off. */
    fun cycle(vk: Int) {
        val next = when (_latches.value[vk] ?: Latch.OFF) {
            Latch.OFF -> Latch.ONCE
            Latch.ONCE -> Latch.LOCKED
            Latch.LOCKED -> Latch.OFF
        }
        _latches.value = _latches.value + (vk to next)
    }

    fun clear() { _latches.value = emptyMap() }

    /** A key went out without a chord (a shifted character typed as text): release one-shots. */
    fun consumeOnce() {
        _latches.value = _latches.value.mapValues { (_, latch) -> if (latch == Latch.ONCE) Latch.OFF else latch }
    }

    /**
     * Press [vk] with every latched modifier held, then release the one-shot ones.
     * [send] is (virtualKey, down) — MirrorControl.key in the app, a recorder in tests.
     */
    fun chord(vk: Int, send: (Int, Boolean) -> Unit) {
        val held = ALL.filter { (_latches.value[it] ?: Latch.OFF) != Latch.OFF }
        held.forEach { send(it, true) }
        send(vk, true)
        send(vk, false)
        held.asReversed().forEach { send(it, false) }
        _latches.value = _latches.value.mapValues { (_, latch) -> if (latch == Latch.ONCE) Latch.OFF else latch }
    }

    /**
     * The virtual-key for a typed character, for sending it as part of a chord. Letters and
     * digits map directly (Ctrl+C, Alt+F4's digits, Win+1); null for anything else, which is
     * then typed as text.
     */
    fun virtualKeyFor(c: Char): Int? = when (c) {
        in 'a'..'z' -> 0x41 + (c - 'a')
        in 'A'..'Z' -> 0x41 + (c - 'A')
        in '0'..'9' -> 0x30 + (c - '0')
        ' ' -> 0x20
        else -> null
    }
}
