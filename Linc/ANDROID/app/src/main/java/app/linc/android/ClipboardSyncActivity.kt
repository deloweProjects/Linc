package app.linc.android

import android.app.Activity
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import app.linc.android.service.ClipboardBridge

/**
 * An invisible window that exists only to hold focus for the instant it takes to read the
 * clipboard, which Android 10+ allows only to the focused app (D-075). Started by
 * [app.linc.android.service.ClipboardWatcher]; closes itself either way.
 */
class ClipboardSyncActivity : Activity() {

    private val handler = Handler(Looper.getMainLooper())

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Never linger: if focus does not come, give up rather than sit over the user's app.
        handler.postDelayed(::close, GIVE_UP_MS)
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus) return
        ClipboardBridge.pushIfChanged(this)
        close()
    }

    private fun close() {
        handler.removeCallbacksAndMessages(null)
        if (isFinishing) return
        finish()
        @Suppress("DEPRECATION")
        overridePendingTransition(0, 0)
    }

    private companion object {
        const val GIVE_UP_MS = 1_500L
    }
}
