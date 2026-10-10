package app.linc.android.service

import android.content.ActivityNotFoundException
import android.content.ClipData
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri

/**
 * Hands files to the phone's own Quick Share (Google's, or Samsung's on Galaxy phones) rather
 * than to Linc's link. The PC's Share page is itself a Quick Share receiver, so this reaches it
 * with no Linc connection at all — the fallback for when the link is down — and it reaches every
 * other nearby device too.
 *
 * No private API: the send goes through the public ACTION_SEND the Quick Share activity already
 * handles; we only pick that activity out of the resolver list so the user doesn't have to.
 */
object QuickShareLauncher {

    private const val GMS = "com.google.android.gms"
    private const val SAMSUNG = "com.samsung.android.app.sharelive"

    /** The action Google's Quick Share uses for "make this phone visible to receive". */
    private const val RECEIVE_ACTION = "com.google.android.gms.RECEIVE_NEARBY"

    /** Opens Quick Share's device picker for [uris]. Returns false when this phone has no Quick Share. */
    fun send(context: Context, uris: List<Uri>, mime: String = "*/*"): Boolean {
        if (uris.isEmpty()) return false
        val intent = if (uris.size == 1) {
            Intent(Intent.ACTION_SEND).putExtra(Intent.EXTRA_STREAM, uris[0])
        } else {
            Intent(Intent.ACTION_SEND_MULTIPLE).putParcelableArrayListExtra(Intent.EXTRA_STREAM, ArrayList(uris))
        }
        intent.type = mime
        // ClipData carries the read grant to whichever activity ends up handling the send.
        intent.clipData = ClipData.newUri(context.contentResolver, "files", uris[0]).apply {
            uris.drop(1).forEach { addItem(ClipData.Item(it)) }
        }
        intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)

        val target = quickShareTarget(context, intent)
        return try {
            if (target != null) {
                context.startActivity(Intent(intent).setComponent(target))
            } else {
                // No Quick Share activity we recognise: the system share sheet still lists it
                // when it exists under another name, and every other way to share besides.
                context.startActivity(Intent.createChooser(intent, "Share with").addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            }
            LogStore.log(LogLevel.INFO, "Opened Quick Share for ${uris.size} file(s)")
            true
        } catch (_: ActivityNotFoundException) {
            LogStore.log(LogLevel.WARN, "Quick Share isn't available on this phone")
            false
        } catch (_: SecurityException) {
            LogStore.log(LogLevel.WARN, "Quick Share refused to open from Linc")
            false
        }
    }

    /**
     * Makes this phone visible to Quick Share senders for a while, so the PC's Share page can
     * send to it. Google's Quick Share only; Samsung's has no public entry point for this.
     */
    fun becomeVisible(context: Context): Boolean {
        val intents = listOf(
            Intent(RECEIVE_ACTION).setType("*/*"),
            Intent().setComponent(ComponentName(GMS, "$GMS.nearby.sharing.receive.ReceiveActivityQrCodeAlias")),
        )
        for (intent in intents) {
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            try {
                context.startActivity(intent)
                LogStore.log(LogLevel.INFO, "Made this phone visible to Quick Share")
                return true
            } catch (_: ActivityNotFoundException) {
                // Try the next entry point.
            } catch (_: SecurityException) {
                // Not exported on this Play services build; try the next one.
            }
        }
        LogStore.log(LogLevel.WARN, "This phone's Quick Share can't be opened for receiving from another app")
        return false
    }

    /** Whether any Quick Share implementation is installed at all (for showing the buttons). */
    fun isAvailable(context: Context): Boolean =
        quickShareTarget(context, Intent(Intent.ACTION_SEND).setType("*/*")) != null ||
            isInstalled(context, SAMSUNG)

    private fun quickShareTarget(context: Context, intent: Intent): ComponentName? {
        val matches = runCatching {
            context.packageManager.queryIntentActivities(intent, PackageManager.MATCH_DEFAULT_ONLY)
        }.getOrDefault(emptyList())
        val hit = matches.firstOrNull {
            val info = it.activityInfo
            (info.packageName == GMS && info.name.contains("nearby.sharing", ignoreCase = true)) ||
                info.packageName == SAMSUNG
        } ?: return null
        return ComponentName(hit.activityInfo.packageName, hit.activityInfo.name)
    }

    private fun isInstalled(context: Context, pkg: String): Boolean =
        runCatching { context.packageManager.getPackageInfo(pkg, 0); true }.getOrDefault(false)
}
