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
import app.linc.android.ui.TerminalSession
import app.linc.android.ui.theme.LincTheme

/**
 * The PC tools on a page of their own, outside the Tools tab: above all a terminal, full screen
 * (Tools → Terminal opens each shell here), and Sound, PC and Files for anything that links
 * straight to them. One host, the tool picked by [EXTRA_TOOL].
 */
class PcToolActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        val tool = intent.getStringExtra(EXTRA_TOOL) ?: SOUND
        val shell = intent.getStringExtra(EXTRA_SHELL)
        setContent {
            LincTheme {
                when (tool) {
                    PC -> PcScreen(onBack = { finish() })
                    TERMINAL -> TerminalSession(shell = shell, onClose = { finish() })
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
        const val EXTRA_SHELL = "shell"

        fun open(context: Context, tool: String) {
            context.startActivity(Intent(context, PcToolActivity::class.java).putExtra(EXTRA_TOOL, tool)
                .addFlags(if (context is android.app.Activity) 0 else Intent.FLAG_ACTIVITY_NEW_TASK))
        }

        /** A shell full screen; [shell] null goes back to the one already running. */
        fun openTerminal(context: Context, shell: String?) {
            context.startActivity(Intent(context, PcToolActivity::class.java).putExtra(EXTRA_TOOL, TERMINAL).putExtra(EXTRA_SHELL, shell)
                .addFlags(if (context is android.app.Activity) 0 else Intent.FLAG_ACTIVITY_NEW_TASK))
        }
    }
}
