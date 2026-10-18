package app.linc.android.ui

import android.Manifest
import android.content.Context
import android.content.Intent
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
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
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.linc.android.CaptureConsentActivity
import app.linc.android.PcToolActivity
import app.linc.android.service.AudioEverywhere
import app.linc.android.service.AudioOutputs
import app.linc.android.service.EverywhereDevice
import app.linc.android.service.EverywhereState
import app.linc.android.service.Explanations
import app.linc.android.service.PcSpeaker
import app.linc.android.service.PcVoiceAccess
import app.linc.android.service.PhoneSource
import app.linc.android.ui.kit.ConnectedChoice
import app.linc.android.ui.kit.HelperText
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.LincScaffold
import app.linc.android.ui.kit.SectionHeader
import app.linc.android.ui.kit.pagePadding
import kotlin.math.abs
import kotlin.math.roundToInt

/*
 * Play everywhere's own page (D-097). The surface is deliberately small — on/off, whose sound,
 * which speakers and how loud, one Align button — and everything about timing lives one tap away
 * under Advanced, for whoever wants to nudge by ear.
 */

/** The line in the Media sheet: what Play everywhere is doing, and the way to its page. */
@Composable
fun PlayEverywhereSummary() {
    val context = LocalContext.current
    val state by AudioEverywhere.state.collectAsState()
    val phone by PhoneSource.state.collectAsState()
    val on = state.on || phone.on
    Surface(
        shape = RoundedCornerShape(20.dp),
        color = MaterialTheme.colorScheme.secondaryContainer,
        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter)
            .clickable { PcToolActivity.open(context, PcToolActivity.SOUND) },
    ) {
        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(LincSymbols.Devices, contentDescription = null, modifier = Modifier.size(28.dp))
            Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                Text("Play everywhere", style = MaterialTheme.typography.titleMedium)
                Text(
                    when {
                        phone.on -> "This phone's sound, on ${speakerCount(state, phone.replays.size, true)}"
                        state.on -> "The PC's sound, on ${speakerCount(state, 0, false)}"
                        else -> "PC and phone speakers together, in step"
                    },
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSecondaryContainer,
                )
            }
            Icon(LincSymbols.ChevronRight, contentDescription = if (on) "Open Play everywhere" else "Set up Play everywhere")
        }
    }
}

