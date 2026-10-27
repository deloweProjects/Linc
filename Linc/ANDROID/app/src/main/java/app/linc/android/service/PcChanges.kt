package app.linc.android.service

import android.content.Context
import android.graphics.Point
import android.hardware.display.DisplayManager
import android.provider.Settings
import android.view.Display
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min

/**
 * What a PC can leave changed on this phone, and how this app puts it back by itself (owner,
 * 2026-10-27: "add section on linc app settings that fixes such stuff that may arise coz of pc").
 * Mirror's "Fill this monitor" changes the screen size and turns it sideways; the PC's Tools page
 * can turn on stay-awake, show touches, the demo-mode status bar and other animation speeds.
 *
 * Writing these back needs the one-time `WRITE_SECURE_SETTINGS` grant the PC makes over the cable
 * ([AdbArming]); the screen size goes through the window manager's own reset, which asks for the
 * same grant. While the PC's mirror watcher is still on the phone, the screen is handed to it
 * instead ([app.linc.android.ui.ScreenShape]): it knows the exact size and rotation from before.
 */
object PcChanges {
    enum class Item { SCREEN, ROTATION, STAY_AWAKE, SHOW_TOUCHES, DEMO_MODE, ANIMATIONS }

    /** The raw values, null where Android wouldn't say. */
    data class Snapshot(
        val realW: Int, val realH: Int,
        val panelW: Int, val panelH: Int,
        val shapeActive: Boolean,
        val accelerometerRotation: Int?,
        val userRotation: Int?,
        val stayOn: Int?,
        val showTouches: Int?,
        val demoOn: Int?,
        val animationScales: List<Float?>,
    )

    /** One thing that differs from normal, in words for the Settings row. */
    data class Change(val item: Item, val now: String)

    /** Pure: what differs from normal. Unknown values never count as changed. */
    fun detect(s: Snapshot): List<Change> = buildList {
        val real = sorted(s.realW, s.realH)
        val panel = sorted(s.panelW, s.panelH)
        if (s.shapeActive || (real.first > 0 && panel.first > 0 && real != panel)) {
            add(Change(Item.SCREEN, "Now ${real.first}×${real.second}; its own is ${panel.first}×${panel.second}"))
        }
        if (s.accelerometerRotation == 0 && (s.userRotation == 1 || s.userRotation == 3)) {
            add(Change(Item.ROTATION, "Locked sideways"))
        }
        if (s.stayOn != null && s.stayOn != 0) add(Change(Item.STAY_AWAKE, "The screen stays on while charging"))
        if (s.showTouches == 1) add(Change(Item.SHOW_TOUCHES, "Touches show as dots"))
        if (s.demoOn == 1) add(Change(Item.DEMO_MODE, "The status bar is frozen (demo mode)"))
        val off = s.animationScales.filterNotNull().filter { abs(it - 1f) > 0.01f }
        if (off.isNotEmpty()) {
            val same = off.distinct().size == 1 && off.size == s.animationScales.size
            add(Change(Item.ANIMATIONS, when {
                same && off.first() == 0f -> "Animations are off"
                same -> "Animations at ${scaleText(off.first())}"
                else -> "Animation speeds changed"
            }))
        }
    }

    fun scaleText(scale: Float): String = "%.1f×".format(java.util.Locale.US, scale).replace(".0×", "×")

    private fun sorted(a: Int, b: Int) = min(a, b) to max(a, b)

    // ---- Reading and writing on the phone ------------------------------------------------------

    private const val SHOW_TOUCHES = "show_touches"
    private const val DEMO_ON = "sysui_tuner_demo_on"
    private const val DEMO_ALLOWED = "sysui_demo_allowed"
    private val ANIMATION_KEYS = listOf(
        Settings.Global.WINDOW_ANIMATION_SCALE, Settings.Global.TRANSITION_ANIMATION_SCALE, Settings.Global.ANIMATOR_DURATION_SCALE)

    fun canFix(context: Context): Boolean = AdbArming.isPermissionHeld(context)

