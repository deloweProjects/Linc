package app.linc.android.ui

import android.Manifest
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Switch
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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import app.linc.android.service.CompanionStateHolder
import app.linc.android.service.MirrorSettings
import app.linc.android.service.PcVoiceAccess
import app.linc.android.ui.theme.Dimens

/**
 * Tools → Voice Access (v21, D-070): start Windows Voice Access on the PC — and hear it through
 * this phone's microphone, so you can talk to the PC from across the room. The same switch is
 * in PC Remote's bubble.
 */
@Composable
fun VoiceAccessCard() {
    val context = LocalContext.current
    val connection by CompanionStateHolder.state.collectAsState()
    val state by PcVoiceAccess.state.collectAsState()
    val micLive by PcVoiceAccess.micLive.collectAsState()
    var phoneMic by remember { mutableStateOf(MirrorSettings.voicePhoneMic(context)) }
    var askSetup by remember { mutableStateOf(false) }
    /** Start with the phone's mic; first offer to set the PC up for it if it can't hear one yet. */
    fun startWithPhoneMic() {
        if (PcVoiceAccess.state.value.canSetUp) askSetup = true
        else PcVoiceAccess.set(context, true, phoneMic = true)
    }
    val askMic = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) startWithPhoneMic()
    }
    if (askSetup) {
        AlertDialog(
            onDismissRequest = { askSetup = false },
            title = { Text("Set up the microphone on the PC?") },
            text = { Text(PcVoiceAccess.SETUP_EXPLANATION) },
            confirmButton = {
                TextButton(onClick = {
                    askSetup = false
                    PcVoiceAccess.set(context, true, phoneMic = true, installCable = true)
                }) { Text("Set up") }
            },
            dismissButton = {
                TextButton(onClick = {
                    askSetup = false
                    PcVoiceAccess.set(context, true, phoneMic = false)
                }) { Text("Use the PC's mic") }
            },
        )
    }
    LaunchedEffect(connection) { PcVoiceAccess.refresh(context) }

    val ready = PcVoiceAccess.ready()
    LincCard(title = "Voice Access", modifier = Modifier.padding(bottom = Dimens.l)) {
        Column(verticalArrangement = Arrangement.spacedBy(Dimens.s)) {
            Text(
                when {
                    !ready -> "Update Linc on the PC to use Voice Access from here."
                    !state.available -> "This PC doesn't have Windows Voice Access (Windows 11 22H2 or later)."
                    state.settingUp -> state.setupText
                    state.on && micLive -> "On — the PC is listening through this phone. Try \"open Edge\" or \"show numbers\"."
                    state.on -> "On — the PC is listening through its own microphone."
                    else -> "Control the PC by voice: \"click Start\", \"scroll down\", \"type hello\"."
                },
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("Use this phone as the microphone", style = MaterialTheme.typography.bodyLarge)
                    if (phoneMic && ready && !state.cable && !state.settingUp) {
                        Text(
                            if (state.canSetUp) "The PC needs a virtual microphone first — Linc sets it up when you start."
                            else "Update Linc on the PC so it can set up the microphone by itself.",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.tertiary,
                        )
                    }
                }
                Switch(checked = phoneMic, onCheckedChange = {
                    phoneMic = it
                    MirrorSettings.setVoicePhoneMic(context, it)
                })
            }
            Button(
                enabled = ready && state.available && !state.settingUp,
                modifier = Modifier.fillMaxWidth().heightIn(min = Dimens.touchTarget),
                colors = if (state.on) ButtonDefaults.filledTonalButtonColors() else ButtonDefaults.buttonColors(),
                onClick = {
                    val on = !state.on
                    if (on && phoneMic && !PcVoiceAccess.hasMicPermission(context)) {
                        askMic.launch(Manifest.permission.RECORD_AUDIO)
                    } else if (on && phoneMic) {
                        startWithPhoneMic()
                    } else {
                        PcVoiceAccess.set(context, on, phoneMic)
                    }
                },
            ) {
                Icon(if (micLive) LincSymbols.Mic else LincSymbols.VoiceAccess, null, Modifier.size(18.dp))
                Spacer(Modifier.size(8.dp))
                Text(if (state.on) "Stop Voice Access" else "Start Voice Access")
            }
            state.error?.let {
                Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
            }
        }
    }
}
