package app.linc.android.ui

import android.Manifest
import android.content.Context
import android.content.Intent
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.FilledIconButton
import androidx.compose.material3.MaterialShapes
import androidx.compose.material3.RadioButton
import androidx.compose.material3.toShape
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.draw.clip
import app.linc.android.service.AudioReceiver
import app.linc.android.service.CompanionStateHolder
import app.linc.android.service.PcControl
import app.linc.android.service.PcMediaControl
import app.linc.android.service.PcMediaStore
import app.linc.android.service.PcTools
import app.linc.android.ui.kit.LincAlertDialog
import app.linc.android.ui.kit.SettingsGroup
import app.linc.android.ui.kit.SettingsRow
import app.linc.android.ui.kit.Segment
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
fun PlayEverywhereSummary(onOpen: () -> Unit) {
    val context = LocalContext.current
    val state by AudioEverywhere.state.collectAsState()
    val phone by PhoneSource.state.collectAsState()
    val on = state.on || phone.on
    Surface(
        shape = RoundedCornerShape(20.dp),
        color = MaterialTheme.colorScheme.secondaryContainer,
        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter)
            .clickable { onOpen() },
    ) {
        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(LincSymbols.Devices, contentDescription = null, modifier = Modifier.size(28.dp))
            Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                Text("Sound", style = MaterialTheme.typography.titleMedium)
                Text(
                    when {
                        phone.on -> "This phone's sound, on ${speakerCount(state, phone.replays.size, true)}"
                        state.on && state.source == "mirror" -> "This phone's sound, on ${speakerCount(state, 0, false)}"
                        state.on -> "The PC's sound, on ${speakerCount(state, 0, false)}"
                        else -> "Play everywhere, output, speaker and microphone"
                    },
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSecondaryContainer,
                )
            }
            Icon(LincSymbols.ChevronRight, contentDescription = "Open Sound")
        }
    }
}

private fun speakerCount(state: EverywhereState, local: Int, phoneSource: Boolean): String {
    val n = if (phoneSource) 1 + local + state.devices.count { it.kind == "pc" && it.enabled && it.status == "playing" }
    else state.devices.count { it.enabled && it.status == "playing" }
    return if (n == 1) "1 speaker" else "$n speakers"
}

