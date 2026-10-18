package app.linc.android.ui

import android.Manifest
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.IconToggleButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Slider
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import app.linc.android.service.AudioEverywhere
import app.linc.android.service.EverywhereDevice
import app.linc.android.service.Explanations
import app.linc.android.service.PcSpeaker
import app.linc.android.service.PcVoiceAccess
import app.linc.android.ui.kit.HelperText
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.SectionHeader
import kotlin.math.abs
import kotlin.math.roundToInt

/**
 * Tools → Media → Play everywhere (v26, D-096): the PC's sound on the PC's speakers, its
 * Bluetooth devices and this phone's at once, in step — with a volume for each, and alignment
 * the microphone measures and the user can nudge by ear.
 */
@Composable
fun PlayEverywhereSection() {
    val context = LocalContext.current
    val state by AudioEverywhere.state.collectAsState()
    val ready = AudioEverywhere.ready()
    LaunchedEffect(ready) { if (ready) AudioEverywhere.refresh(context) }
    val mic = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) AudioEverywhere.calibrate()
    }

    SectionHeader("Play everywhere")
    Column(Modifier.fillMaxWidth().padding(horizontal = Kit.gutter), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                when {
                    !ready -> "Update Linc on the PC to play its sound everywhere at once."
                    state.on -> "On: every speaker below plays the PC's sound together."
                    else -> "Play the PC's sound on its speakers, its Bluetooth devices and this phone, all in step."
                },
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.weight(1f),
            )
            Switch(checked = state.on, enabled = ready, onCheckedChange = { AudioEverywhere.set(context, it) })
        }
        PcSpeaker.explain(state.error)?.let { HelperText(it, kind = Explanations.Kind.Error) }

        if (state.on) {
            HelperText("Sound is ${state.delayMs} ms behind the PC's picture, so the slowest speaker can keep up.")
            state.note?.let { HelperText(it, kind = Explanations.Kind.Warning) }
            if (state.listening) HelperText("Listening…", kind = Explanations.Kind.Warning)
            if (state.devices.isNotEmpty() && state.devices.none { it.calibrated } && state.testing == null) {
                HelperText("Not aligned yet: tap \"Align automatically\" so Bluetooth and the phone line up exactly.",
                    kind = Explanations.Kind.Warning)
            }
            for (device in state.devices) DeviceRow(device)
            AlignmentControls(
                state.devices.filter { it.enabled },
                state.marginMs,
                busy = state.testing != null,
                onAlign = {
                    if (PcVoiceAccess.hasMicPermission(context)) AudioEverywhere.calibrate()
                    else mic.launch(Manifest.permission.RECORD_AUDIO)
                },
            )
        }
    }
}

@Composable
private fun DeviceRow(device: EverywhereDevice) {
    Surface(shape = RoundedCornerShape(16.dp), color = MaterialTheme.colorScheme.surfaceContainer, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(horizontal = 12.dp, vertical = 8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(
                    when {
                        device.bluetooth -> LincSymbols.Devices
                        device.kind == "phone" -> LincSymbols.Smartphone
                        else -> LincSymbols.Computer
                    },
                    contentDescription = null,
                    modifier = Modifier.size(24.dp),
                )
                Column(Modifier.weight(1f).padding(start = 12.dp)) {
                    Text(device.name, style = MaterialTheme.typography.bodyLarge)
                    Text(statusText(device), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                Switch(checked = device.enabled, onCheckedChange = { AudioEverywhere.setDevice(device.id, enabled = it) })
            }
            if (device.enabled) {
                var volume by remember(device.id, device.volume) { mutableFloatStateOf(device.volume.toFloat()) }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    IconToggleButton(checked = device.muted, onCheckedChange = { AudioEverywhere.setDevice(device.id, muted = it) }) {
                        Icon(if (device.muted) LincSymbols.VolumeOff else LincSymbols.VolumeUp,
                            contentDescription = if (device.muted) "Unmute ${device.name}" else "Mute ${device.name}")
                    }
                    Slider(
                        value = volume,
                        onValueChange = { volume = it },
                        onValueChangeFinished = { AudioEverywhere.setDevice(device.id, volume = volume.roundToInt()) },
                        valueRange = 0f..100f,
                        modifier = Modifier.weight(1f).semantics { contentDescription = "${device.name} volume" },
                    )
                    Text("${volume.roundToInt()}", style = MaterialTheme.typography.labelLarge, modifier = Modifier.width(36.dp))
                }
            }
        }
    }
}

