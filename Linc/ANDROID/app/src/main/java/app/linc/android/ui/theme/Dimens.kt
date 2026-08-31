package app.linc.android.ui.theme

import androidx.compose.ui.unit.dp

/** M17a B1: one spacing scale for the whole app. No hand-set paddings anywhere else. */
object Dimens {
    val xs = 4.dp
    val s = 8.dp
    val m = 12.dp
    val l = 16.dp
    val xl = 24.dp
    val xxl = 32.dp

    /** Card corner radius (B1). Buttons/chips use [radiusControl]. */
    val radiusCard = 20.dp
    val radiusControl = 12.dp

    /** Every interactive element is at least this tall/wide (B1). */
    val touchTarget = 48.dp

    /** M16: the trackpad's floor. Do not remove; do not turn back into a weight. */
    val trackpadMin = 280.dp
}
