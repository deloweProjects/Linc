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
 * Tools → Microphone (v23, D-075): this phone as the PC's microphone for any app — a call,
 * dictation, a recorder — not just Voice Access. The same switch decides whether PC Remote uses the
 * phone's mic by itself whenever an app on the PC listens.
 */
@Composable
fun PhoneMicCard() {
    val context = LocalContext.current
    val connection by CompanionStateHolder.state.collectAsState()
    val state by PcVoiceAccess.state.collectAsState()
    val micLive by PcVoiceAccess.micLive.collectAsState()
    var inMirror by remember { mutableStateOf(MirrorSettings.voicePhoneMic(context)) }
    var askSetup by remember { mutableStateOf(false) }

    fun start() {
        if (PcVoiceAccess.state.value.canSetUp) askSetup = true
        else PcVoiceAccess.setTool(context, true)
    }
    val askMic = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) start()
    }
    LaunchedEffect(connection) { PcVoiceAccess.refresh(context) }

    if (askSetup) {
        AlertDialog(
            onDismissRequest = { askSetup = false },
            title = { Text("Set up the microphone on the PC?") },
            text = { Text(PcVoiceAccess.SETUP_EXPLANATION) },
            confirmButton = {
                TextButton(onClick = {
                    askSetup = false
                    PcVoiceAccess.setTool(context, true, installCable = true)
                }) { Text("Set up") }
            },
            dismissButton = { TextButton(onClick = { askSetup = false }) { Text("Not now") } },
        )
    }

    val ready = PcVoiceAccess.micReady()
    LincCard(title = "Microphone", modifier = Modifier.padding(bottom = Dimens.l)) {
        Column(verticalArrangement = Arrangement.spacedBy(Dimens.s)) {
            Text(
                when {
                    !ready -> "Update Linc on the PC to use this phone as its microphone."
                    state.settingUp -> state.setupText
                    state.tool && micLive -> "On — the PC is hearing this phone in every app: calls, dictation (Win+H), Voice Access."
                    state.tool -> "On — connecting to the PC…"
                    else -> "Use this phone as the PC's microphone, in any app."
                },
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("In Mirror PC, use this phone's mic", style = MaterialTheme.typography.bodyLarge)
                    Text(
                        "When an app on the PC starts listening, it hears this phone. Also in the Mirror PC bubble.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                Switch(checked = inMirror, onCheckedChange = {
                    inMirror = it
                    MirrorSettings.setVoicePhoneMic(context, it)
                })
            }
            Button(
                enabled = ready && !state.settingUp,
                modifier = Modifier.fillMaxWidth().heightIn(min = Dimens.touchTarget),
                colors = if (state.tool) ButtonDefaults.filledTonalButtonColors() else ButtonDefaults.buttonColors(),
                onClick = {
                    when {
                        state.tool -> PcVoiceAccess.setTool(context, false)
                        !PcVoiceAccess.hasMicPermission(context) -> askMic.launch(Manifest.permission.RECORD_AUDIO)
                        else -> start()
                    }
                },
            ) {
                Icon(LincSymbols.Mic, null, Modifier.size(18.dp))
                Spacer(Modifier.size(8.dp))
                Text(if (state.tool) "Stop using the phone's mic" else "Use the phone's mic on the PC")
            }
            state.error?.let {
                Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
            }
        }
    }
}
