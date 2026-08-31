package app.linc.android.service

/**
 * The slideshow clicker's key mapping (M4c Part B): a presentation action in, the Windows
 * virtual-key code to send as `pc.input` out. Pure — no Context, no connection — so the Android
 * unit tests cover it directly, the [KeyboardMapping] pattern M4a established.
 *
 * Next and previous reuse [KeyboardMapping]'s existing `VK_RIGHT`/`VK_LEFT` instead of declaring
 * a second copy of those codes. M4a merged two drifting keyboard mappings into one; this must not
 * start a third.
 *
 * **These are key presses to whatever has focus on the PC.** There is no app detection and no
 * PowerPoint integration — nothing here knows what a slide is.
 */
object PresentationKeys {

    const val VK_ESCAPE = 0x1B
    const val VK_SPACE = 0x20
    const val VK_UP = 0x26
    const val VK_DOWN = 0x28
    const val VK_B = 0x42
    const val VK_F5 = 0x74

    enum class Action { Next, Previous, Start, End, Black }

    /**
     * The one key each action sends. Presentation software generally accepts Right, [VK_DOWN] and
     * [VK_SPACE] for "next" and Left and [VK_UP] for "previous"; exactly one may be sent, or a
     * single tap would advance three slides. The alternates are named here as constants so the
     * choice is visible, not so a caller can send them as well.
     */
    fun keyFor(action: Action): Int = when (action) {
        Action.Next -> KeyboardMapping.VK_RIGHT
        Action.Previous -> KeyboardMapping.VK_LEFT
        Action.Start -> VK_F5
        Action.End -> VK_ESCAPE
        Action.Black -> VK_B
    }
}

/**
 * Sends a presentation action to the PC as a real key press over `pc.input` (v14), down then up —
 * the same shape MirrorActivity's keyboard uses, through the same [MirrorControl.key].
 *
 * **No mirror session is needed.** `pc.input` gates on the negotiated protocol version alone
 * (`requiredVersion = 14`) and references no streaming flag and no channel 5, which is what M4a
 * proved when it put a keyboard and trackpad on the Tools page with no stream open.
 */
object PresentationControl {

    /** True when a v14 desktop is connected and a slide key can be sent right now. */
    fun ready(): Boolean = MirrorControl.ready()

    fun send(action: PresentationKeys.Action) {
        val key = PresentationKeys.keyFor(action)
        MirrorControl.key(key, down = true)
        MirrorControl.key(key, down = false)
    }
}
