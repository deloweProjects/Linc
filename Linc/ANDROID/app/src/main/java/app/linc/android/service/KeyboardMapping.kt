package app.linc.android.service

import android.view.KeyEvent

/**
 * Printable-vs-special key mapping for `pc.input` (M4a/M05): printable characters go as text,
 * backspace/enter/left/right go as Windows virtual-key codes. Lifted out of MirrorActivity
 * (which documented this at its header) so the PC Remote keyboard and the Tools page's
 * keyboard share exactly one mapping instead of growing a second one.
 */
object KeyboardMapping {

    const val VK_BACK = 0x08
    const val VK_RETURN = 0x0D
    const val VK_LEFT = 0x25
    const val VK_RIGHT = 0x27

    sealed interface Mapped {
        data class VirtualKey(val code: Int) : Mapped
        data class Text(val text: String) : Mapped
    }

    /**
     * Maps one key-down event to what to send the PC, or null if this key isn't handled here
     * (the caller should let the event fall through, e.g. to the system).
     */
    fun map(keyCode: Int, unicodeChar: Int): Mapped? = when (keyCode) {
        KeyEvent.KEYCODE_DEL -> Mapped.VirtualKey(VK_BACK)
        KeyEvent.KEYCODE_ENTER -> Mapped.VirtualKey(VK_RETURN)
        KeyEvent.KEYCODE_DPAD_LEFT -> Mapped.VirtualKey(VK_LEFT)
        KeyEvent.KEYCODE_DPAD_RIGHT -> Mapped.VirtualKey(VK_RIGHT)
        else -> if (unicodeChar != 0) Mapped.Text(String(Character.toChars(unicodeChar))) else null
    }
}
