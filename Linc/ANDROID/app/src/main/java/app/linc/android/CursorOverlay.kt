package app.linc.android

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Path
import android.os.SystemClock
import android.view.View

/**
 * Draws the PC's mouse pointer on top of the mirrored video (v20, D-067). The pointer is
 * rendered here on the phone, not in the video, so it moves the instant a finger does — the
 * single biggest reason Remote Desktop feels responsive on a link whose video is behind.
 *
 * Position and scale are set by MirrorActivity, which owns the zoom matrix; this view only
 * paints. It never takes touches.
 */
class CursorOverlay(context: Context) : View(context) {

    private var bitmap: Bitmap? = null
    private var hotX = 0f
    private var hotY = 0f
    private var viewX = 0f
    private var viewY = 0f
    private var scale = 1f
    private var shown = false

    private val paint = Paint(Paint.FILTER_BITMAP_FLAG or Paint.ANTI_ALIAS_FLAG)
    private val fill = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.WHITE; style = Paint.Style.FILL }
    private val stroke = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.BLACK; style = Paint.Style.STROKE; strokeWidth = 1.5f; strokeJoin = Paint.Join.ROUND
    }

    /**
     * A click ring, drawn the instant a button goes down. The PC's own reaction only shows once
     * the next video frame arrives (often 100 ms or more over Wi-Fi), so without this a tap
     * looked like it had done nothing.
     */
    private var pulseStartMs = 0L
    private var pulseColor = Color.WHITE
    private val ring = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE }

    /** Show a click at the pointer tip: [button] picks the colour so a right-click reads differently. */
    fun pulse(button: String) {
        pulseStartMs = SystemClock.uptimeMillis()
        pulseColor = when (button) {
            "right" -> 0xFFFFB300.toInt()
            "middle" -> 0xFF80CBC4.toInt()
            else -> 0xFF64B5F6.toInt()
        }
        invalidate()
    }

    /** The classic arrow, in 32-px cursor units with the tip at 0,0 — used until the PC sends its own. */
    private val arrow = Path().apply {
        moveTo(0f, 0f); lineTo(0f, 17f); lineTo(4f, 13f); lineTo(7f, 20f)
        lineTo(10f, 19f); lineTo(7f, 12f); lineTo(12f, 12f); close()
    }

    init {
        isClickable = false
        isFocusable = false
    }

    /** Place the pointer: tip at view pixel [x],[y], drawn at [scale] view px per PC px. */
    fun show(x: Float, y: Float, scale: Float, image: Bitmap?, hotX: Int, hotY: Int) {
        val changed = !shown || x != viewX || y != viewY || scale != this.scale || image !== bitmap
        shown = true
        viewX = x; viewY = y; this.scale = scale
        bitmap = image; this.hotX = hotX.toFloat(); this.hotY = hotY.toFloat()
        if (changed) invalidate()
    }

    fun hide() {
        if (!shown) return
        shown = false
        invalidate()
    }

    override fun onDraw(canvas: Canvas) {
        if (!shown) return
        drawPulse(canvas)
        canvas.save()
        canvas.translate(viewX, viewY)
        canvas.scale(scale, scale)
        val image = bitmap
        if (image != null) {
            canvas.drawBitmap(image, -hotX, -hotY, paint)
        } else {
            canvas.drawPath(arrow, fill)
            canvas.drawPath(arrow, stroke)
        }
        canvas.restore()
    }

    private fun drawPulse(canvas: Canvas) {
        val age = SystemClock.uptimeMillis() - pulseStartMs
        if (pulseStartMs == 0L || age >= PULSE_MS) return
        val t = age / PULSE_MS.toFloat()
        val density = resources.displayMetrics.density
        ring.color = pulseColor
        ring.alpha = ((1f - t) * 230).toInt()
        ring.strokeWidth = 3f * density
        canvas.drawCircle(viewX, viewY, (8f + 22f * t) * density, ring)
        postInvalidateOnAnimation()
    }

    private companion object {
        const val PULSE_MS = 280L
    }
}
