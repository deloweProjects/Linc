package app.linc.android

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import app.linc.android.ui.RemoteScreen
import app.linc.android.ui.theme.LincTheme

/**
 * A standalone host for the Remote screen (was Tools, M4a). Remote is a tab in the main app since
 * 1.0.0.11; this stays so anything that opens Tools directly (an old shortcut) still lands there.
 */
class ToolsActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        setContent {
            LincTheme {
                RemoteScreen(
                    onBack = { finish() },
                    onMirror = { startActivity(Intent(this, MirrorActivity::class.java)) },
                )
            }
        }
    }
}
