package app.linc.android

import android.Manifest
import android.annotation.SuppressLint
import android.app.AlertDialog
import android.content.Intent
import android.graphics.Color
import android.graphics.Matrix
import android.graphics.SurfaceTexture
import android.net.wifi.WifiManager
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.speech.RecognitionListener
import android.speech.RecognizerIntent
import android.speech.SpeechRecognizer
import android.view.Gravity
import android.view.HapticFeedbackConstants
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.ScaleGestureDetector
import android.view.Surface
import android.view.TextureView
import android.view.View
import android.view.WindowManager
import android.view.inputmethod.InputMethodManager
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.ProgressBar
import android.widget.TextView
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.ui.platform.ComposeView
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.lifecycleScope
import app.linc.android.service.AudioReceiver
import app.linc.android.service.KeyboardSink
import app.linc.android.service.LinkStats
import app.linc.android.service.MirrorControl
import app.linc.android.service.MirrorReceiver
import app.linc.android.service.MirrorSession
import app.linc.android.service.MirrorSettings
import app.linc.android.service.MirrorSettings.PointerMode
import app.linc.android.service.ModifierKeys
import app.linc.android.service.PcTabletMode
import app.linc.android.service.PcVoiceAccess
import app.linc.android.service.PointerGestures
import app.linc.android.ui.PcRemoteOverlay
import app.linc.android.ui.RemoteActions
import app.linc.android.ui.RemoteUiState
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch

/**
 * The PC Remote view (M05, spacedesk-style; Windows-App features D-067, D-069, D-070): the PC's
 * screen fullscreen in landscape, pinch-to-zoom, and the PC driven by touch.
 *
 * **Zoom is view-level only** — a Matrix on the TextureView, decoupled from the PC (the
 * stream itself never changes). Touch is mapped through the INVERSE of that matrix, so a tap
 * lands on the PC pixel you see, zoomed or not.
 *
 * **After Windows App:**
 *  - the PC's pointer is drawn here ([CursorOverlay]) from `pc.mirror.cursor`, and moves the
 *    moment a finger does instead of a video-round-trip later;
 *  - two pointer modes ([PointerGestures]): Touch and Mouse (a trackpad); press-and-hold or a
 *    two-finger tap is a right click; two fingers scroll;
 *  - a Material 3 Expressive status bubble ([PcRemoteOverlay]) — its icon is the connection —
 *    opening a panel with ping, frame rate, speed and every control;
 *  - a floating PC keyboard ([FloatingKeyboard]) with voice typing, or the phone's own keyboard,
 *    raised by itself on PC text fields unless that is switched off;
 *  - Windows Voice Access, hearing through this phone's microphone ([PcVoiceAccess]);
 *  - a dropped link shows "Reconnecting…" over the last picture and heals by itself.
 */
class MirrorActivity : ComponentActivity() {

    private lateinit var root: FrameLayout
    private lateinit var video: TextureView
    private lateinit var cursorView: CursorOverlay
    private lateinit var loading: View
    private lateinit var loadingText: TextView
    private lateinit var imeSink: EditText
    private lateinit var keyboard: FloatingKeyboard

