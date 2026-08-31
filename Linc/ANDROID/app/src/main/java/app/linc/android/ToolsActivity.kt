package app.linc.android

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import app.linc.android.ui.ToolsScreen
import app.linc.android.ui.theme.LincTheme

/**
 * Full-screen host for [ToolsScreen] (M4a): the phone's remote keyboard + trackpad. Opened only
 * from the Home "Tools" card, same precedent as [MirrorActivity].
 */
class ToolsActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            LincTheme {
                ToolsScreen(onBack = { finish() })
            }
        }
    }
}
