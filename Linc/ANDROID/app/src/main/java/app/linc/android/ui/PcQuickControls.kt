package app.linc.android.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import app.linc.android.service.PcControl
import app.linc.android.ui.kit.Kit
import kotlinx.coroutines.delay
import kotlin.math.roundToInt

/**
 * Home → "Your PC" (D-092): its battery and network, and sliders for volume and screen
 * brightness, through the same v17 `pc.control` as Tools. Polls `pc.state` every ten seconds
 * while visible, since the PC never pushes it (PROTOCOL.md v17). Nothing shows until the PC has
 * answered once, and nothing at all for a PC too old for quick controls.
 */
@Composable
fun PcQuickControls(connectionKey: Any?) {
    val state by PcControl.state.collectAsState()
    if (!PcControl.ready()) return
    LaunchedEffect(connectionKey) {
        while (true) {
            PcControl.requestState()
            delay(10_000)
        }
    }
    val pc = state ?: return

    Surface(
        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter),
        shape = RoundedCornerShape(24.dp),
        color = MaterialTheme.colorScheme.surfaceContainer,
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("Your PC", style = MaterialTheme.typography.titleMedium)
            Text(
                listOfNotNull(
                    pc.batteryPercent?.let { "Battery $it%" + if (pc.charging) " · charging" else "" },
                    when (pc.network) { "wifi" -> "On Wi-Fi"; "ethernet" -> "On Ethernet"; else -> null },
                ).joinToString("  ·  ").ifEmpty { "Plugged in" },
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            QuickSlider(
                label = if (pc.muted) "Volume (muted)" else "Volume",
                value = pc.volume,
                onSet = { PcControl.volumeSet(it) },
                trailing = {
                    TextButton(onClick = { PcControl.volumeMute(!pc.muted) }) {
                        Text(if (pc.muted) "Unmute" else "Mute")
                    }
                },
            )
            if (pc.canBrightness && pc.brightness != null) {
                QuickSlider(label = "Brightness", value = pc.brightness, onSet = { PcControl.brightnessSet(it) })
            }
        }
    }
}

/** A 0-100 slider that sends once, when the finger lifts, rather than on every pixel of drag. */
@Composable
private fun QuickSlider(
    label: String,
    value: Int,
    onSet: (Int) -> Unit,
    trailing: @Composable () -> Unit = {},
) {
    var dragging by remember { mutableStateOf<Float?>(null) }
    val shown = dragging ?: value.toFloat()
    Column {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("$label  ${shown.roundToInt()}%", style = MaterialTheme.typography.labelLarge,
                modifier = Modifier.weight(1f))
            trailing()
        }
        Slider(
            value = shown,
            onValueChange = { dragging = it },
            onValueChangeFinished = {
                dragging?.let { onSet(it.roundToInt()) }
                dragging = null
            },
            valueRange = 0f..100f,
        )
    }
}
