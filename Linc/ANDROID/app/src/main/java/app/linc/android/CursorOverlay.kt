package app.linc.android

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Path
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
}
