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

    fun wire(editText: EditText) {
        editText.setOnKeyListener { _, keyCode, event ->
            if (event.action != KeyEvent.ACTION_DOWN) return@setOnKeyListener false
            when (val mapped = KeyboardMapping.map(keyCode, event.unicodeChar)) {
                is KeyboardMapping.Mapped.VirtualKey -> {
                    MirrorControl.key(mapped.code, true)
                    MirrorControl.key(mapped.code, false)
                    true
                }
                is KeyboardMapping.Mapped.Text -> {
                    MirrorControl.text(mapped.text)
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
                    MirrorControl.key(KeyboardMapping.VK_BACK, true)
                    MirrorControl.key(KeyboardMapping.VK_BACK, false)
                }
                if (now.length > previous.length) {
                    MirrorControl.text(now.substring(previous.length))
                }
            }
            override fun afterTextChanged(s: Editable) {
                // Keep the sink tiny so diffs stay cheap; never let it grow unbounded.
                if (s.length > 64) s.delete(0, s.length - 1)
            }
        })
    }
}