    fun snapshot(context: Context): Snapshot {
        val cr = context.contentResolver
        val display = context.getSystemService(DisplayManager::class.java)?.getDisplay(Display.DEFAULT_DISPLAY)
        val real = Point()
        @Suppress("DEPRECATION")
        runCatching { display?.getRealSize(real) }
        val mode = runCatching { display?.mode }.getOrNull()
        return Snapshot(
            realW = real.x, realH = real.y,
            panelW = mode?.physicalWidth ?: 0, panelH = mode?.physicalHeight ?: 0,
            shapeActive = app.linc.android.ui.ScreenShape.active(context),
            accelerometerRotation = runCatching { Settings.System.getInt(cr, Settings.System.ACCELEROMETER_ROTATION) }.getOrNull(),
            userRotation = runCatching { Settings.System.getInt(cr, Settings.System.USER_ROTATION) }.getOrNull(),
            stayOn = runCatching { Settings.Global.getInt(cr, Settings.Global.STAY_ON_WHILE_PLUGGED_IN) }.getOrNull(),
            showTouches = runCatching { Settings.System.getInt(cr, SHOW_TOUCHES) }.getOrNull(),
            demoOn = runCatching { Settings.Global.getInt(cr, DEMO_ON) }.getOrNull(),
            animationScales = ANIMATION_KEYS.map { runCatching { Settings.Global.getFloat(cr, it) }.getOrNull() },
        )
    }

    /** Puts [item] back. Blocking (the screen waits up to 4 s for the mirror watcher): call off the main thread. */
    fun fix(context: Context, item: Item): Boolean {
        val cr = context.contentResolver
        val ok = when (item) {
            Item.SCREEN -> fixScreen(context)
            // Upright, keeping whether rotation is locked: that part is the owner's own choice.
            Item.ROTATION -> runLogged("Undo PC changes: rotation") { Settings.System.putInt(cr, Settings.System.USER_ROTATION, 0) }.getOrDefault(false)
            Item.STAY_AWAKE -> runLogged("Undo PC changes: stay awake") { Settings.Global.putInt(cr, Settings.Global.STAY_ON_WHILE_PLUGGED_IN, 0) }.getOrDefault(false)
            Item.SHOW_TOUCHES -> runLogged("Undo PC changes: show touches") { Settings.System.putInt(cr, SHOW_TOUCHES, 0) }.getOrDefault(false)
            // SystemUI leaves demo mode when either goes to 0 (it watches both).
            Item.DEMO_MODE -> runLogged("Undo PC changes: demo mode") {
                Settings.Global.putInt(cr, DEMO_ON, 0) and Settings.Global.putInt(cr, DEMO_ALLOWED, 0)
            }.getOrDefault(false)
            Item.ANIMATIONS -> runLogged("Undo PC changes: animation speed") {
                ANIMATION_KEYS.all { Settings.Global.putFloat(cr, it, 1f) }
            }.getOrDefault(false)
        }
        LogStore.log(if (ok) LogLevel.INFO else LogLevel.WARN,
            "Undo PC changes: ${item.name.lowercase().replace('_', ' ')} ${if (ok) "put back" else "couldn't be put back"}")
        return ok
    }

    private fun fixScreen(context: Context): Boolean {
        val shape = app.linc.android.ui.ScreenShape
        if (shape.active(context) && shape.requestReset(context)) {
            // The mirror watcher puts back the exact earlier size and rotation within a second.
            repeat(8) {
                Thread.sleep(500)
                if (!shape.active(context)) return true
            }
            LogStore.log(LogLevel.INFO, "Undo PC changes: the mirror watcher didn't answer (the phone restarted since?); resetting the size here")
        }
        val reset = runLogged("Undo PC changes: screen size") {
            // The window manager's own `wm size reset`; it asks for WRITE_SECURE_SETTINGS.
            val wmg = Class.forName("android.view.WindowManagerGlobal")
            val wm = wmg.getMethod("getWindowManagerService").invoke(null) ?: error("no window manager")
            Class.forName("android.view.IWindowManager").getMethod("clearForcedDisplaySize", Int::class.javaPrimitiveType)
                .invoke(wm, Display.DEFAULT_DISPLAY)
        }.isSuccess
        if (reset) runCatching { Settings.Global.putString(context.contentResolver, shape.ACTIVE, null) }
        return reset
    }
}