private fun speakerCount(state: EverywhereState, local: Int, phoneSource: Boolean): String {
    val n = if (phoneSource) 1 + local + state.devices.count { it.kind == "pc" && it.enabled && it.status == "playing" }
    else state.devices.count { it.enabled && it.status == "playing" }
    return if (n == 1) "1 speaker" else "$n speakers"
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun SoundScreen(onBack: () -> Unit) {
    val context = LocalContext.current
    val state by AudioEverywhere.state.collectAsState()
    val phone by PhoneSource.state.collectAsState()
    val ready = AudioEverywhere.ready()
    var advanced by remember { mutableIntStateOf(0) }
    LaunchedEffect(ready) { if (ready) AudioEverywhere.refresh(context) }

    // Recording what apps play needs the microphone permission, then Android's own consent.
    val startPhone = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) context.startActivity(Intent(context, CaptureConsentActivity::class.java))
    }
    val alignMic = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) align(context, phone.on)
    }
    fun phoneSourceOn() {
        if (PcVoiceAccess.hasMicPermission(context)) context.startActivity(Intent(context, CaptureConsentActivity::class.java))
        else startPhone.launch(Manifest.permission.RECORD_AUDIO)
    }
    val source = if (phone.on) "phone" else "pc"
    val on = state.on || phone.on

    if (advanced == 1) {
        AdvancedSound(state, phone.on, onBack = { advanced = 0 })
        return
    }
    LincScaffold(title = "Play everywhere", onBack = onBack) { inner ->
        LazyColumn(contentPadding = pagePadding(inner), verticalArrangement = Arrangement.spacedBy(Kit.groupGap)) {
            item {
                Surface(shape = RoundedCornerShape(28.dp), color = if (on) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceContainer,
                    modifier = Modifier.fillMaxWidth()) {
                    Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Column(Modifier.weight(1f)) {
                                Text(if (on) "On" else "Off", style = MaterialTheme.typography.headlineMedium)
                                Text(
                                    when {
                                        phone.on -> "Apps on this phone play on every speaker below, in step with ${phone.nativeName}."
                                        state.on -> "The PC's sound plays on every speaker below, together."
                                        else -> "Every speaker you pick plays the same sound at the same moment."
                                    },
                                    style = MaterialTheme.typography.bodyMedium,
                                )
                            }
                            Switch(checked = on, enabled = source == "phone" || ready, onCheckedChange = {
                                when {
                                    it && source == "pc" -> AudioEverywhere.set(context, true)
                                    !it && phone.on -> PhoneSource.stop(context, "turned off")
                                    !it -> AudioEverywhere.set(context, false)
                                }
                            })
                        }
                        ConnectedChoice(
                            options = listOf("pc" to "PC's sound", "phone" to "Phone's sound"),
                            selected = source,
                            onSelect = { chosen ->
                                if (chosen == "phone" && !phone.on) {
                                    if (state.on) AudioEverywhere.set(context, false)
                                    phoneSourceOn()
                                } else if (chosen == "pc" && phone.on) {
                                    PhoneSource.stop(context, "switched to the PC's sound")
                                    if (ready) AudioEverywhere.set(context, true)
                                }
                            },
                        )
                        if (!ready && source == "pc") HelperText("Connect the PC (and update Linc there) to play its sound. The phone's sound works without it.",
                            kind = Explanations.Kind.Blocked)
                        PcSpeaker.explain(state.error)?.let { HelperText(it, kind = Explanations.Kind.Error) }
                        (phone.note ?: state.note)?.let { HelperText(it, kind = Explanations.Kind.Warning) }
                        if (on && state.devices.isNotEmpty() && state.devices.none { it.calibrated } && state.testing == null && !phone.on) {
                            HelperText("Tap Align once so Bluetooth and the phone line up exactly.", kind = Explanations.Kind.Warning)
                        }
                    }
                }
            }
            item { SectionHeader("Speakers") }
            if (phone.on) {
                item { NativeRow(phone.nativeName) }
                val outputs = AudioOutputs.list(context).filter { it.name != phone.nativeName }
                for (o in outputs) item(o.id) {
                    SpeakerRow(
                        name = o.name, icon = if (o.bluetooth) LincSymbols.Devices else LincSymbols.Smartphone,
                        status = if (PhoneSource.localEnabled(context, o.id)) "Plays in step" else "Off",
                        enabled = PhoneSource.localEnabled(context, o.id), volume = PhoneSource.localVolume(context, o.id), muted = false,
                        onEnabled = { PhoneSource.setLocal(context, o.id, enabled = it) },
                        onVolume = { PhoneSource.setLocal(context, o.id, volume = it) },
                        onMuted = null,
                    )
                }
                for (d in state.devices.filter { it.kind == "pc" }) item(d.id) { DeviceSpeakerRow(d) }
                if (state.devices.none { it.kind == "pc" }) item {
                    HelperText("Connect the PC to add its speakers.", modifier = Modifier.padding(horizontal = Kit.gutter))
                }
            } else {
                if (state.devices.isEmpty()) item {
                    HelperText(if (on) "Looking for speakers…" else "Turn it on to see the speakers on the PC and this phone.",
                        modifier = Modifier.padding(horizontal = Kit.gutter))
                }
                for (d in state.devices) item(d.id) { DeviceSpeakerRow(d) }
            }
            if (on) item {
                Row(Modifier.fillMaxWidth().padding(horizontal = Kit.gutter, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(
                        onClick = { if (PcVoiceAccess.hasMicPermission(context)) align(context, phone.on) else alignMic.launch(Manifest.permission.RECORD_AUDIO) },
                        enabled = state.testing == null && !state.listening && !phone.listening,
                        modifier = Modifier.weight(1f),
                    ) { Text(if (state.listening || phone.listening) "Listening…" else "Align") }
                    OutlinedButton(onClick = { advanced = 1 }, modifier = Modifier.weight(1f)) { Text("Advanced") }
                }
            }
            if (on) item {
                HelperText(if (phone.on) "Align plays a short sweep and times the copies against ${phone.nativeName} with the microphone. Pause your music first."
                    else "Align plays a short sweep on each speaker and times it with this phone's microphone. Hold the phone where you'll listen.",
                    modifier = Modifier.padding(horizontal = Kit.gutter))
            }
        }
    }
}