private fun statusText(d: EverywhereDevice): String = when {
    !d.enabled -> "Off"
    d.status == "error" -> "Stopped" + (d.problem?.let { ": $it" } ?: "")
    d.status == "rerouted" -> "Android played it elsewhere: this phone may only play one output at a time"
    d.status == "playing" && d.muted -> "Muted"
    d.status == "playing" -> "In step, within ${"%.1f".format(abs(d.offByMs))} ms"
    else -> "Starting…"
}

/** Align automatically, test clicks, and a nudge per speaker for whoever trusts their ears more. */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun AlignmentControls(devices: List<EverywhereDevice>, marginMs: Int, busy: Boolean, onAlign: () -> Unit) {
    var open by rememberSaveable { mutableStateOf(false) }
    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        FilledTonalButton(onClick = onAlign, enabled = !busy) { Text("Align automatically") }
        OutlinedButton(onClick = { AudioEverywhere.clicks() }, enabled = !busy) { Text("Play test clicks") }
        TextButton(onClick = { open = !open }) { Text(if (open) "Hide alignment" else "Adjust by hand") }
    }
    HelperText("Align automatically plays a short sweep on each speaker in turn and times it with this phone's microphone. Hold the phone where you'll be listening.")
    if (!open) return
    for (d in devices) NudgeRow(d)
    var margin by remember(marginMs) { mutableFloatStateOf(marginMs.toFloat()) }
    Text("Extra safety margin: ${margin.roundToInt()} ms", style = MaterialTheme.typography.bodyMedium)
    Slider(
        value = margin,
        onValueChange = { margin = it },
        onValueChangeFinished = { AudioEverywhere.setMargin(margin.roundToInt()) },
        valueRange = 0f..300f,
        modifier = Modifier.semantics { contentDescription = "Extra safety margin" },
    )
    HelperText("Raise this if the sound breaks up on a busy Wi-Fi. Every speaker waits a little longer, together.")
    TextButton(onClick = { AudioEverywhere.resetAlignment() }) { Text("Reset alignment") }
}

@Composable
private fun NudgeRow(d: EverywhereDevice) {
    var nudge by remember(d.id, d.manualMs) { mutableFloatStateOf(d.manualMs.toFloat()) }
    fun commit(value: Float) {
        nudge = value.coerceIn(-300f, 300f)
        AudioEverywhere.setDevice(d.id, manualMs = nudge.toDouble())
    }
    Column {
        Text(d.name, style = MaterialTheme.typography.bodyMedium)
        Text(
            (if (d.calibrated) "Measured ${"%.1f".format(d.autoMs)} ms" else "Not measured") +
                " · nudge ${"%+.0f".format(nudge)} ms" + (if (nudge > 0) " (plays earlier)" else if (nudge < 0) " (plays later)" else ""),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Row(verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = { commit(nudge - 1) }) { Icon(LincSymbols.ArrowLeft, contentDescription = "1 ms later") }
            Slider(
                value = nudge,
                onValueChange = { nudge = it },
                onValueChangeFinished = { commit(nudge.roundToInt().toFloat()) },
                valueRange = -300f..300f,
                modifier = Modifier.weight(1f).semantics { contentDescription = "${d.name} alignment" },
            )
            IconButton(onClick = { commit(nudge + 1) }) { Icon(LincSymbols.ArrowRight, contentDescription = "1 ms earlier") }
        }
    }
}