/**
 * Tools → Sound: everything about the PC's sound and this phone's, on one page.
 *  - The PC's volume and what it's playing, at the top.
 *  - Play everywhere: the PC's sound (or this phone's) on every speaker at once, in step.
 *  - Where the PC's sound comes out, this phone as its speaker, and this phone as its microphone.
 * Timing knobs live under Advanced, for whoever wants to nudge by ear.
 */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun SoundScreen(onBack: (() -> Unit)? = null, onSettings: (() -> Unit)? = null) {
    val context = LocalContext.current
    val state by AudioEverywhere.state.collectAsState()
    val phone by PhoneSource.state.collectAsState()
    val outputs by PcTools.outputs.collectAsState()
    val voice by PcVoiceAccess.state.collectAsState()
    val micLive by PcVoiceAccess.micLive.collectAsState()
    val connection by CompanionStateHolder.state.collectAsState()
    val ready = AudioEverywhere.ready()
    var advanced by remember { mutableIntStateOf(0) }
    var explainPhone by remember { mutableStateOf(false) }
    LaunchedEffect(ready, connection) {
        if (ready) AudioEverywhere.refresh(context)
        PcControl.requestState()
        PcTools.refreshOutputs()
        PcTools.refreshBtAudio()
        PcVoiceAccess.refresh(context)
    }
    val bt by PcTools.btAudio.collectAsState()
    val btReady = PcTools.btAudioAvailable()
    // v31: the PC takes this phone's sound the way Mirror screen does and plays it everywhere.
    val mirrorReady = AudioEverywhere.mirrorReady()
    val mirror = state.on && state.source == "mirror"

    // Recording what apps play needs the microphone permission, then Android's own consent.
    val startPhone = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) context.startActivity(Intent(context, CaptureConsentActivity::class.java))
    }
    val alignMic = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) align(context, phone.on)
    }
    val toolMic = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) PcVoiceAccess.setTool(context, true, installCable = voice.canSetUp)
    }
    fun phoneSourceOn() {
        if (PcVoiceAccess.hasMicPermission(context)) context.startActivity(Intent(context, CaptureConsentActivity::class.java))
        else startPhone.launch(Manifest.permission.RECORD_AUDIO)
    }
    val source = if (phone.on || bt.on || bt.busy || mirror) "phone" else "pc"
    val on = state.on || phone.on || bt.on

    // A speaker that has sat on "starting" for a while says so, rather than spinning for ever.
    var startedAt by remember { mutableStateOf(0L) }
    LaunchedEffect(on) { startedAt = if (on) System.currentTimeMillis() else 0L }
    val stuck = on && startedAt > 0 && state.devices.isNotEmpty() &&
        state.devices.filter { it.enabled }.none { it.status == "playing" } &&
        System.currentTimeMillis() - startedAt > 15_000

    if (advanced == 1) {
        AdvancedSound(state, phone.on, onBack = { advanced = 0 })
        return
    }
    LincScaffold(title = "Sound", subtitle = (connection as? CompanionStateHolder.ServiceState.Connected)?.host, onBack = onBack, onSettings = onSettings) { inner ->
        LazyColumn(contentPadding = pagePadding(inner), verticalArrangement = Arrangement.spacedBy(Kit.groupGap)) {
            // ---- the PC's volume and what's playing -----------------------------------------
            item { PcVolumeCard() }

            // ---- play everywhere --------------------------------------------------------------
            item { SectionHeader("Play everywhere") }
            item {
                Surface(shape = RoundedCornerShape(28.dp), color = if (on) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceContainer,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter)) {
                    Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Box(Modifier.size(44.dp).clip(MaterialShapes.Cookie9Sided.toShape())
                                .background(if (on) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.secondaryContainer),
                                contentAlignment = Alignment.Center) {
                                Icon(LincSymbols.SpeakerGroup, contentDescription = null,
                                    tint = if (on) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSecondaryContainer)
                            }
                            Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                                Text(if (on) "Playing everywhere" else "Every speaker at once", style = MaterialTheme.typography.titleMedium)
                                Text(
                                    when {
                                        bt.on -> "This phone's sound plays on the PC too, the way the mirror carries it. Nothing is recorded."
                                        bt.busy -> "Bringing this phone's sound to the PC…"
                                        phone.on -> "This phone's sound, on the speakers below, in step with ${phone.nativeName}."
                                        mirror -> "This phone's sound, on the speakers below and on this phone, together."
                                        state.on -> "The PC's sound, on the speakers below, together."
                                        else -> "PC speakers, Bluetooth and this phone play the same sound at the same moment."
                                    },
                                    style = MaterialTheme.typography.bodyMedium,
                                )
                            }
                            Switch(checked = on, enabled = source == "phone" || ready, onCheckedChange = {
                                when {
                                    it && source == "pc" -> AudioEverywhere.set(context, true)
                                    !it && bt.on -> PcTools.setBtAudio(context, false)
                                    !it && phone.on -> PhoneSource.stop(context, "turned off")
                                    !it -> AudioEverywhere.set(context, false)
                                }
                            })
                        }
                        Text("Whose sound", style = MaterialTheme.typography.labelLarge)
                        ConnectedChoice(
                            options = listOf("pc" to "The PC's", "phone" to "This phone's"),
                            selected = source,
                            onSelect = { chosen ->
                                // A v31 PC takes this phone's sound as Mirror screen does and plays it on
                                // every speaker, this one included: nothing to record, no prompt.
                                if (chosen == "phone" && mirrorReady && !phone.on) {
                                    if (bt.on || bt.busy) PcTools.setBtAudio(context, false)
                                    AudioEverywhere.setMirrorSource(context, true)
                                }
                                else if (chosen == "pc" && mirror) AudioEverywhere.setMirrorSource(context, false)
                                // A v29 PC plays this phone over Bluetooth: nothing to record, no prompt.
                                else if (chosen == "phone" && btReady) {
                                    if (state.on) AudioEverywhere.set(context, false)
                                    PcTools.setBtAudio(context, true)
                                }
                                else if (chosen == "phone" && !phone.on) explainPhone = true
                                else if (chosen == "pc" && (bt.on || bt.busy)) PcTools.setBtAudio(context, false)
                                else if (chosen == "pc" && phone.on) {
                                    PhoneSource.stop(context, "switched to the PC's sound")
                                    if (ready) AudioEverywhere.set(context, true)
                                }
                            },
                        )
                        if (!ready && source == "pc") HelperText("Connect the PC (and update Linc there) to play its sound. The phone's sound works without it.",
                            kind = Explanations.Kind.Blocked)
                        if (state.error?.let { it in MIRROR_ERRORS } == true) {
                            HelperText(mirrorError(state.error), kind = Explanations.Kind.Error)
                            if (state.error != "mirror-stopped") TextButton(onClick = { explainPhone = true }) { Text("Record it instead") }
                        }
                        else PcSpeaker.explain(state.error)?.let { HelperText(it, kind = Explanations.Kind.Error) }
                        (phone.note ?: state.note)?.let { HelperText(it, kind = Explanations.Kind.Warning) }
                        if (stuck) HelperText("Still getting the speakers ready. If it stays like this, send the logs from Settings → Logs on both the phone and the PC.",
                            kind = Explanations.Kind.Warning)
                        if (on && state.devices.isNotEmpty() && state.devices.none { it.calibrated } && state.testing == null && !phone.on) {
                            HelperText("Tap Align once so Bluetooth and the phone line up exactly.", kind = Explanations.Kind.Warning)
                        }
                    }
                }
            }
            // Over Bluetooth, Android and Windows keep it together: no speakers to line up here.
            if (on && !bt.on) {
                item { SectionHeader("Speakers") }
                if (phone.on) {
                    item { NativeRow(phone.nativeName) }
                    val outputs = AudioOutputs.list(context).filter { it.name != phone.nativeName }
                    for (o in outputs) item(o.id) {
                        SpeakerRow(
                            name = o.name, icon = if (o.bluetooth) LincSymbols.Headphones else LincSymbols.Smartphone,
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
                        HelperText("Looking for speakers…", modifier = Modifier.padding(horizontal = Kit.gutter))
                    }
                    for (d in state.devices) item(d.id) { DeviceSpeakerRow(d) }
                }
                item {
                    Row(Modifier.fillMaxWidth().padding(horizontal = Kit.gutter, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        Button(
                            onClick = { if (PcVoiceAccess.hasMicPermission(context)) align(context, phone.on) else alignMic.launch(Manifest.permission.RECORD_AUDIO) },
                            enabled = state.testing == null && !state.listening && !phone.listening,
                            modifier = Modifier.weight(1f),
                        ) { Text(if (state.listening || phone.listening) "Listening…" else "Align") }
                        OutlinedButton(onClick = { advanced = 1 }, modifier = Modifier.weight(1f)) { Text("Timing") }
                    }
                }
                item {
                    HelperText(if (phone.on) "Align plays a short sweep and times the copies against ${phone.nativeName} with the microphone. Pause your music first."
                        else "Align plays a short sweep on each speaker and times it with this phone's microphone. Hold the phone where you'll listen.",
                        modifier = Modifier.padding(horizontal = Kit.gutter))
                }
            }

            // ---- where the PC's sound comes out ------------------------------------------------
            val list = outputs
            if (!on && list != null && list.isNotEmpty()) {
                item { SectionHeader("The PC plays on") }
                item {
                    SettingsGroup(rows = list.map { o ->
                        { seg ->
                            SettingsRow(
                                title = o.name, segment = seg,
                                icon = if (o.bluetooth) LincSymbols.Headphones else LincSymbols.Speaker,
                                iconContainer = if (o.isDefault) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.secondaryContainer,
                                iconTint = if (o.isDefault) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSecondaryContainer,
                                value = if (o.isDefault) "Playing here" else null,
                                onClick = { PcTools.setOutput(o.id) },
                                trailing = { RadioButton(selected = o.isDefault, onClick = { PcTools.setOutput(o.id) }) },
                            )
                        }
                    })
                }
            }

            // ---- phone and PC ---------------------------------------------------------------
            item { SectionHeader("This phone and the PC") }
            item {
                val speaker by PcSpeaker.state.collectAsState()
                val playing by AudioReceiver.playing.collectAsState()
                // The phone's sound on the PC, with the PC as its Bluetooth speaker (v29).
                val btRow: (@Composable (Segment) -> Unit)? = if (btReady) ({ seg ->
                        SettingsRow(
                            title = "This phone's sound on the PC", segment = seg, icon = LincSymbols.Speaker,
                            supporting = when {
                                mirror -> "Play everywhere already plays this phone's sound on the PC."
                                bt.busy -> "Starting…"
                                bt.on -> "On: this phone's sound plays on the PC and here too."
                                !bt.supported -> "This PC's Windows can't be a Bluetooth speaker (it needs Windows 10 version 2004 or later)."
                                else -> "Music and videos play on the PC's speakers as well, the way Mirror screen carries the sound. Nothing is recorded."
                            },
                            supportingKind = if (!bt.supported) Explanations.Kind.Blocked else Explanations.Kind.Helper,
                            enabled = bt.supported && !bt.busy && !mirror,
                            trailing = { Switch(checked = bt.on || bt.busy, enabled = bt.supported && !bt.busy && !mirror, onCheckedChange = { PcTools.setBtAudio(context, it) }) },
                        )
                    }) else null
                SettingsGroup(rows = listOfNotNull<@Composable (Segment) -> Unit>(
                    btRow,
                    { seg ->
                        SettingsRow(
                            title = "Phone as the PC's speaker", segment = seg, icon = LincSymbols.Smartphone,
                            supporting = when {
                                !PcSpeaker.ready() -> "Update Linc on the PC to play its sound here."
                                state.on -> "Play everywhere already plays the PC's sound here."
                                speaker.on && playing -> "On: the PC's sound plays on this phone, not the PC."
                                speaker.on -> "On: waiting for the PC's sound…"
                                else -> "The PC's sound on this phone instead of the PC's own speakers."
                            },
                            supportingKind = if (!PcSpeaker.ready()) Explanations.Kind.Blocked else Explanations.Kind.Helper,
                            enabled = PcSpeaker.ready() && !state.on,
                            trailing = { Switch(checked = speaker.on, enabled = PcSpeaker.ready() && !state.on, onCheckedChange = { PcSpeaker.set(it) }) },
                        )
                    },
                    { seg ->
                        val micReady = PcVoiceAccess.micReady()
                        SettingsRow(
                            title = "Phone as the PC's microphone", segment = seg, icon = LincSymbols.Mic,
                            supporting = if (voice.tool && micLive) "On: calls, dictation (Win+H) and every app on the PC hear this phone."
                                else "For calls, dictation and any app on the PC.",
                            enabled = micReady && !voice.settingUp,
                            trailing = {
                                Switch(checked = voice.tool, enabled = micReady && !voice.settingUp, onCheckedChange = { want ->
                                    if (!want) PcVoiceAccess.setTool(context, false)
                                    else if (PcVoiceAccess.hasMicPermission(context)) PcVoiceAccess.setTool(context, true, installCable = voice.canSetUp)
                                    else toolMic.launch(Manifest.permission.RECORD_AUDIO)
                                })
                            },
                        )
                    },
                ))
                PcSpeaker.explain(speaker.error)?.let {
                    HelperText(it, kind = Explanations.Kind.Error, modifier = Modifier.padding(horizontal = Kit.gutter, vertical = 4.dp))
                }
                when (bt.error) {
                    null -> {}
                    "not-paired" -> Column(Modifier.padding(horizontal = Kit.gutter, vertical = 4.dp)) {
                        HelperText("Pair this phone with the PC over Bluetooth once: open Bluetooth here, then on the PC Settings › Bluetooth & devices › Add device. Then turn this on again.",
                            kind = Explanations.Kind.Warning)
                        TextButton(onClick = {
                            runCatching { context.startActivity(Intent(android.provider.Settings.ACTION_BLUETOOTH_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
                        }) { Text("Open Bluetooth") }
                    }
                    "unsupported" -> {}
                    "needs-android-13" -> HelperText("Playing on the PC and the phone at once needs Android 13 or later.",
                        kind = Explanations.Kind.Error, modifier = Modifier.padding(horizontal = Kit.gutter, vertical = 4.dp))
                    else -> HelperText("The PC couldn't play this phone's sound (${bt.error}). Plug in the cable or turn on Wireless debugging, then try again.",
                        kind = Explanations.Kind.Error, modifier = Modifier.padding(horizontal = Kit.gutter, vertical = 4.dp))
                }
            }
        }
    }

    if (explainPhone) {
        LincAlertDialog(
            onDismissRequest = { explainPhone = false },
            icon = { Icon(LincSymbols.GraphicEq, contentDescription = null) },
            title = { Text("Play this phone's sound everywhere") },
            text = {
                Text("Android lets an app hear what other apps play only through its screen-recording prompt, so it will ask " +
                    "to \"start recording or casting\". Linc takes the sound only — never the picture — and stops when you turn this off. " +
                    "If it offers a choice, pick the entire screen.")
            },
            confirmButton = {
                TextButton(onClick = {
                    explainPhone = false
                    if (state.on) AudioEverywhere.set(context, false)
                    phoneSourceOn()
                }) { Text("Continue") }
            },
            dismissButton = { TextButton(onClick = { explainPhone = false }) { Text("Cancel") } },
        )
    }
}

/** The PC's master volume, mute, and what it's playing, as Sound's first card. */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun PcVolumeCard() {
    val pcState by PcControl.state.collectAsState()
    val media by PcMediaStore.state.collectAsState()
    val connection by CompanionStateHolder.state.collectAsState()
    val ready = controlsReason(connection) == null
    val playing = media?.present == true
    Surface(shape = RoundedCornerShape(28.dp), color = MaterialTheme.colorScheme.surfaceContainerHigh,
        modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter)) {
        Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.size(56.dp).clip(MaterialShapes.Cookie12Sided.toShape()).background(MaterialTheme.colorScheme.tertiaryContainer),
                    contentAlignment = Alignment.Center) {
                    Icon(LincSymbols.MusicNote, contentDescription = null, tint = MaterialTheme.colorScheme.onTertiaryContainer)
                }
                Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                    Text(if (playing) media?.title?.takeIf { it.isNotBlank() } ?: "Something is playing" else "Nothing playing on the PC",
                        style = MaterialTheme.typography.titleMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    val sub = listOfNotNull(media?.artist?.takeIf { it.isNotBlank() && playing }, media?.app?.takeIf { it.isNotBlank() && playing })
                    Text(if (sub.isNotEmpty()) sub.joinToString(" · ") else "The PC's volume", style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                if (playing) {
                    IconButton(onClick = { PcMediaControl.send("prev") }) { Icon(LincSymbols.SkipPrevious, contentDescription = "Previous") }
                    FilledIconButton(onClick = { PcMediaControl.send(if (media?.playing == true) "pause" else "play") }) {
                        Icon(if (media?.playing == true) LincSymbols.Pause else LincSymbols.Play, contentDescription = if (media?.playing == true) "Pause" else "Play")
                    }
                    IconButton(onClick = { PcMediaControl.send("next") }) { Icon(LincSymbols.SkipNext, contentDescription = "Next") }
                }
            }
            var volume by remember(pcState?.volume) { mutableFloatStateOf((pcState?.volume ?: 50).toFloat()) }
            Row(verticalAlignment = Alignment.CenterVertically) {
                IconToggleButton(checked = pcState?.muted == true, onCheckedChange = { PcControl.volumeMute() }, enabled = ready) {
                    Icon(if (pcState?.muted == true) LincSymbols.VolumeOff else LincSymbols.VolumeUp,
                        contentDescription = if (pcState?.muted == true) "Unmute the PC" else "Mute the PC")
                }
                Slider(
                    value = volume, onValueChange = { volume = it }, onValueChangeFinished = { PcControl.volumeSet(volume.roundToInt()) },
                    valueRange = 0f..100f, enabled = ready, modifier = Modifier.weight(1f).semantics { contentDescription = "PC volume" },
                )
                Text("${volume.roundToInt()}", style = MaterialTheme.typography.labelLarge, modifier = Modifier.width(36.dp))
            }
            controlsReason(connection)?.let { HelperText(it, kind = Explanations.Kind.Blocked) }
        }
    }
}

