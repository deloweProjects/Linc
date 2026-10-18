package app.linc.android

import android.content.Context
import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import app.linc.android.ui.PcFilesScreen
import app.linc.android.ui.PcScreen
import app.linc.android.ui.SoundScreen
import app.linc.android.ui.TerminalScreen
import app.linc.android.ui.theme.LincTheme

/**
 * The full-page tools opened from Tools (D-097): Play everywhere's own page, the PC's look and
 * feel, a terminal on the PC, and the PC's files. One host, the tool picked by [EXTRA_TOOL].
 */
class PcToolActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        val tool = intent.getStringExtra(EXTRA_TOOL) ?: SOUND
        setContent {
            LincTheme {
                when (tool) {
                    PC -> PcScreen(onBack = { finish() })
                    TERMINAL -> TerminalScreen(onBack = { finish() })
                    FILES -> PcFilesScreen(onBack = { finish() })
                    else -> SoundScreen(onBack = { finish() })
                }
            }
        }
    }

    companion object {
        const val EXTRA_TOOL = "tool"
        const val SOUND = "sound"
        const val PC = "pc"
        const val TERMINAL = "terminal"
        const val FILES = "files"

        fun open(context: Context, tool: String) {
            context.startActivity(Intent(context, PcToolActivity::class.java).putExtra(EXTRA_TOOL, tool)
                .addFlags(if (context is android.app.Activity) 0 else Intent.FLAG_ACTIVITY_NEW_TASK))
        }
    }
}
