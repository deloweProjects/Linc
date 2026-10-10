package app.linc.android

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.View
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import app.linc.android.service.MirrorControl
import app.linc.android.service.ModifierKeys
import app.linc.android.service.PcKeyboardLayout
import app.linc.android.service.PcKeyboardLayout.Action

/**
 * A full PC keyboard floating over PC Remote (v21, D-068): drag it by its handle, resize it
 * with − / +, hide it with ✕ or its ⌄ key. Remembers where it was.
 *
 * Unlike the phone's own keyboard it has everything a PC needs — Esc, Tab, Ctrl/Alt/Win/Shift
 * (sticky: tap for the next key, tap twice to lock), arrows, Del, and an Fn layer with F1–F12 —
 * and it never covers the whole screen. Several fingers can type at once; ⌫, Del and the arrows
 * repeat while held.
 */
@SuppressLint("ViewConstructor")
class FloatingKeyboard(
    context: Context,
    private val onHide: () -> Unit,
    private val onVoice: () -> Unit,
) : LinearLayout(context) {

    private val keys = KeysView(context)
    private val prefs = context.getSharedPreferences("linc", Context.MODE_PRIVATE)
    private var widthFraction = prefs.getFloat(KEY_WIDTH, 0.62f)
    private var dragStartX = 0f
    private var dragStartY = 0f
    private val status = TextView(context).apply {
        setTextColor(Color.rgb(150, 200, 255))
        textSize = 13f
        maxLines = 1
        gravity = Gravity.CENTER
        visibility = View.GONE
    }

    init {
        orientation = VERTICAL
        isClickable = true // touches never fall through to the PC
        background = GradientDrawable().apply {
            cornerRadius = 36f
            setColor(Color.argb(235, 28, 28, 32))
            setStroke(2, Color.argb(90, 255, 255, 255))
        }
        setPadding(14, 6, 14, 14)
        elevation = 24f

        val handle = LinearLayout(context).apply {
            orientation = HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            addView(chip("−") { resize(-0.08f) })
            addView(chip("+") { resize(+0.08f) })
            addView(FrameLayout(context).apply {
                // The grip: a short rounded bar, centred, that you drag the keyboard by. While
                // voice typing it gives way to what is being heard.
                addView(View(context).apply {
                    background = GradientDrawable().apply { cornerRadius = 8f; setColor(Color.argb(140, 255, 255, 255)) }
                }, FrameLayout.LayoutParams(160, 10, Gravity.CENTER))
                addView(status, FrameLayout.LayoutParams(
                    FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))
            }, LayoutParams(0, LayoutParams.MATCH_PARENT, 1f))
            addView(chip("✕") { onHide() })
            setOnTouchListener { _, e -> drag(e) }
        }
        addView(handle, LayoutParams(LayoutParams.MATCH_PARENT, 72))
        addView(keys, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT))
        keys.onHide = onHide
        keys.onVoice = onVoice
    }

    /** Voice typing state: [listening] lights the 🎤 key; [heard] shows the words so far. */
    fun setListening(listening: Boolean, heard: String = "") {
        keys.listening = listening
        status.visibility = if (listening || heard.isNotEmpty()) View.VISIBLE else View.GONE
        status.text = when {
            heard.isNotEmpty() -> heard
            listening -> "Listening…"
            else -> ""
        }
        keys.invalidate()
    }

    /** Size and place the keyboard inside its parent; call when shown and on parent resize. */
    fun place() {
        val parent = parent as? View ?: return
        if (parent.width == 0) { post { place() }; return }
        val w = (parent.width * widthFraction).toInt()
        layoutParams = (layoutParams as FrameLayout.LayoutParams).apply { width = w }
        requestLayout()
        post {
            val maxX = (parent.width - width).toFloat().coerceAtLeast(0f)
            val maxY = (parent.height - height).toFloat().coerceAtLeast(0f)
            translationX = (prefs.getFloat(KEY_X, 0.5f) * maxX).coerceIn(0f, maxX)
            translationY = (prefs.getFloat(KEY_Y, 1f) * maxY).coerceIn(0f, maxY)
        }
    }

    private fun resize(delta: Float) {
        widthFraction = (widthFraction + delta).coerceIn(0.4f, 1f)
        prefs.edit().putFloat(KEY_WIDTH, widthFraction).apply()
        place()
    }

    private fun drag(e: MotionEvent): Boolean {
        val parent = parent as? View ?: return false
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN -> { dragStartX = e.rawX - translationX; dragStartY = e.rawY - translationY }
            MotionEvent.ACTION_MOVE -> {
                val maxX = (parent.width - width).toFloat().coerceAtLeast(0f)
                val maxY = (parent.height - height).toFloat().coerceAtLeast(0f)
                translationX = (e.rawX - dragStartX).coerceIn(0f, maxX)
                translationY = (e.rawY - dragStartY).coerceIn(0f, maxY)
            }
            MotionEvent.ACTION_UP -> {
                val maxX = (parent.width - width).toFloat().coerceAtLeast(1f)
                val maxY = (parent.height - height).toFloat().coerceAtLeast(1f)
                prefs.edit().putFloat(KEY_X, translationX / maxX).putFloat(KEY_Y, translationY / maxY).apply()
            }
        }
        return true
    }

    private fun chip(label: String, onClick: () -> Unit) = TextView(context).apply {
        text = label
        setTextColor(Color.WHITE)
        textSize = 15f
        gravity = Gravity.CENTER
        setPadding(26, 0, 26, 0)
        setOnClickListener { onClick() }
    }

    /** Repaint modifier highlights (sticky Ctrl/Alt/Shift/Win changed). */
    fun refresh() = keys.invalidate()

    /** The keys themselves, drawn on a canvas: fast, and exactly the same at every size. */
    private class KeysView(context: Context) : View(context) {
        var onHide: () -> Unit = {}
        var onVoice: () -> Unit = {}
        var listening = false
        private var fn = false
        private var rows = PcKeyboardLayout.rows(fn)
        private val rects = mutableListOf<Pair<RectF, PcKeyboardLayout.KeyCap>>()
        private val pressed = mutableMapOf<Int, PcKeyboardLayout.KeyCap>()  // pointer id -> key
        private val handler = Handler(Looper.getMainLooper())
        private var repeating: Runnable? = null

        private val keyPaint = Paint(Paint.ANTI_ALIAS_FLAG)
        private val textPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = Color.WHITE; textAlign = Paint.Align.CENTER; typeface = Typeface.DEFAULT
        }
        private val subPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = Color.argb(150, 255, 255, 255); textAlign = Paint.Align.RIGHT
        }

        override fun onMeasure(widthSpec: Int, heightSpec: Int) {
            val w = MeasureSpec.getSize(widthSpec)
            val unit = w / PcKeyboardLayout.ROW_UNITS
            setMeasuredDimension(w, (unit * 0.95f * rows.size).toInt())
        }

        override fun onDraw(canvas: Canvas) {
            rects.clear()
            val unit = width / PcKeyboardLayout.ROW_UNITS
            val rowH = height.toFloat() / rows.size
            val gap = unit * 0.07f
            textPaint.textSize = rowH * 0.36f
            subPaint.textSize = rowH * 0.22f
            val latches = ModifierKeys.latches.value
            rows.forEachIndexed { r, row ->
                var x = 0f
                for (cap in row) {
                    val rect = RectF(x + gap, r * rowH + gap, x + cap.width * unit - gap, (r + 1) * rowH - gap)
                    rects += rect to cap
                    keyPaint.color = colourFor(cap, latches)
                    canvas.drawRoundRect(rect, unit * 0.14f, unit * 0.14f, keyPaint)
                    val shifted = (latches[ModifierKeys.VK_SHIFT] ?: ModifierKeys.Latch.OFF) != ModifierKeys.Latch.OFF
                    val label = when (val a = cap.action) {
                        is Action.Char -> if (shifted) a.shifted.toString() else cap.label
                        else -> cap.label
                    }
                    canvas.drawText(label, rect.centerX(), rect.centerY() - (textPaint.ascent() + textPaint.descent()) / 2, textPaint)
                    if (cap.shiftLabel != null && !shifted) {
                        canvas.drawText(cap.shiftLabel, rect.right - gap * 2, rect.top + subPaint.textSize + gap, subPaint)
                    }
                    x += cap.width * unit
                }
            }
        }

        private fun colourFor(cap: PcKeyboardLayout.KeyCap, latches: Map<Int, ModifierKeys.Latch>): Int {
            if (pressed.values.any { it === cap }) return Color.rgb(90, 90, 100)
            return when (val a = cap.action) {
                is Action.Modifier -> when (latches[a.vk] ?: ModifierKeys.Latch.OFF) {
                    ModifierKeys.Latch.OFF -> Color.rgb(44, 44, 50)
                    ModifierKeys.Latch.ONCE -> Color.rgb(30, 110, 200)
                    ModifierKeys.Latch.LOCKED -> Color.rgb(200, 120, 20)
                }
                is Action.Fn -> if (fn) Color.rgb(30, 110, 200) else Color.rgb(44, 44, 50)
                is Action.Voice -> if (listening) Color.rgb(200, 60, 70) else Color.rgb(44, 44, 50)
                is Action.Char -> Color.rgb(64, 64, 72)
                else -> Color.rgb(44, 44, 50)
            }
        }

        private fun keyAt(x: Float, y: Float) = rects.firstOrNull { it.first.contains(x, y) }?.second

        @SuppressLint("ClickableViewAccessibility")
        override fun onTouchEvent(e: MotionEvent): Boolean {
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN -> {
                    val i = e.actionIndex
                    val cap = keyAt(e.getX(i), e.getY(i)) ?: return true
                    pressed[e.getPointerId(i)] = cap
                    performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                    val action = cap.action
                    if (action is Action.Key && action.repeats) {
                        // Fire now, then keep firing while held — like a real ⌫ or arrow key.
                        send(cap)
                        val repeat = object : Runnable {
                            override fun run() { send(cap); handler.postDelayed(this, REPEAT_MS) }
                        }
                        repeating?.let { handler.removeCallbacks(it) }
                        repeating = repeat
                        handler.postDelayed(repeat, REPEAT_DELAY_MS)
                    }
                    invalidate()
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP -> {
                    val cap = pressed.remove(e.getPointerId(e.actionIndex))
                    val action = cap?.action
                    if (action is Action.Key && action.repeats) {
                        repeating?.let { handler.removeCallbacks(it) }
                        repeating = null
                    } else if (cap != null) {
                        send(cap)
                    }
                    invalidate()
                }
                MotionEvent.ACTION_CANCEL -> {
                    pressed.clear()
                    repeating?.let { handler.removeCallbacks(it) }
                    repeating = null
                    invalidate()
                }
            }
            return true
        }

        private fun send(cap: PcKeyboardLayout.KeyCap) {
            when (val action = cap.action) {
                is Action.Modifier -> ModifierKeys.cycle(action.vk)
                is Action.Fn -> { fn = !fn; rows = PcKeyboardLayout.rows(fn) }
                is Action.Hide -> onHide()
                is Action.Voice -> onVoice()
                else -> PcKeyboardLayout.press(action, MirrorControl::key, MirrorControl::text)
            }
            invalidate()
        }

        override fun onDetachedFromWindow() {
            super.onDetachedFromWindow()
            handler.removeCallbacksAndMessages(null)
        }

        private companion object {
            const val REPEAT_DELAY_MS = 400L
            const val REPEAT_MS = 50L
        }
    }

    private companion object {
        const val KEY_WIDTH = "fkb_width"
        const val KEY_X = "fkb_x"
        const val KEY_Y = "fkb_y"
    }
}