/** Errors from the v31 "mirror" source (the PC taking this phone's sound with scrcpy). */
private val MIRROR_ERRORS = setOf("needs-adb", "needs-android-13", "mirror-stopped")

private fun mirrorError(error: String?): String = when (error) {
    "needs-adb" -> "Taking this phone's sound needs the USB cable or Wireless debugging, as Mirror screen does."
    "needs-android-13" -> "Taking this phone's sound this way needs Android 13 or later."
    else -> "The PC stopped getting this phone's sound. Check the cable or Wireless debugging, then choose This phone's again."
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
    d.status == "clock" -> "Syncing with the PC's clock…"
    d.status == "off" -> "Waiting for the sound to arrive…"
    else -> "Getting ready…"
}

// ---- Advanced ---------------------------------------------------------------------------------

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun AdvancedSound(state: EverywhereState, phoneSource: Boolean, onBack: () -> Unit) {
    LincScaffold(title = "Timing", subtitle = "Play everywhere", onBack = onBack) { inner ->
        LazyColumn(contentPadding = pagePadding(inner), verticalArrangement = Arrangement.spacedBy(Kit.groupGap)) {
            item {
                HelperText(
                    if (phoneSource) "Every copy aims for the moment the phone's own output plays the sound: ${PhoneSource.state.value.delayMs} ms after apps play it."
                    else if (state.source == "mirror") "Every speaker, this phone's included, plays ${state.delayMs} ms after this phone's sound reaches the PC, so the slowest can keep up."
                    else "Every speaker plays ${state.delayMs} ms after the PC, so the slowest can keep up. Sound in PC Remote follows the picture instead.",
                    modifier = Modifier.padding(horizontal = Kit.gutter),
                )
            }
            if (!phoneSource) {
                item { SectionHeader("Listen") }
                item {
                    FlowRow(Modifier.padding(horizontal = Kit.gutter), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        FilledTonalButton(onClick = { AudioEverywhere.clicks() }, enabled = state.testing == null) { Text("Play test clicks") }
                        FilledTonalButton(onClick = { AudioEverywhere.resync() }, enabled = state.on && state.testing == null) { Text("Re-sync") }
                        TextButton(onClick = { AudioEverywhere.resetAlignment() }) { Text("Reset alignment") }
                    }
                }
                item { HelperText("Test clicks: every speaker clicks at once. Nudge any that's early or late below. Re-sync puts every speaker back in step if one has drifted.", modifier = Modifier.padding(horizontal = Kit.gutter)) }
                item { SectionHeader("Nudge each speaker") }
                for (d in state.devices.filter { it.enabled }) item(d.id) { NudgeRow(d) }
                item { SectionHeader("Buffer") }
                item { MarginRow(state.marginMs) }
            } else {
                item { SectionHeader("Speakers on the PC") }
                for (d in state.devices.filter { it.kind == "pc" && it.enabled }) item(d.id) { NudgeRow(d) }
            }
        }
    }
}

