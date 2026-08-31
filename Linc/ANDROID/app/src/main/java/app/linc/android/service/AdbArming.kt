package app.linc.android.service

import android.content.Context
import android.content.pm.PackageManager
import android.provider.Settings

/**
 * Self-arming (M13c §2.2): with `WRITE_SECURE_SETTINGS` held, the companion can turn wireless
 * debugging on itself, so a hotspot link comes up with no cable and no trip into Developer
 * options.
 *
 * The permission is granted exactly once, by Linc's PC-driven onboarding, over the ADB session it
 * already has right after installing this APK — so the user never touches ADB or a terminal
 * (D-001). A phone that never got the grant simply reports [AdbArmOutcome.DENIED] and keeps
 * working; arming is then the user's job, and the UI says so.
 *
 * TLS pairing state survives a reboot, so an already-paired PC needs no QR and no interaction:
 * boot → arm → find the port → verify → announce.
 *
 * **This never turns wireless debugging off.** Arming is a visible change to the user's device;
 * un-arming it behind their back would be a change they did not ask for and did not see. See
 * [AdbArmRules], which has no disarm path at all.
 */
object AdbArming {

    /** True when the one-time `pm grant` actually landed on this phone. */
    fun isPermissionHeld(context: Context): Boolean = try {
        context.checkSelfPermission(android.Manifest.permission.WRITE_SECURE_SETTINGS) ==
            PackageManager.PERMISSION_GRANTED
    } catch (_: Exception) {
        false
    }

    /** True when adbd is already armed. Readable without the grant. */
    fun isArmed(context: Context): Boolean = try {
        Settings.Global.getInt(context.contentResolver, AdbArmRules.SETTING_NAME, 0) == AdbArmRules.ENABLED_VALUE
    } catch (_: Exception) {
        false
    }

    /**
     * Arms wireless debugging if the grant is held and it is not already on. Never throws: a
     * refusal is [AdbArmOutcome.UNAVAILABLE] and a missing grant is [AdbArmOutcome.DENIED].
     */
    fun arm(context: Context): AdbArmOutcome {
        val outcome = AdbArmRules.plan(isPermissionHeld(context), isArmed(context))
        if (!AdbArmRules.writesSetting(outcome)) {
            LogStore.log(
                if (outcome == AdbArmOutcome.ALREADY_ON) LogLevel.INFO else LogLevel.WARN,
                AdbArmRules.describe(outcome),
            )
            return outcome
        }
        return try {
            Settings.Global.putInt(
                context.contentResolver, AdbArmRules.SETTING_NAME, AdbArmRules.ENABLED_VALUE)
            LogStore.log(LogLevel.INFO, AdbArmRules.describe(AdbArmOutcome.ARMED))
            AdbArmOutcome.ARMED
        } catch (_: SecurityException) {
            // The grant looked held but the platform refused anyway (some OEM builds do).
            LogStore.log(LogLevel.WARN, AdbArmRules.describe(AdbArmOutcome.UNAVAILABLE))
            AdbArmOutcome.UNAVAILABLE
        } catch (_: Exception) {
            LogStore.log(LogLevel.WARN, AdbArmRules.describe(AdbArmOutcome.UNAVAILABLE))
            AdbArmOutcome.UNAVAILABLE
        }
    }
}
