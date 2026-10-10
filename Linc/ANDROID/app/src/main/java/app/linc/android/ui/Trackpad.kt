package app.linc.android.ui

import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.pointer.PointerInputScope
import androidx.compose.ui.input.pointer.pointerInput
import app.linc.android.service.MirrorControl
import app.linc.android.service.TrackpadGestures
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * Turns this surface into the PC's trackpad. Gesture rules live in the pure [TrackpadGestures];
 * this only feeds it pointer events and sends what it decides. Every change is consumed, so a
 * scrolling parent never steals a drag.
 *
 * [onGesture] is told about each finished gesture (the first-use hints count them).
 */
fun Modifier.trackpad(enabled: Boolean, onGesture: () -> Unit = {}): Modifier =
    if (!enabled) this else pointerInput(Unit) { trackpadLoop(onGesture) }

private suspend fun PointerInputScope.trackpadLoop(onGesture: () -> Unit) {
    val engine = TrackpadGestures(
        out = object : TrackpadGestures.Output {
            override fun move(dx: Int, dy: Int) = MirrorControl.pointerRelative("move", dx, dy)
            override fun button(button: String, down: Boolean) = MirrorControl.trackpadButton(button, down)
            override fun scroll(dx: Int, dy: Int) = MirrorControl.scrollPrecise(dx, dy)
        },
        touchSlopPx = viewConfiguration.touchSlop,
        density = density,
        wheelUnit = { if (MirrorControl.v20()) 1 else 120 },
    )
    kotlinx.coroutines.coroutineScope {
        // A tap's click waits out the double-tap window; this sends it when nothing followed.
        launch {
            while (true) {
                val due = engine.pendingTapAt
                if (due == null) { delay(30); continue }
                val wait = due - android.os.SystemClock.uptimeMillis()
                if (wait > 0) delay(wait)
                engine.flush(android.os.SystemClock.uptimeMillis())
            }
        }
        awaitEachGesture {
            val first = awaitFirstDown(requireUnconsumed = false)
            engine.press(first.uptimeMillis, first.position.x, first.position.y)
            first.consume()
            try {
                while (true) {
                    val event = awaitPointerEvent()
                    val pressed = event.changes.filter { it.pressed }
                    val t = event.changes.maxOf { it.uptimeMillis }
                    if (pressed.isEmpty()) {
                        engine.release(t)
                        event.changes.forEach { it.consume() }
                        break
                    }
                    val two = pressed.take(2)
                    engine.update(
                        t,
                        pressed.size,
                        two.sumOf { it.position.x.toDouble() }.toFloat() / two.size,
                        two.sumOf { it.position.y.toDouble() }.toFloat() / two.size,
                    )
                    event.changes.forEach { it.consume() }
                }
                onGesture()
            } catch (e: kotlinx.coroutines.CancellationException) {
                engine.cancel()
                throw e
            }
        }
    }
}
