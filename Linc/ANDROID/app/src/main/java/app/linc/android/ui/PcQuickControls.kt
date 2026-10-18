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
import androidx.compose.foundation.layout.width
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
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
        Column(Modifier.padding(horizontal = 12.dp, vertical = 8.dp)) {
            QuickSlider(
                icon = if (pc.muted) LincSymbols.VolumeOff else LincSymbols.VolumeUp,
                iconLabel = if (pc.muted) "Unmute the PC" else "Mute the PC",
                onIcon = { PcControl.volumeMute(!pc.muted) },
                label = if (pc.muted) "Volume (muted)" else "Volume",
                value = pc.volume,
                onSet = { PcControl.volumeSet(it) },
            )
            if (pc.canBrightness && pc.brightness != null) {
                QuickSlider(icon = LincSymbols.Brightness, iconLabel = null, onIcon = null,
                    label = "Brightness", value = pc.brightness, onSet = { PcControl.brightnessSet(it) })
            }
            val facts = listOfNotNull(
                pc.batteryPercent?.let { "Battery $it%" + if (pc.charging) " · charging" else "" },
                when (pc.network) { "wifi" -> "On Wi-Fi"; "ethernet" -> "On Ethernet"; else -> null },
            )
            if (facts.isNotEmpty()) Text(facts.joinToString("  ·  "), style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(start = 12.dp, bottom = 8.dp))
        }
    }
}

/** A 0-100 slider that sends once, when the finger lifts, rather than on every pixel of drag. */
@Composable
private fun QuickSlider(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    iconLabel: String?,
    onIcon: (() -> Unit)?,
    label: String,
    value: Int,
    onSet: (Int) -> Unit,
) {
    var dragging by remember { mutableStateOf<Float?>(null) }
    val shown = dragging ?: value.toFloat()
    Row(verticalAlignment = Alignment.CenterVertically) {
        if (onIcon != null) androidx.compose.material3.IconButton(onClick = onIcon) {
            androidx.compose.material3.Icon(icon, contentDescription = iconLabel)
        } else androidx.compose.material3.Icon(icon, contentDescription = null, modifier = Modifier.padding(12.dp))
        Slider(
            value = shown,
            onValueChange = { dragging = it },
            onValueChangeFinished = {
                dragging?.let { onSet(it.roundToInt()) }
                dragging = null
            },
            valueRange = 0f..100f,
            modifier = Modifier.weight(1f).semantics { contentDescription = label },
        )
        Text("${shown.roundToInt()}", style = MaterialTheme.typography.labelLarge, modifier = Modifier.width(40.dp).padding(start = 8.dp))
    }
}
