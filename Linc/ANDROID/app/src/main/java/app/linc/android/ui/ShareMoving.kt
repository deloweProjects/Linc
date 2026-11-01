@file:OptIn(androidx.compose.material3.ExperimentalMaterial3ExpressiveApi::class)

package app.linc.android.ui

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateContentSize
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearWavyProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.linc.android.service.LiveShare
import app.linc.android.service.ShareLive
import app.linc.android.ui.kit.Kit

/**
 * "Moving now" on the Share tab (owner, 2026-11-01: "I gotta see what's being shared"): every file
 * going between the phone and the PC this moment, each with a little phone ⇄ PC flight, its live
 * progress (as the PC reports it), and Cancel. Finished ones say how they ended, then leave.
 */
@Composable
fun MovingNowCard(items: List<LiveShare>) {
    Surface(
        shape = RoundedCornerShape(Kit.groupOuter),
        color = MaterialTheme.colorScheme.surfaceContainer,
        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter).animateContentSize(),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            Text("Moving now", style = MaterialTheme.typography.titleMedium)
            items.forEach { item ->
                AnimatedVisibility(visible = true, enter = fadeIn() + expandVertically(), exit = fadeOut() + shrinkVertically()) {
                    MovingRow(item)
                }
            }
        }
    }
}

@Composable
private fun MovingRow(item: LiveShare) {
    val context = LocalContext.current
    val progress by animateFloatAsState(item.percent / 100f, tween(400, easing = FastOutSlowInEasing), label = "progress")
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Flight(toPhone = item.toPhone, moving = !item.finished && item.state != LiveShare.State.Cancelling)
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(item.name, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(
                    when (item.state) {
                        LiveShare.State.Moving -> (if (item.toPhone) "From your PC" else "To your PC") + " · ${item.percent}%"
                        LiveShare.State.Cancelling -> "Stopping…"
                        LiveShare.State.Done -> if (item.toPhone) "Received" else "On your PC"
                        LiveShare.State.Cancelled -> "Cancelled"
                        LiveShare.State.Failed -> item.reason ?: "Didn't make it"
                    },
                    style = MaterialTheme.typography.bodyMedium,
                    color = if (item.state == LiveShare.State.Failed) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            if (item.state == LiveShare.State.Moving) {
                TextButton(onClick = { ShareLive.cancel(context, item.id) }) { Text("Cancel") }
            }
        }
        if (!item.finished) {
            LinearWavyProgressIndicator(progress = { progress }, modifier = Modifier.fillMaxWidth())
        }
    }
}

/** Phone ⇄ PC with dots flying the way the file goes; still once it's over. */
@Composable
private fun Flight(toPhone: Boolean, moving: Boolean) {
    val transition = rememberInfiniteTransition(label = "flight")
    val t by transition.animateFloat(0f, 1f, infiniteRepeatable(tween(1100, easing = LinearEasing), RepeatMode.Restart), label = "t")
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
        Icon(LincSymbols.Smartphone, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(22.dp))
        BoxWithConstraints(Modifier.weight(1f).height(12.dp), contentAlignment = Alignment.CenterStart) {
            val width = maxWidth - 8.dp
            Box(Modifier.fillMaxWidth().height(2.dp).background(MaterialTheme.colorScheme.outlineVariant, CircleShape))
            if (moving) {
                // Three dots, a third apart, running from the sender to the receiver.
                for (k in 0 until 3) {
                    val phase = (t + k / 3f) % 1f
                    val x = if (toPhone) 1f - phase else phase
                    Box(
                        Modifier
                            .offset(x = width * x)
                            .size(8.dp)
                            .alpha(1f - kotlin.math.abs(phase - 0.5f))
                            .background(MaterialTheme.colorScheme.primary, CircleShape),
                    )
                }
            }
        }
        Icon(LincSymbols.Computer, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(22.dp))
    }
}