/** Each speaker's offset: −400…+400 ms in 10 ms steps, saved per device on the PC (audio-everywhere.json). */
private const val NUDGE_MAX_MS = 400f
private const val NUDGE_STEP_MS = 10f

@Composable
private fun NudgeRow(d: EverywhereDevice) {
    var nudge by remember(d.id, d.manualMs) { mutableFloatStateOf(d.manualMs.toFloat().coerceIn(-NUDGE_MAX_MS, NUDGE_MAX_MS)) }
    fun commit(value: Float) {
        nudge = ((value / NUDGE_STEP_MS).roundToInt() * NUDGE_STEP_MS).coerceIn(-NUDGE_MAX_MS, NUDGE_MAX_MS)
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
            IconButton(onClick = { commit(nudge - NUDGE_STEP_MS) }) { Icon(LincSymbols.ArrowLeft, contentDescription = "10 ms later") }
            Slider(value = nudge, onValueChange = { nudge = it }, onValueChangeFinished = { commit(nudge) },
                valueRange = -NUDGE_MAX_MS..NUDGE_MAX_MS, steps = ((2 * NUDGE_MAX_MS / NUDGE_STEP_MS).toInt() - 1),
                modifier = Modifier.weight(1f).semantics { contentDescription = "${d.name} alignment" })
            IconButton(onClick = { commit(nudge + NUDGE_STEP_MS) }) { Icon(LincSymbols.ArrowRight, contentDescription = "10 ms earlier") }
        }
    }
}

/** Smooth adds this much to the group delay (the PC's marginMs); Tight adds none. */
private const val SMOOTH_MARGIN_MS = 150

@Composable
private fun MarginRow(marginMs: Int) {
    Column(Modifier.padding(horizontal = Kit.gutter), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        ConnectedChoice(
            options = listOf("tight" to "Tight", "smooth" to "Smooth"),
            selected = if (marginMs >= SMOOTH_MARGIN_MS / 2) "smooth" else "tight",
            onSelect = { AudioEverywhere.setMargin(if (it == "smooth") SMOOTH_MARGIN_MS else 0) },
        )
        HelperText("Tight: as little delay as the speakers allow. Smooth: every speaker waits $SMOOTH_MARGIN_MS ms longer, together, so a busy Wi-Fi doesn't break the sound up.")
    }
}
