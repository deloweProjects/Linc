package app.linc.android.service

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.core.content.FileProvider
import java.io.File

/**
 * M17b Part C: hands a downloaded APK to Android's own package installer.
 *
 * **Android shows its own confirmation, and that is correct — nothing here works around it.**
 * Linc never installs anything silently; it only asks the system to open the installer.
 *
 * The whole path is gated on [UpdateSettings.isEnabled]: with no manifest URL configured there is
 * no APK to hand over and [install] refuses before touching an Intent.
 */
object UpdateInstaller {

    /** What [install] decided to do, so the caller can show plain language rather than an error. */
    sealed interface Result {
        /** The system installer was opened; Android now asks the user to confirm. */
        data object Launched : Result

        /** "Install unknown apps" is off for Linc. [intent] takes the user to the right settings page. */
        data class NeedsPermission(val intent: Intent, val message: String) : Result

        /** Nothing could be done; [message] is plain language, safe to show as-is. */
        data class Failed(val message: String) : Result
    }

    /**
     * Opens the system package installer for [apk].
     *
     * Returns [Result.NeedsPermission] rather than throwing when the user has not allowed Linc to
     * request installs — the caller shows the sentence and offers the settings intent (Part C).
     */
    fun install(context: Context, apk: File): Result {
        if (!UpdateSettings.isEnabled(context)) {
            // Belt and braces: with the feature off, nothing in this path may execute.
            return Result.Failed("Automatic updates are turned off.")
        }

        if (!apk.exists() || apk.length() == 0L) {
            return Result.Failed("The update file wasn't downloaded properly. Try again.")
        }

        if (!canRequestInstalls(context)) {
            return Result.NeedsPermission(
                intent = unknownSourcesIntent(context),
                message = "To finish updating, allow Linc to install apps, then tap Update again.",
            )
        }

        val uri: Uri = try {
            FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", apk)
        } catch (_: IllegalArgumentException) {
            return Result.Failed("Linc couldn't open the update file. Try again.")
        }

        val intent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, "application/vnd.android.package-archive")
            // The installer runs in another process, so it needs read access to our file, and a
            // new task because we are handing off rather than showing a screen of our own.
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }

        return try {
            context.startActivity(intent)
            Result.Launched
        } catch (_: ActivityNotFoundException) {
            Result.Failed("This phone has no app installer available.")
        }
    }

    /** Whether the user has allowed Linc to request package installs. Always true before Oreo. */
    fun canRequestInstalls(context: Context): Boolean =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            context.packageManager.canRequestPackageInstalls()
        } else {
            true
        }

    /** The settings page where the user grants "install unknown apps" for Linc. */
    fun unknownSourcesIntent(context: Context): Intent =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            Intent(
                Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                Uri.parse("package:${context.packageName}"),
            )
        } else {
            Intent(Settings.ACTION_SECURITY_SETTINGS)
        }
}
