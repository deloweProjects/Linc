package app.linc.android.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import app.linc.android.service.AudioEverywhere
import app.linc.android.service.AudioReceiver
import app.linc.android.service.PcSpeaker
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.SectionHeader

/**
 * Tools → Media → Phone as PC speaker (v25, D-093): the PC's sound plays here instead of on the
 * PC, so it is never heard twice and out of step.
 */
@Composable
fun PcSpeakerSection() {
    val state by PcSpeaker.state.collectAsState()
    val playing by AudioReceiver.playing.collectAsState()
    val ready = PcSpeaker.ready()
    // Play everywhere (v26) already plays the PC's sound here, alongside its own speakers.
    val everywhere by AudioEverywhere.state.collectAsState()
    SectionHeader("Phone as PC speaker")
    Column(Modifier.fillMaxWidth().padding(horizontal = Kit.gutter), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                when {
                    !ready -> "Update Linc on the PC to play its sound here."
                    everywhere.on -> "Play everywhere is on, which already plays the PC's sound here."
                    state.on && playing -> "On: the PC's sound is playing on this phone."
                    state.on -> "On: waiting for the PC's sound…"
                    else -> "Play the PC's sound on this phone instead of its own speakers."
                },
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.weight(1f),
            )
            Switch(checked = state.on, enabled = ready && !everywhere.on, onCheckedChange = { PcSpeaker.set(it) })
        }
        PcSpeaker.explain(state.error)?.let {
            Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
        }
    }
}
