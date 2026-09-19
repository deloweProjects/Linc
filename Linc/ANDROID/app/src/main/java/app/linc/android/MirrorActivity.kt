package app.linc.android

import android.annotation.SuppressLint
import android.graphics.Color
import android.graphics.Matrix
import android.graphics.SurfaceTexture
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.view.Gravity
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
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.lifecycleScope
import app.linc.android.service.KeyboardSink
import app.linc.android.service.MirrorControl
import app.linc.android.service.MirrorReceiver
import app.linc.android.service.MirrorSettings
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch

/**
 * The PC Remote view (M05, spacedesk-style): the PC's screen fullscreen in landscape,
 * pinch-to-zoom, touch as mouse, and the soft keyboard raised whenever the PC reports a
 * focused text field (`pc.textfocus`).
 *
 * **Zoom is view-level only** — a Matrix on the TextureView, decoupled from the PC (the
 * stream itself never changes). Touch is mapped through the INVERSE of that matrix, so a tap
 * lands on the PC pixel you see, zoomed or not. One finger = mouse (tap = click, drag =
 * drag); two fingers = pinch-zoom and pan.
 *
 * **Keyboard**: a zero-size EditText holds IME focus. Printable characters go to the PC as
 * `pc.input text`; backspace/enter/arrows go as Windows virtual-key codes. Raised
 * automatically on `pc.textfocus`, and manually via the keyboard button.
 *
 * The activity retries `pc.mirror.start` on a backoff until frames flow, so a link that
 * was mid-reconnect when the screen opened heals by itself instead of hanging on "loading".
 */
class MirrorActivity : ComponentActivity() {

    private lateinit var video: TextureView
    private lateinit var loading: View
    private lateinit var loadingText: TextView
    private lateinit var imeSink: EditText
    private lateinit var toolbar: View
    private var keyboardUp = false

    private var surface: Surface? = null
    private val matrix = Matrix()
    private val inverse = Matrix()
    private var scale = 1f
    private var panX = 0f
    private var panY = 0f

    private var streamWidth = 0
    private var streamHeight = 0

    private lateinit var scaleDetector: ScaleGestureDetector
    private var dragging = false
    private var downX = 0f
    private var downY = 0f
    private var suppressMouseUntilUp = false

    @SuppressLint("ClickableViewAccessibility")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Fullscreen immersive landscape; keep the screen on while remoting.
        WindowCompat.setDecorFitsSystemWindows(window, false)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        WindowInsetsControllerCompat(window, window.decorView).apply {
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }

        val root = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }

        video = TextureView(this)
        root.addView(video, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))

        // Invisible 1x1 EditText that owns IME focus; its keys are forwarded to the PC.
        imeSink = object : EditText(this) {
            override fun onKeyPreIme(keyCode: Int, event: KeyEvent): Boolean {
                if (keyCode == KeyEvent.KEYCODE_BACK && event.action == KeyEvent.ACTION_UP) {
                    hideKeyboard()
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
        wireImeSink()

        loadingText = TextView(this).apply {
            text = "Loading your PC…"
            setTextColor(Color.WHITE)
            textSize = 18f
        }
        loading = FrameLayout(this).apply {
            addView(ProgressBar(this@MirrorActivity), FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.CENTER).apply { bottomMargin = 120 })
            addView(loadingText, FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.CENTER).apply { topMargin = 120 })
        }
        root.addView(loading, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))

        // Floating tool bar over the mirror: keyboard, Windows key, Esc, Alt+Tab, End. Topmost so
        // its buttons always take the tap; shown only while streaming. Pinned top-centre.
        toolbar = buildToolbar().apply { visibility = View.GONE }
        root.addView(toolbar, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT,
            Gravity.TOP or Gravity.CENTER_HORIZONTAL).apply { topMargin = 24 })

        setContentView(root)

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

            override fun onScaleBegin(detector: ScaleGestureDetector): Boolean {
                suppressMouseUntilUp = true   // a pinch is never a click
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

        // Reflect stream state: hide the loading overlay when frames flow, letterbox the
        // video to the stream's aspect, and keep nudging the PC until it streams.
        lifecycleScope.launch {
            MirrorReceiver.state.collectLatest { state ->
                if (state.streaming) {
                    streamWidth = state.width
                    streamHeight = state.height
                    loading.visibility = View.GONE
                    toolbar.visibility = View.VISIBLE
                    applyTransform()
                } else {
                    loading.visibility = View.VISIBLE
                    toolbar.visibility = View.GONE
                }
            }
        }
        lifecycleScope.launch {
            MirrorReceiver.textFocus.collectLatest { focused ->
                if (focused) showKeyboard() else hideKeyboard()
            }
        }
        lifecycleScope.launch {
            // Self-heal, with room for a slow start. Bringing the stream up is not instant:
            // the PC builds a capture and a hardware encoder, then the phone dials a second
            // TLS connection back for the video channel — over Wi-Fi that regularly takes
            // longer than the 3 s this used to wait, so the retry landed on a start that was
            // nearly done and the two fought each other forever. Ask again, but back off, so
            // a slow link is given time to finish instead of being interrupted.
            var wait = FIRST_RETRY_MS
            while (true) {
                delay(wait)
                if (MirrorReceiver.state.value.streaming) {
                    wait = FIRST_RETRY_MS   // healthy again; be quick if it drops later
                    continue
                }
                if (surface == null) continue
                loadingText.text = if (MirrorControl.ready()) "Loading your PC…"
                    else "Waiting for the PC connection…"
                startMirror()
                wait = (wait * 2).coerceAtMost(MAX_RETRY_MS)
            }
        }
    }

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
    }

    /** One finger = mouse; two fingers = zoom/pan. Coordinates go through the inverse matrix. */
    private fun onVideoTouch(event: MotionEvent): Boolean {
        scaleDetector.onTouchEvent(event)

        when (event.actionMasked) {
            MotionEvent.ACTION_POINTER_DOWN -> suppressMouseUntilUp = true
            MotionEvent.ACTION_MOVE -> if (event.pointerCount >= 2) {
                // Two-finger drag pans when zoomed (the ScaleDetector handles the zoom part).
                if (scale > 1f && !scaleDetector.isInProgress) {
                    panX += event.getX(0) - downX
                    panY += event.getY(0) - downY
                    applyTransform()
                }
                downX = event.getX(0); downY = event.getY(0)
                return true
            }
        }

        if (event.pointerCount == 1 && !suppressMouseUntilUp) {
            val (nx, ny) = toVideoNormalized(event.x, event.y) ?: return true
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    downX = event.x; downY = event.y; dragging = true
                    MirrorControl.pointer("down", nx, ny, "touch")
                }
                MotionEvent.ACTION_MOVE -> if (dragging) MirrorControl.pointer("move", nx, ny, "touch")
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    dragging = false
                    MirrorControl.pointer("up", nx, ny, "touch")
                }
            }
        }

        if (event.actionMasked == MotionEvent.ACTION_UP || event.actionMasked == MotionEvent.ACTION_CANCEL) {
            suppressMouseUntilUp = false
            dragging = false
            downX = event.x; downY = event.y
        }
        if (event.actionMasked == MotionEvent.ACTION_DOWN) { downX = event.x; downY = event.y }
        return true
    }

    /** View pixel -> 0..65535 on the video, through the current zoom/pan. Null outside it. */
    private fun toVideoNormalized(x: Float, y: Float): Pair<Int, Int>? {
        if (video.width == 0 || video.height == 0) return null
        val point = floatArrayOf(x, y)
        inverse.mapPoints(point)
        // After inversion the point is in view-stretched stream space (0..viewW / 0..viewH).
        val nx = (point[0] / video.width * 65535f).toInt()
        val ny = (point[1] / video.height * 65535f).toInt()
        if (nx !in 0..65535 || ny !in 0..65535) return null
        return nx to ny
    }

    // ---- keyboard ------------------------------------------------------------

    // Key-vs-text mapping and the sink wiring itself now live in the shared KeyboardMapping /
    // KeyboardSink (M4a), so the Tools page's keyboard uses exactly the same logic.
    private fun wireImeSink() = KeyboardSink.wire(imeSink)

    private fun showKeyboard() {
        keyboardUp = true
        imeSink.requestFocus()
        (getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager)
            .showSoftInput(imeSink, InputMethodManager.SHOW_IMPLICIT)
    }

    private fun hideKeyboard() {
        keyboardUp = false
        (getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager)
            .hideSoftInputFromWindow(imeSink.windowToken, 0)
    }

    /** Ask the PC to stream at the user's chosen quality (MirrorSettings). */
    private fun startMirror() {
        val q = MirrorSettings.get(this)
        MirrorControl.start(fps = q.fps, bitrate = q.bitrate)
    }

    // ---- floating tool bar ---------------------------------------------------

    /** Send a virtual-key as a quick press (down then up). */
    private fun tapKey(virtualKey: Int) {
        MirrorControl.key(virtualKey, down = true)
        MirrorControl.key(virtualKey, down = false)
    }

    /** Alt+Tab: hold Alt, press Tab once, release Alt — switches to the previous PC window. */
    private fun altTab() {
        MirrorControl.key(0x12, down = true)  // VK_MENU (Alt)
        MirrorControl.key(0x09, down = true)  // VK_TAB
        MirrorControl.key(0x09, down = false)
        MirrorControl.key(0x12, down = false)
    }

    private fun buildToolbar(): View {
        fun toolButton(label: String, onClick: () -> Unit) = TextView(this).apply {
            text = label
            setTextColor(Color.WHITE)
            textSize = 13f
            isClickable = true
            isFocusable = false
            setPadding(30, 22, 30, 22)
            setOnClickListener { onClick() }
        }
        return LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            // Consume touches on the bar itself so they don't leak through as a PC click.
            isClickable = true
            background = GradientDrawable().apply {
                cornerRadius = 60f
                setColor(Color.argb(170, 24, 24, 24))
            }
            addView(toolButton("⌨ Keyboard") { if (keyboardUp) hideKeyboard() else showKeyboard() })
            addView(toolButton("⊞ Win") { tapKey(0x5B) })   // VK_LWIN
            addView(toolButton("Esc") { tapKey(0x1B) })      // VK_ESCAPE
            addView(toolButton("Alt+Tab") { altTab() })
            addView(toolButton("✕ End") { finish() })
        }
    }

    private companion object {
        /** First self-heal retry; doubles up to [MAX_RETRY_MS] while the stream stays down. */
        const val FIRST_RETRY_MS = 5_000L
        const val MAX_RETRY_MS = 20_000L
    }

    override fun onDestroy() {
        super.onDestroy()
        MirrorControl.stop()
        MirrorReceiver.detachSurface()
    }
}
