package app.linc.android.service

import android.content.Context

/**
 * Where Linc lives, shown under Settings → About. The desktop shows the same links in the
 * About card of `Linc.Desktop/Views/SettingsPage.xaml` — keep the two the same.
 */
object ProjectLinks {
    const val DEVELOPER = "delowe"
    const val DEVELOPER_HANDLE = "@delow3"
    const val TELEGRAM = "https://t.me/LincApp"
    const val GITHUB = "https://github.com/deloweProjects/Linc"
    const val DEVELOPER_GITHUB = "https://github.com/deloweProjects"

    private const val PREFS = "linc_community"
    private const val KEY_TELEGRAM_CARD_DONE = "telegram_card_done"

    /** Home's one-time "Join the channel" card: shown until the user joins or says not now. */
    fun telegramCardDone(context: Context): Boolean =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_TELEGRAM_CARD_DONE, false)

    fun markTelegramCardDone(context: Context) =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean(KEY_TELEGRAM_CARD_DONE, true).apply()
}
