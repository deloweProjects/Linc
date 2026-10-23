package app.linc.android.service

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import app.linc.android.service.link.LinkStore

/**
 * Always on (LincLink, linclink.md §7): start Linc's service after the phone restarts, and after
 * an update installs, so the PC finds the phone with no taps. Only when the new connection engine
 * is on, the phone knows a PC, and the user didn't stop Linc by hand (nor left it stopped in a build
 * from before this rule, see [LinkStore.mayStartByItself]). The service's foreground
 * type is "connected device", which Android still allows to start from boot.
 */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED && intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        if (!LinkStore.engineOn(context) || !LinkStore.mayStartByItself(context) || !TransportStore.hasAnyPc(context)) return
        LogStore.init(context)
        runCatching { context.startForegroundService(Intent(context, CompanionService::class.java)) }
            .onSuccess { LogStore.log(LogLevel.INFO, "Started Linc after ${if (intent.action == Intent.ACTION_BOOT_COMPLETED) "the phone restarted" else "an update"}") }
            .onFailure { LogStore.log(LogLevel.WARN, "Android wouldn't let Linc start after a restart (${it.javaClass.simpleName}); open Linc once") }
    }
}