private fun align(context: Context, phoneSource: Boolean) {
    if (phoneSource) PhoneSource.calibrate(context) else AudioEverywhere.calibrate()
}

@Composable
private fun NativeRow(name: String) {
    Surface(shape = RoundedCornerShape(16.dp), color = MaterialTheme.colorScheme.surfaceContainer, modifier = Modifier.fillMaxWidth()) {
        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(LincSymbols.MusicNote, contentDescription = null)
            Column(Modifier.weight(1f).padding(start = 12.dp)) {
                Text(name, style = MaterialTheme.typography.bodyLarge)
                Text("Plays apps' sound itself; everything else follows it", style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}

@Composable
private fun DeviceSpeakerRow(d: EverywhereDevice) {
    SpeakerRow(
        name = d.name,
        icon = when { d.bluetooth -> LincSymbols.Devices; d.kind == "phone" -> LincSymbols.Smartphone; else -> LincSymbols.Computer },
        status = statusText(d), enabled = d.enabled, volume = d.volume, muted = d.muted,
        onEnabled = { AudioEverywhere.setDevice(d.id, enabled = it) },
        onVolume = { AudioEverywhere.setDevice(d.id, volume = it) },
        onMuted = { AudioEverywhere.setDevice(d.id, muted = it) },
    )
}

@Composable
private fun SpeakerRow(
    name: String, icon: ImageVector, status: String, enabled: Boolean, volume: Int, muted: Boolean,
    onEnabled: (Boolean) -> Unit, onVolume: (Int) -> Unit, onMuted: ((Boolean) -> Unit)?,
) {
    Surface(shape = RoundedCornerShape(16.dp), color = MaterialTheme.colorScheme.surfaceContainer, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(start = 16.dp, end = 12.dp, top = 8.dp, bottom = 4.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(icon, contentDescription = null, modifier = Modifier.size(24.dp))
                Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                    Text(name, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(status, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                Switch(checked = enabled, onCheckedChange = onEnabled)
            }
            if (enabled) {
                var level by remember(name, volume) { mutableFloatStateOf(volume.toFloat()) }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    if (onMuted != null) {
                        IconToggleButton(checked = muted, onCheckedChange = onMuted) {
                            Icon(if (muted) LincSymbols.VolumeOff else LincSymbols.VolumeUp, contentDescription = if (muted) "Unmute $name" else "Mute $name")
                        }
                    } else Icon(LincSymbols.VolumeUp, contentDescription = null, modifier = Modifier.padding(12.dp))
                    Slider(
                        value = level, onValueChange = { level = it }, onValueChangeFinished = { onVolume(level.roundToInt()) },
                        valueRange = 0f..100f, modifier = Modifier.weight(1f).semantics { contentDescription = "$name volume" },
                    )
                    Text("${level.roundToInt()}", style = MaterialTheme.typography.labelLarge, modifier = Modifier.width(36.dp))
                }
            }
        }
    }
}

private fun statusText(d: EverywhereDevice): String = when {
    !d.enabled -> "Off"
    d.status == "error" -> "Stopped" + (d.problem?.let { ": $it" } ?: "")
    d.status == "rerouted" -> "Android played it elsewhere: this phone plays one output at a time"
    d.status == "playing" && d.muted -> "Muted"
    d.status == "playing" -> "In step" + (if (abs(d.offByMs) >= 0.5) ", within ${"%.1f".format(abs(d.offByMs))} ms" else "")
    else -> "Starting…"
}

// ---- Advanced ---------------------------------------------------------------------------------

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun AdvancedSound(state: EverywhereState, phoneSource: Boolean, onBack: () -> Unit) {
    LincScaffold(title = "Advanced", subtitle = "Play everywhere", onBack = onBack) { inner ->
        LazyColumn(contentPadding = pagePadding(inner), verticalArrangement = Arrangement.spacedBy(Kit.groupGap)) {
            item {
                HelperText(
                    if (phoneSource) "Every copy aims for the moment the phone's own output plays the sound: ${PhoneSource.state.value.delayMs} ms after apps play it."
                    else "Every speaker plays ${state.delayMs} ms after the PC, so the slowest can keep up. Sound in PC Remote follows the picture instead.",
                    modifier = Modifier.padding(horizontal = Kit.gutter),
                )
            }
            if (!phoneSource) {
                item { SectionHeader("Listen") }
                item {
                    FlowRow(Modifier.padding(horizontal = Kit.gutter), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        FilledTonalButton(onClick = { AudioEverywhere.clicks() }, enabled = state.testing == null) { Text("Play test clicks") }
                        TextButton(onClick = { AudioEverywhere.resetAlignment() }) { Text("Reset alignment") }
                    }
                }
                item { HelperText("Test clicks: every speaker clicks at once. Nudge any that's early or late below.", modifier = Modifier.padding(horizontal = Kit.gutter)) }
                item { SectionHeader("Nudge each speaker") }
                for (d in state.devices.filter { it.enabled }) item(d.id) { NudgeRow(d) }
                item { SectionHeader("Safety margin") }
                item { MarginRow(state.marginMs) }
                item { SectionHeader("Phone as PC speaker") }
                item { PcSpeakerSection() }
            } else {
                item { SectionHeader("Speakers on the PC") }
                for (d in state.devices.filter { it.kind == "pc" && it.enabled }) item(d.id) { NudgeRow(d) }
            }
        }
    }
}

@Composable
private fun NudgeRow(d: EverywhereDevice) {
    var nudge by remember(d.id, d.manualMs) { mutableFloatStateOf(d.manualMs.toFloat()) }
    fun commit(value: Float) {
        nudge = value.coerceIn(-300f, 300f)
        AudioEverywhere.setDevice(d.id, manualMs = nudge.toDouble())
    }
    Column(Modifier.padding(horizontal = Kit.gutter)) {
        Text(d.name, style = MaterialTheme.typography.bodyMedium)
        Text(
            (if (d.calibrated) "Measured ${"%.1f".format(d.autoMs)} ms" else "Not measured") +
                " · nudge ${"%+.0f".format(nudge)} ms" + (if (nudge > 0) " (earlier)" else if (nudge < 0) " (later)" else ""),
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Row(verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = { commit(nudge - 1) }) { Icon(LincSymbols.ArrowLeft, contentDescription = "1 ms later") }
            Slider(value = nudge, onValueChange = { nudge = it }, onValueChangeFinished = { commit(nudge.roundToInt().toFloat()) },
                valueRange = -300f..300f, modifier = Modifier.weight(1f).semantics { contentDescription = "${d.name} alignment" })
            IconButton(onClick = { commit(nudge + 1) }) { Icon(LincSymbols.ArrowRight, contentDescription = "1 ms earlier") }
        }
    }
}

@Composable
private fun MarginRow(marginMs: Int) {
    var margin by remember(marginMs) { mutableFloatStateOf(marginMs.toFloat()) }
    Column(Modifier.padding(horizontal = Kit.gutter)) {
        Text("${margin.roundToInt()} ms extra", style = MaterialTheme.typography.bodyMedium)
        Slider(value = margin, onValueChange = { margin = it }, onValueChangeFinished = { AudioEverywhere.setMargin(margin.roundToInt()) },
            valueRange = 0f..300f, modifier = Modifier.semantics { contentDescription = "Extra safety margin" })
        HelperText("Raise this if the sound breaks up on a busy Wi-Fi. Every speaker waits a little longer, together.")
    }
}