    private val ui = RemoteUiState()
    private var phoneKeyboardUp = false
    private var keyboardAutoShown = false
    private var lastSetupStage = 0
    private var recognizer: SpeechRecognizer? = null
    private var afterMicGranted: (() -> Unit)? = null
    private val askMic = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        val next = afterMicGranted
        afterMicGranted = null
        if (granted) next?.invoke() else toast("Linc needs the microphone for that.")
    }

    private var surface: Surface? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private val matrix = Matrix()
    private val inverse = Matrix()
    private var scale = 1f
    private var panX = 0f
    private var panY = 0f

    private var streamWidth = 0
    private var streamHeight = 0
    private var everStreamed = false
    private var displayId = 0

    private lateinit var scaleDetector: ScaleGestureDetector
    private lateinit var gestures: PointerGestures
    private val handler = Handler(Looper.getMainLooper())
    private var lastLocalInputMs = 0L
    private var scrollAccX = 0f
    private var scrollAccY = 0f
    private var lastScrollSentMs = 0L
    private val closePanel = Runnable { ui.panelOpen = false }

    @SuppressLint("ClickableViewAccessibility")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Fullscreen immersive landscape; keep the screen on while remoting.
        WindowCompat.setDecorFitsSystemWindows(window, false)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        window.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_NOTHING)
        WindowInsetsControllerCompat(window, window.decorView).apply {
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }

        root = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }

        video = TextureView(this)
        root.addView(video, matchParent())

        cursorView = CursorOverlay(this)
        root.addView(cursorView, matchParent())

        // Invisible 1x1 EditText that owns IME focus; its keys are forwarded to the PC.
        imeSink = object : EditText(this) {
            override fun onKeyPreIme(keyCode: Int, event: KeyEvent): Boolean {
                if (keyCode == KeyEvent.KEYCODE_BACK && event.action == KeyEvent.ACTION_UP) {
                    hidePhoneKeyboard()
                    return true
                }
                return super.onKeyPreIme(keyCode, event)
            }
        }.apply {
            layoutParams = FrameLayout.LayoutParams(1, 1)
            alpha = 0f
            isFocusable = true
            isFocusableInTouchMode = true
        }
        root.addView(imeSink)
        KeyboardSink.wire(imeSink)

        loadingText = TextView(this).apply {
            text = "Loading your PC…"
            setTextColor(Color.WHITE)
            textSize = 18f
        }
        loading = FrameLayout(this).apply {
            setBackgroundColor(Color.BLACK)
            addView(ProgressBar(this@MirrorActivity), FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.CENTER).apply { bottomMargin = 120 })
            addView(loadingText, FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.CENTER).apply { topMargin = 120 })
            // Swallow touches while loading so nothing reaches a stale picture underneath.
            isClickable = true
        }
        root.addView(loading, matchParent())

        keyboard = FloatingKeyboard(this, onHide = { hideFloatingKeyboard() }, onVoice = { toggleDictation() })
            .apply { visibility = View.GONE }
        root.addView(keyboard, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT))

        // The status bubble and its panel, in Compose (Material 3 Expressive). It covers the
        // screen but only the bubble and panel take touches; the rest falls through to the PC.
        val prefs = getPreferences(MODE_PRIVATE)
        ui.bubbleRight = prefs.getBoolean(PREF_BUBBLE_RIGHT, true)
        ui.bubbleY = prefs.getFloat(PREF_BUBBLE_Y, 0.08f)
        root.addView(ComposeView(this).apply { setContent { PcRemoteOverlay(ui, actions) } }, matchParent())

        setContentView(root)

        gestures = PointerGestures(
            out = object : PointerGestures.Output {
                override fun pointerTo(x: Float, y: Float) {
                    markLocalInput()
                    drawCursor()
                    followCursor()
                    toNormalized(x, y)?.let { (nx, ny) -> MirrorControl.pointer("move", nx, ny, "touch") }
                }

                override fun button(button: String, down: Boolean) {
                    markLocalInput()
                    val (nx, ny) = toNormalized(gestures.cursorX, gestures.cursorY) ?: return
                    if (down) {
                        // Feedback now, not when the PC's next frame arrives.
                        val (vx, vy) = streamToView(gestures.cursorX, gestures.cursorY).let { it[0] to it[1] }
                        cursorView.pulse(button, vx, vy)
                        video.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                    }
                    MirrorControl.pointer(if (down) "down" else "up", nx, ny, "touch", button)
                }

                override fun twoFinger(dx: Float, dy: Float) = onTwoFingerDrag(dx, dy)

                override fun held() {
                    video.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
                }
            },
            mode = MirrorSettings.pointerMode(this),
            toStream = ::viewToStream,
            viewPerStream = ::viewPerStream,
            streamSize = { streamWidth to streamHeight },
        )
        refreshUi()

        scaleDetector = ScaleGestureDetector(this, object : ScaleGestureDetector.SimpleOnScaleGestureListener() {
            override fun onScale(detector: ScaleGestureDetector): Boolean {
                val previous = scale
                scale = (scale * detector.scaleFactor).coerceIn(1f, 6f)
                // Zoom around the pinch focus so the point under your fingers stays put.
                val factor = scale / previous
                panX = detector.focusX - factor * (detector.focusX - panX)
                panY = detector.focusY - factor * (detector.focusY - panY)
                applyTransform()
                return true
            }
        })

        video.surfaceTextureListener = object : TextureView.SurfaceTextureListener {
            override fun onSurfaceTextureAvailable(texture: SurfaceTexture, w: Int, h: Int) {
                surface = Surface(texture).also { MirrorReceiver.attachSurface(it) }
                startMirror()
            }

            override fun onSurfaceTextureSizeChanged(texture: SurfaceTexture, w: Int, h: Int) {
                applyTransform()
                if (keyboard.visibility == View.VISIBLE) keyboard.place()
            }

            override fun onSurfaceTextureDestroyed(texture: SurfaceTexture): Boolean {
                MirrorControl.stop()
                MirrorReceiver.detachSurface()
                surface?.release()
                surface = null
                return true
            }

            override fun onSurfaceTextureUpdated(texture: SurfaceTexture) = Unit
        }

        video.setOnTouchListener { _, event -> onVideoTouch(event) }

        observe()
        MirrorControl.requestDisplays()
        PcVoiceAccess.refresh(this)
    }

    // ---- the panel's actions --------------------------------------------------------

    private val actions = object : RemoteActions {
        override fun setMouseMode(mouse: Boolean) {
            val mode = if (mouse) PointerMode.MOUSE else PointerMode.TOUCH
            gestures.mode = mode
            MirrorSettings.setPointerMode(this@MirrorActivity, mode)
            drawCursor()
            touched()
        }

        override fun floatingKeyboard() {
            MirrorSettings.setFloatingKeyboard(this@MirrorActivity, true)
            if (keyboard.visibility == View.VISIBLE) hideFloatingKeyboard()
            else { showFloatingKeyboard(); ui.panelOpen = false }
        }

        override fun phoneKeyboard() {
            MirrorSettings.setFloatingKeyboard(this@MirrorActivity, false)
            if (phoneKeyboardUp) hidePhoneKeyboard()
            else { showPhoneKeyboard(); ui.panelOpen = false }
        }

        override fun setAutoKeyboard(on: Boolean) {
            MirrorSettings.setAutoKeyboard(this@MirrorActivity, on)
            refreshUi(); touched()
        }

        override fun sound() {
            val on = !MirrorSettings.audio(this@MirrorActivity)
            MirrorSettings.setAudio(this@MirrorActivity, on)
            MirrorControl.audio(on)
            refreshUi(); touched()
        }

        override fun voiceAccess() {
            if (PcVoiceAccess.state.value.settingUp) {
                toast(PcVoiceAccess.state.value.setupText)
                touched()
                return
            }
            if (!PcVoiceAccess.ready() || !PcVoiceAccess.state.value.available) {
                toast(if (!PcVoiceAccess.ready()) "Voice Access needs a newer Linc on the PC."
                      else "This PC doesn't have Windows Voice Access (Windows 11 22H2 or later).")
                touched()
                return
            }
            val on = !PcVoiceAccess.state.value.on
            val phoneMic = MirrorSettings.voicePhoneMic(this@MirrorActivity)
            if (on && phoneMic && !PcVoiceAccess.hasMicPermission(this@MirrorActivity)) {
                afterMicGranted = { startVoiceWithPhoneMic() }
                askMic.launch(Manifest.permission.RECORD_AUDIO)
            } else if (on && phoneMic) {
                startVoiceWithPhoneMic()
            } else {
                PcVoiceAccess.set(this@MirrorActivity, on, phoneMic)
            }
            touched()
        }

        /** Tablet mode (v22, D-074): the PC scales itself up for fingers; put back when you leave. */
        override fun tabletMode() {
            // Shown even to an older PC, so the button isn't mysteriously missing — it says why.
            if (!PcTabletMode.ready()) {
                toast("Tablet mode needs Linc 1.0.0.3 or newer on the PC. Quit Linc on the PC and run the new version.")
                touched()
                return
            }
            PcTabletMode.set(!PcTabletMode.state.value.on)
            touched()
        }

        /**
         * Which microphone the PC hears while you're in PC Remote (D-075). Phone: Voice Access,
         * calls and dictation on the PC all get this phone's mic — streamed only while a PC app
         * is actually listening. Switches Voice Access over live if it's running.
         */
        override fun setPhoneMic(phone: Boolean) {
            if (phone && !PcVoiceAccess.hasMicPermission(this@MirrorActivity)) {
                afterMicGranted = { setPhoneMic(true) }
                askMic.launch(Manifest.permission.RECORD_AUDIO)
                return
            }
            val s = PcVoiceAccess.state.value
            if (phone && !s.cable && s.canSetUp) {
                ui.panelOpen = false
                AlertDialog.Builder(this@MirrorActivity)
                    .setTitle("Set up the microphone on the PC?")
                    .setMessage(PcVoiceAccess.SETUP_EXPLANATION)
                    .setPositiveButton("Set up") { _, _ ->
                        MirrorSettings.setVoicePhoneMic(this@MirrorActivity, true)
                        PcVoiceAccess.setAuto(this@MirrorActivity, true, installCable = true)
                        refreshUi()
                    }
                    .setNegativeButton("Not now", null)
                    .show()
                return
            }
            MirrorSettings.setVoicePhoneMic(this@MirrorActivity, phone)
            if (s.on) PcVoiceAccess.set(this@MirrorActivity, true, phoneMic = phone)
            if (PcVoiceAccess.micReady()) PcVoiceAccess.setAuto(this@MirrorActivity, phone)
            else if (phone && !s.on) toast("Using the phone as the PC's mic outside Voice Access needs Linc 1.0.0.6 or newer on the PC.")
            refreshUi(); touched()
        }

        override fun display() = pickDisplay()
        override fun quality() = pickQuality()
        override fun fit() { scale = 1f; applyTransform(); refreshUi() }
        override fun taskView() = combo(0x5B, 0x09)     // Win+Tab
        override fun showDesktop() = combo(0x5B, 0x44)  // Win+D
        override fun end() = finish()

        override fun bubbleMoved(right: Boolean, y: Float) {
            ui.bubbleRight = right
            ui.bubbleY = y
            getPreferences(MODE_PRIVATE).edit().putBoolean(PREF_BUBBLE_RIGHT, right).putFloat(PREF_BUBBLE_Y, y).apply()
        }
    }

    /**
     * Voice Access through this phone's mic. A PC with no virtual microphone yet can set one up
     * itself (D-071) — explained and agreed to here first, since Windows will then ask on the PC.
     */
    private fun startVoiceWithPhoneMic() {
        if (!PcVoiceAccess.state.value.canSetUp) {
            PcVoiceAccess.set(this, true, phoneMic = true)
            return
        }
        ui.panelOpen = false
        AlertDialog.Builder(this)
            .setTitle("Set up the microphone on the PC?")
            .setMessage(PcVoiceAccess.SETUP_EXPLANATION)
            .setPositiveButton("Set up") { _, _ -> PcVoiceAccess.set(this, true, phoneMic = true, installCable = true) }
            .setNegativeButton("Use the PC's mic") { _, _ -> PcVoiceAccess.set(this, true, phoneMic = false) }
            .show()
    }

    /** Keep the panel open while it is being used; close it after a quiet spell. */
    private fun touched() {
        handler.removeCallbacks(closePanel)
        handler.postDelayed(closePanel, PANEL_IDLE_MS)
    }

    /** Copy settings and session facts into the overlay's state. */
    private fun refreshUi() {
        ui.mouseMode = gestures.mode == PointerMode.MOUSE
        ui.floatingKeyboard = keyboard.visibility == View.VISIBLE
        ui.phoneKeyboard = phoneKeyboardUp
        ui.autoKeyboard = MirrorSettings.autoKeyboard(this)
        ui.soundAvailable = MirrorControl.v20()
        ui.soundOn = MirrorSettings.audio(this)
        // Always shown, in fixed places: a button that appeared a moment after each reconnect used
        // to shift the panel under the finger, so a tap meant for Tablet mode hit Voice Access.
        ui.voiceAccessAvailable = true
        ui.voiceAccessOn = PcVoiceAccess.state.value.on
        ui.tabletAvailable = true
        ui.phoneMic = MirrorSettings.voicePhoneMic(this)
        ui.micLive = PcVoiceAccess.micLive.value
        ui.tabletOn = PcTabletMode.state.value.on
        ui.displays = MirrorSession.displays.value.size
        ui.zoomed = scale > 1f
        ui.picture = MirrorSettings.get(this).label
    }

    private fun observe() {
        // Reflect stream state. A drop after the picture has been up keeps the last frame on
        // screen under a "Reconnecting…" veil instead of cutting to black — what Windows App
        // does — and the retry loop below heals it.
        lifecycleScope.launch {
            MirrorReceiver.state.collectLatest { state ->
                if (state.streaming) {
                    everStreamed = true
                    streamWidth = state.width
                    streamHeight = state.height
                    loading.visibility = View.GONE
                    ui.visible = true
                    // Until the PC reports its pointer (or forever, from a pre-v20 PC), start it
                    // in the middle so Mouse mode has something to move.
                    if (MirrorSession.cursor.value == null) {
                        gestures.syncCursor(state.width / 2f, state.height / 2f)
                    }
                    applyTransform()
                    if (MirrorSettings.audio(this@MirrorActivity)) MirrorControl.audio(true)
                    PcVoiceAccess.refresh(this@MirrorActivity)
                    PcTabletMode.refresh()
                    // "Phone" chosen: the PC hears this phone whenever an app there listens (D-075).
                    if (PcVoiceAccess.micReady() && MirrorSettings.voicePhoneMic(this@MirrorActivity) &&
                        PcVoiceAccess.hasMicPermission(this@MirrorActivity)) {
                        PcVoiceAccess.setAuto(this@MirrorActivity, true)
                    }
                    refreshUi()
                } else {
                    loading.visibility = View.VISIBLE
                    if (everStreamed) {
                        loading.setBackgroundColor(Color.argb(150, 0, 0, 0))
                        loadingText.text = "Reconnecting…"
                    }
                    ui.visible = false
                    ui.panelOpen = false
                }
            }
        }
        lifecycleScope.launch {
            MirrorReceiver.textFocus.collectLatest { focused ->
                // Raise the preferred keyboard when the PC focuses a text field — unless that is
                // switched off — and only take it away again if it was raised that way.
                if (focused) {
                    if (MirrorSettings.autoKeyboard(this@MirrorActivity) && !keyboardShown()) {
                        showKeyboard(); keyboardAutoShown = true
                    }
                } else if (keyboardAutoShown) {
                    hideFloatingKeyboard(); hidePhoneKeyboard()
                }
            }
        }
        lifecycleScope.launch {
            MirrorSession.cursor.collectLatest { cursor ->
                if (cursor == null) return@collectLatest
                // The PC's report trails a finger by a round trip, so while this phone is
                // driving the pointer it trusts itself; otherwise the PC moved it (its own
                // mouse, a jump to a dialog) and the phone follows.
                if (!gestures.active && SystemClock.uptimeMillis() - lastLocalInputMs > localAuthorityMs()) {
                    gestures.syncCursor(cursor.x.toFloat(), cursor.y.toFloat())
                }
                drawCursor()
            }
        }
        lifecycleScope.launch { ModifierKeys.latches.collectLatest { keyboard.refresh() } }
        lifecycleScope.launch { MirrorSession.displays.collectLatest { refreshUi() } }
        lifecycleScope.launch { AudioReceiver.playing.collectLatest { refreshUi() } }
        lifecycleScope.launch { PcVoiceAccess.micLive.collectLatest { refreshUi() } }
        lifecycleScope.launch {
            PcVoiceAccess.state.collectLatest { state ->
                refreshUi()
                state.error?.let { toast(it) }
                if (state.settingUp) {
                    // One toast when setup starts and one when the installer opens, not every 5 %.
                    val stage = if ((state.progress ?: 100) >= 100) 2 else 1
                    if (stage != lastSetupStage) toast(state.setupText)
                    lastSetupStage = stage
                } else {
                    lastSetupStage = 0
                }
                if (!state.settingUp && state.on && state.mic != "phone" && MirrorSettings.voicePhoneMic(this@MirrorActivity) &&
                    !state.cable && !state.canSetUp) {
                    toast("Voice Access is using the PC's own microphone. Update Linc on the PC so it can set up this phone as its microphone.")
                }
            }
        }
        lifecycleScope.launch {
            var first = true
            PcTabletMode.state.collectLatest { state ->
                refreshUi()
                if (first) { first = false; return@collectLatest }
                state.error?.let { toast("Tablet mode: $it") }
                if (state.error == null && state.on && state.scale != null) {
                    toast("Tablet mode on: the PC is at ${state.scale} % so everything is finger-sized.")
                }
            }
        }
        lifecycleScope.launch { healthLoop() }
        lifecycleScope.launch { retryLoop() }
    }

    /**
     * Once a second: ping the PC, work out what the phone is actually showing, and update the
     * bubble. A picture that has stopped while the link claims to be streaming is nudged with a
     * keyframe and, if it stays stuck, the stream is restarted.
     */
    private suspend fun healthLoop() {
        var lastFrames = MirrorReceiver.framesShown
        var lastBytes = MirrorReceiver.bytesReceived
        var stalledSeconds = 0
        while (true) {
            delay(1_000)
            if (MirrorControl.v20()) MirrorControl.ping(SystemClock.elapsedRealtime())
            if (!MirrorReceiver.state.value.streaming) { stalledSeconds = 0; continue }

            val frames = MirrorReceiver.framesShown
            val bytes = MirrorReceiver.bytesReceived
            val fps = (frames - lastFrames).toInt()
            val kbps = ((bytes - lastBytes) * 8 / 1000).toInt()
            lastFrames = frames; lastBytes = bytes

            stalledSeconds = if (fps == 0) stalledSeconds + 1 else 0
            when (stalledSeconds) {
                2 -> MirrorControl.keyframe()
                STALL_RESTART_S -> {
                    MirrorControl.stop()
                    delay(300)
                    startMirror()
                }
            }
            val rtt = MirrorSession.rttMs.value
            val stats = MirrorSession.stats.value
            ui.stalled = stalledSeconds >= 2
            ui.quality = when {
                stats != null -> stats.quality(rtt).let {
                    // The PC's view plus what actually arrived here.
                    if (fps < 8 && it != LinkStats.Quality.POOR) LinkStats.Quality.FAIR else it
                }
                fps >= 20 -> LinkStats.Quality.GOOD
                fps >= 8 -> LinkStats.Quality.FAIR
                else -> LinkStats.Quality.POOR
            }
            ui.pingMs = rtt
            ui.fps = fps
            ui.mbps = kbps / 1000f
        }
    }

    /**
     * Self-heal, with room for a slow start. Bringing the stream up is not instant: the PC
     * builds a capture and a hardware encoder, then the phone dials a second TLS connection
     * back for the video channel — over Wi-Fi that can take several seconds, so retrying too
     * eagerly lands on a start that was nearly done. After a drop, though, the first retry is
     * quick: the PC side is usually fine and only the channel needs re-dialling.
     */
    private suspend fun retryLoop() {
        var wait = FIRST_RETRY_MS
        while (true) {
            delay(wait)
            if (MirrorReceiver.state.value.streaming) {
                wait = RECONNECT_RETRY_MS   // healthy again; be quick if it drops later
                continue
            }
            if (surface == null) continue
            if (!everStreamed) {
                loadingText.text = if (MirrorControl.ready()) "Loading your PC…" else "Waiting for the PC connection…"
            }
            startMirror()
            wait = (wait * 2).coerceAtMost(MAX_RETRY_MS)
        }
    }

    // ---- geometry ------------------------------------------------------------

    /** Fit the stream inside the view (letterbox), then apply user zoom/pan on top. */
    private fun applyTransform() {
        if (streamWidth == 0 || streamHeight == 0 || video.width == 0) return

        val viewW = video.width.toFloat()
        val viewH = video.height.toFloat()
        val fit = minOf(viewW / streamWidth, viewH / streamHeight)
        val contentW = streamWidth * fit
        val contentH = streamHeight * fit

        // Clamp the pan so the zoomed content never drifts off leaving dead void: the
        // content rect after fit+centre starts at (ox,oy); after zoom about (0,0) and pan,
        // its edges are scale*edge + pan.
        val ox = (viewW - contentW) / 2f
        val oy = (viewH - contentH) / 2f
        if (scale <= 1f) {
            panX = 0f; panY = 0f
        } else {
            panX = panX.coerceIn(viewW - scale * (ox + contentW), -scale * ox)
            panY = panY.coerceIn(viewH - scale * (oy + contentH), -scale * oy)
        }

        // TextureView stretches the stream to the view by default; undo that, then fit+centre.
        matrix.reset()
        matrix.setScale(contentW / viewW, contentH / viewH)
        matrix.postTranslate(ox, oy)
        matrix.postScale(scale, scale)
        matrix.postTranslate(panX, panY)
        video.setTransform(matrix)
        matrix.invert(inverse)
        ui.zoomed = scale > 1f
        drawCursor()
    }

    /** View px per PC px at the current fit and zoom. */
    private fun viewPerStream(): Float {
        if (streamWidth == 0 || video.width == 0) return 1f
        return minOf(video.width.toFloat() / streamWidth, video.height.toFloat() / streamHeight) * scale
    }

    /** View pixel -> PC pixel through the current zoom/pan. Null outside the picture. */
    private fun viewToStream(x: Float, y: Float): Pair<Float, Float>? {
        if (video.width == 0 || streamWidth == 0) return null
        val point = floatArrayOf(x, y)
        inverse.mapPoints(point)
        // After inversion the point is in view-stretched stream space (0..viewW / 0..viewH).
        val sx = point[0] / video.width * streamWidth
        val sy = point[1] / video.height * streamHeight
        if (sx < 0 || sy < 0 || sx > streamWidth || sy > streamHeight) return null
        return sx to sy
    }

    private fun streamToView(sx: Float, sy: Float): FloatArray {
        val point = floatArrayOf(sx / streamWidth * video.width, sy / streamHeight * video.height)
        matrix.mapPoints(point)
        return point
    }

    /** PC pixel -> the 0..65535 normalised coordinates `pc.input` carries. */
    private fun toNormalized(sx: Float, sy: Float): Pair<Int, Int>? {
        if (streamWidth <= 1 || streamHeight <= 1) return null
        val nx = (sx / (streamWidth - 1) * 65535f).toInt().coerceIn(0, 65535)
        val ny = (sy / (streamHeight - 1) * 65535f).toInt().coerceIn(0, 65535)
        return nx to ny
    }

    // ---- the pointer -----------------------------------------------------------

    /**
     * Paint the pointer where this phone believes it is. In Mouse mode it is always drawn —
     * a trackpad with no visible pointer is unusable — using the PC's own image once it has
     * arrived. In Touch mode it follows the PC: hidden when the PC hides it (typing, video).
     */
    private fun drawCursor() {
        if (streamWidth == 0 || video.width == 0 || !MirrorReceiver.state.value.streaming) {
            cursorView.hide(); return
        }
        val pc = MirrorSession.cursor.value
        val mouse = gestures.mode == PointerMode.MOUSE
        val visible = mouse || (pc?.visible == true) || (gestures.active && MirrorControl.v20())
        if (!visible) { cursorView.hide(); return }
        val (vx, vy) = streamToView(gestures.cursorX, gestures.cursorY).let { it[0] to it[1] }
        val image = pc?.let { MirrorSession.shape(it.shapeId) }
        cursorView.show(vx, vy, viewPerStream().coerceAtLeast(1f), image?.bitmap, image?.hotX ?: 0, image?.hotY ?: 0)
    }

    /** Zoomed in Mouse mode, the view pans to keep the pointer in sight, as Windows App does. */
    private fun followCursor() {
        if (scale <= 1f || gestures.mode != PointerMode.MOUSE) return
        val (vx, vy) = streamToView(gestures.cursorX, gestures.cursorY).let { it[0] to it[1] }
        val marginX = video.width * 0.12f
        val marginY = video.height * 0.12f
        var dx = 0f
        var dy = 0f
        if (vx < marginX) dx = marginX - vx else if (vx > video.width - marginX) dx = video.width - marginX - vx
        if (vy < marginY) dy = marginY - vy else if (vy > video.height - marginY) dy = video.height - marginY - vy
        if (dx != 0f || dy != 0f) { panX += dx; panY += dy; applyTransform() }
    }

    // ---- touch ---------------------------------------------------------------

    private fun onVideoTouch(event: MotionEvent): Boolean {
        // A tap outside the open panel just closes it, as a popup should.
        if (ui.panelOpen && event.actionMasked == MotionEvent.ACTION_DOWN) {
            ui.panelOpen = false
            return true
        }
        scaleDetector.onTouchEvent(event)
        val t = event.eventTime
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                gestures.down(event.x, event.y, t)
                handler.postDelayed({ gestures.holdTimeout(t) }, PointerGestures.HOLD_MS)
            }
            MotionEvent.ACTION_POINTER_DOWN -> if (event.pointerCount == 2) {
                val (cx, cy) = centroid(event)
                scrollAccX = 0f; scrollAccY = 0f
                gestures.secondFingerDown(cx, cy, t)
            }
            MotionEvent.ACTION_MOVE -> if (event.pointerCount >= 2) {
                val (cx, cy) = centroid(event)
                gestures.twoFingerMove(cx, cy, pinching = scaleDetector.isInProgress)
            } else {
                gestures.move(event.x, event.y, t)
            }
            MotionEvent.ACTION_POINTER_UP -> gestures.secondFingerUp(t)
            MotionEvent.ACTION_UP -> gestures.up(event.x, event.y, t)
            MotionEvent.ACTION_CANCEL -> gestures.cancel()
        }
        return true
    }

    private fun centroid(event: MotionEvent): Pair<Float, Float> {
        var x = 0f
        var y = 0f
        val n = minOf(event.pointerCount, 2)
        for (i in 0 until n) { x += event.getX(i); y += event.getY(i) }
        return x / n to y / n
    }

    /**
     * Two fingers moving. Zoomed in Touch mode they pan the view (the picture is under your
     * fingers); otherwise they scroll the PC, smoothly, in wheel units (v20 "precise").
     */
    private fun onTwoFingerDrag(dx: Float, dy: Float) {
        if (scale > 1f && gestures.mode == PointerMode.TOUCH) {
            panX += dx; panY += dy
            applyTransform()
            return
        }
        scrollAccX += -dx * WHEEL_PER_PX
        scrollAccY += dy * WHEEL_PER_PX
        val now = SystemClock.uptimeMillis()
        if (now - lastScrollSentMs < SCROLL_INTERVAL_MS) return
        // Without v20 the PC only understands whole notches; keep the remainder either way.
        val unit = if (MirrorControl.v20()) 1 else 120
        val qx = scrollAccX.toInt() / unit * unit
        val qy = scrollAccY.toInt() / unit * unit
        if (qx == 0 && qy == 0) return
        MirrorControl.scrollPrecise(qx, qy)
        scrollAccX -= qx; scrollAccY -= qy
        lastScrollSentMs = now
    }

    // ---- keyboards -------------------------------------------------------------

    private fun keyboardShown() = phoneKeyboardUp || keyboard.visibility == View.VISIBLE

    /** Raise whichever keyboard the user last chose. */
    private fun showKeyboard() {
        if (MirrorSettings.floatingKeyboard(this)) showFloatingKeyboard() else showPhoneKeyboard()
    }

    private fun showFloatingKeyboard() {
        hidePhoneKeyboard()
        keyboard.visibility = View.VISIBLE
        keyboard.place()
        refreshUi()
    }

    private fun hideFloatingKeyboard() {
        keyboardAutoShown = false
        stopDictation()
        keyboard.visibility = View.GONE
        ModifierKeys.clear()
        refreshUi()
    }

    private fun showPhoneKeyboard() {
        hideFloatingKeyboard()
        phoneKeyboardUp = true
        imeSink.requestFocus()
        (getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager)
            .showSoftInput(imeSink, InputMethodManager.SHOW_IMPLICIT)
        refreshUi()
    }

    private fun hidePhoneKeyboard() {
        if (phoneKeyboardUp) keyboardAutoShown = false
        phoneKeyboardUp = false
        (getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager)
            .hideSoftInputFromWindow(imeSink.windowToken, 0)
        if (::gestures.isInitialized) refreshUi()
    }

    // ---- voice typing (the floating keyboard's 🎤 key) ---------------------------------

    /**
     * Speak, and the words are typed on the PC. Uses the phone's own speech recogniser, so it
     * understands whatever languages the phone does; partial results show on the keyboard as
     * you talk, and the final text is typed when you pause.
     */
    private fun toggleDictation() {
        if (recognizer != null) { stopDictation(); return }
        if (!SpeechRecognizer.isRecognitionAvailable(this)) {
            toast("This phone has no speech recognition."); return
        }
        if (!PcVoiceAccess.hasMicPermission(this)) {
            afterMicGranted = { toggleDictation() }
            askMic.launch(Manifest.permission.RECORD_AUDIO)
            return
        }
        val sr = SpeechRecognizer.createSpeechRecognizer(this)
        recognizer = sr
        sr.setRecognitionListener(object : RecognitionListener {
            override fun onReadyForSpeech(params: Bundle?) = keyboard.setListening(true)
            override fun onPartialResults(partial: Bundle?) {
                partial?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull()
                    ?.let { keyboard.setListening(true, it) }
            }
            override fun onResults(results: Bundle?) {
                results?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull()
                    ?.takeIf { it.isNotBlank() }?.let { MirrorControl.text("$it ") }
                stopDictation()
            }
            override fun onError(error: Int) {
                if (error != SpeechRecognizer.ERROR_NO_MATCH && error != SpeechRecognizer.ERROR_SPEECH_TIMEOUT) {
                    toast("Voice typing stopped (error $error).")
                }
                stopDictation()
            }
            override fun onBeginningOfSpeech() = Unit
            override fun onRmsChanged(rmsdB: Float) = Unit
            override fun onBufferReceived(buffer: ByteArray?) = Unit
            override fun onEndOfSpeech() = Unit
            override fun onEvent(eventType: Int, params: Bundle?) = Unit
        })
        sr.startListening(Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).apply {
            putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
            putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true)
        })
        keyboard.setListening(true)
    }

    private fun stopDictation() {
        recognizer?.let { runCatching { it.cancel() }; it.destroy() }
        recognizer = null
        if (::keyboard.isInitialized) keyboard.setListening(false)
    }

    /** Ask the PC to stream at the user's chosen quality (MirrorSettings). */
    private fun startMirror() {
        val q = MirrorSettings.get(this)
        MirrorControl.start(displayId = displayId, fps = q.fps, bitrate = q.bitrate)
    }

    private fun pickDisplay() {
        val displays = MirrorSession.displays.value
        if (displays.isEmpty()) return
        val labels = displays.map {
            "Display ${it.id + 1}${if (it.primary) " (main)" else ""} — ${it.width}×${it.height}"
        }.toTypedArray()
        AlertDialog.Builder(this)
            .setTitle("Show which display?")
            .setSingleChoiceItems(labels, displays.indexOfFirst { it.id == displayId }) { dialog, which ->
                displayId = displays[which].id
                scale = 1f
                startMirror()
                dialog.dismiss()
            }
            .show()
    }

    private fun pickQuality() {
        val options = MirrorSettings.Quality.entries
        val current = MirrorSettings.get(this)
        AlertDialog.Builder(this)
            .setTitle("Picture quality")
            .setSingleChoiceItems(options.map { "${it.label} — ${it.blurb}" }.toTypedArray(),
                options.indexOf(current)) { dialog, which ->
                MirrorSettings.set(this, options[which])
                startMirror()
                refreshUi()
                dialog.dismiss()
            }
            .show()
    }

    /** A fixed combination, independent of the sticky modifiers (Win+Tab, Win+D…). */
    private fun combo(vararg keys: Int) {
        keys.forEach { MirrorControl.key(it, down = true) }
        keys.reversed().forEach { MirrorControl.key(it, down = false) }
    }

    private fun toast(text: String) = Toast.makeText(this, text, Toast.LENGTH_LONG).show()

    // ---- lifecycle -------------------------------------------------------------

    /**
     * Hold a low-latency Wi-Fi lock while the mirror is on screen.
     *
     * With Wi-Fi power save on (Android's default), the radio sleeps between access-point
     * beacons and packets addressed to it wait at the AP until it wakes — typically 100 ms
     * and sometimes several hundred, arriving in bursts. For a file transfer that is noise;
     * for a 30 fps mirror it is exactly "stutters on Wi-Fi, fine on the cable", and it also
     * reads to the PC as a congested link, pulling the bit rate down for no reason.
     * `WIFI_MODE_FULL_LOW_LATENCY` turns power save off, but Android honours it only while
     * the app is in the foreground with the screen on — which is precisely when the mirror
     * is being watched, so it costs no battery the rest of the time. Released in [onPause].
     */
    /**
     * How long the phone keeps trusting its own pointer after the last touch. A fixed 400 ms let
     * a report the PC sent before the finger lifted arrive after the window on a slow link and
     * pull the pointer back to where it had been. So the window covers two round trips (the
     * move out, the report back) with margin, and never less than 400 ms.
     */
    /**
     * The phone just drove the pointer. Once its trust window ends, take the PC's word again —
     * even if no new report comes, since the PC may have moved the pointer itself during the
     * window (a dialog grabbing it) and then left it still, which sends nothing more.
     */
    private fun markLocalInput() {
        lastLocalInputMs = SystemClock.uptimeMillis()
        handler.removeCallbacks(resyncCursor)
        handler.postDelayed(resyncCursor, localAuthorityMs() + 20)
    }

    private val resyncCursor = Runnable {
        val cursor = MirrorSession.cursor.value ?: return@Runnable
        if (gestures.active || SystemClock.uptimeMillis() - lastLocalInputMs <= localAuthorityMs()) return@Runnable
        gestures.syncCursor(cursor.x.toFloat(), cursor.y.toFloat())
        drawCursor()
    }

    private fun localAuthorityMs(): Long {
        val rtt = MirrorSession.rttMs.value
        if (rtt < 0) return LOCAL_AUTHORITY_MS
        return (rtt * 2L + 150L).coerceIn(LOCAL_AUTHORITY_MS, LOCAL_AUTHORITY_MAX_MS)
    }

    override fun onResume() {
        super.onResume()
        if (wifiLock == null) {
            wifiLock = runCatching {
                (applicationContext.getSystemService(WIFI_SERVICE) as WifiManager)
                    .createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "linc:pc-remote")
                    .apply { setReferenceCounted(false) }
            }.getOrNull()
        }
        runCatching { wifiLock?.acquire() }
    }

    override fun onPause() {
        runCatching { wifiLock?.release() }
        stopDictation()
        super.onPause()
    }

    override fun onDestroy() {
        super.onDestroy()
        // Leaving PC Remote ends the automatic phone mic; the PC puts its own default back (D-075).
        if (PcVoiceAccess.micReady()) PcVoiceAccess.setAuto(this, false)
        // Tablet mode is for PC Remote: leaving puts the PC's own scale back (D-074).
        if (PcTabletMode.ready() && PcTabletMode.state.value.on) PcTabletMode.set(false)
        handler.removeCallbacksAndMessages(null)
        ModifierKeys.clear()
        MirrorControl.audio(false)
        MirrorControl.stop()
        MirrorReceiver.detachSurface()
    }

    private fun matchParent() = FrameLayout.LayoutParams(
        FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT)

    private companion object {
        /** First self-heal retry; doubles up to [MAX_RETRY_MS] while the stream stays down. */
        const val FIRST_RETRY_MS = 5_000L
        /** First retry after a stream that was working drops: the PC side is usually still fine. */
        const val RECONNECT_RETRY_MS = 1_500L
        const val MAX_RETRY_MS = 20_000L
        /** Seconds with no new frame before the stream is torn down and asked for again. */
        const val STALL_RESTART_S = 8
        /** After the phone moves the pointer, the PC's (older) reports are ignored this long. */
        const val LOCAL_AUTHORITY_MS = 400L
        const val LOCAL_AUTHORITY_MAX_MS = 1_500L
        const val PANEL_IDLE_MS = 10_000L
        /** Two-finger scroll: wheel units per view pixel (120 = one notch per ~50 px). */
        const val WHEEL_PER_PX = 2.4f
        const val SCROLL_INTERVAL_MS = 30L
        const val PREF_BUBBLE_Y = "bubble_y"
        const val PREF_BUBBLE_RIGHT = "bubble_right"
    }
}
