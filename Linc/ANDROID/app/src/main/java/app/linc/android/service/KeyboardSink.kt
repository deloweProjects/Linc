package app.linc.android.service

import android.text.Editable
import android.text.TextWatcher
import android.view.KeyEvent
import android.widget.EditText

/**
 * Wires an invisible IME-sink `EditText` to forward keystrokes to the PC via [MirrorControl],
 * using [KeyboardMapping] for the key-vs-text decision. Shared by MirrorActivity's keyboard
 * button and the Tools page (M4a) so there is exactly one keyboard glue path, not two.
 */
object KeyboardSink {

    /**
     * Typed text goes as Unicode — unless a sticky modifier is latched (v20 extra-keys row),
     * when each character that has a key is sent as a chord instead, so Ctrl then "c" is Ctrl+C.
     */
    private fun sendText(text: String) {
        if (!ModifierKeys.any()) {
            MirrorControl.text(text)
            return
        }
        for (c in text) {
            val vk = ModifierKeys.virtualKeyFor(c)
            if (vk != null) ModifierKeys.chord(vk, MirrorControl::key) else MirrorControl.text(c.toString())
        }
    }

    fun wire(editText: EditText) {
        editText.setOnKeyListener { _, keyCode, event ->
            if (event.action != KeyEvent.ACTION_DOWN) return@setOnKeyListener false
            when (val mapped = KeyboardMapping.map(keyCode, event.unicodeChar)) {
                is KeyboardMapping.Mapped.VirtualKey -> {
                    ModifierKeys.chord(mapped.code, MirrorControl::key)
                    true
                }
                is KeyboardMapping.Mapped.Text -> {
                    sendText(mapped.text)
                    true
                }
                null -> false
            }
        }
        editText.addTextChangedListener(object : TextWatcher {
            private var previous = ""
            override fun beforeTextChanged(s: CharSequence, start: Int, count: Int, after: Int) {
                previous = s.toString()
            }
            override fun onTextChanged(s: CharSequence, start: Int, before: Int, count: Int) {
                val now = s.toString()
                // Deletions -> backspaces; additions -> text. Covers soft keyboards that
                // commit text without key events (the common case on Gboard).
                repeat((previous.length - now.length).coerceAtLeast(0)) {
                    ModifierKeys.chord(KeyboardMapping.VK_BACK, MirrorControl::key)
                }
                if (now.length > previous.length) {
                    sendText(now.substring(previous.length))
                }
            }
            override fun afterTextChanged(s: Editable) {
                // Keep the sink tiny so diffs stay cheap; never let it grow unbounded.
                if (s.length > 64) s.delete(0, s.length - 1)
            }
        })
    }
}
