package app.linc.android.service

import android.content.Context
import android.content.Intent
import androidx.core.content.pm.ShortcutInfoCompat
import androidx.core.content.pm.ShortcutManagerCompat
import androidx.core.graphics.drawable.IconCompat
import app.linc.android.MainActivity
import app.linc.android.R

/**
 * Direct Share (1.0.0.11, D-083): the PC as a one-tap target at the top of Android's share sheet,
 * next to "Linc — send to PC" in the app row. Shares go to [app.linc.android.ShareReceiverActivity]
 * (res/xml/shortcuts.xml), which sends over Linc — or Quick Share when Linc isn't connected.
 */
object DirectShare {
    private const val ID = "linc-pc"
    const val CATEGORY = "app.linc.android.category.SEND_TO_PC"

    /** Publish (or rename) the shortcut. Called when a PC connects, with its name if it sent one. */
    fun publish(context: Context, pcName: String?) {
        runCatching {
            val label = pcName?.takeIf { it.isNotBlank() } ?: "Your PC"
            val shortcut = ShortcutInfoCompat.Builder(context, ID)
                .setShortLabel(label)
                .setLongLabel("Send to $label with Linc")
                .setIcon(IconCompat.createWithResource(context, R.mipmap.ic_launcher))
                .setCategories(setOf(CATEGORY))
                .setLongLived(true)
                .setIntent(Intent(context, MainActivity::class.java).setAction(Intent.ACTION_VIEW))
                .build()
            ShortcutManagerCompat.pushDynamicShortcut(context, shortcut)
        }.onFailure { LogStore.log(LogLevel.WARN, "Couldn't add the PC to the share sheet: ${it.javaClass.simpleName}") }
    }
}
