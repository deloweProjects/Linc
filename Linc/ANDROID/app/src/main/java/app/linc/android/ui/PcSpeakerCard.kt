package app.linc.android.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import app.linc.android.service.AudioReceiver
import app.linc.android.service.PcSpeaker
import app.linc.android.ui.theme.Dimens

/**
 * Tools → Phone as PC speaker (v22, D-076): the PC's sound plays here instead of on the PC, so
 * it is never heard twice and out of step.
 */
@Composable
fun PcSpeakerCard() {
    val state by PcSpeaker.state.collectAsState()
    val playing by AudioReceiver.playing.collectAsState()
    val ready = PcSpeaker.ready()
    LincCard(title = "Phone as PC speaker", modifier = Modifier.padding(bottom = Dimens.l)) {
        Column(verticalArrangement = Arrangement.spacedBy(Dimens.s)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    when {
                        !ready -> "Update Linc on the PC to play its sound here."
                        state.on && playing -> "On: the PC's sound is playing on this phone."
                        state.on -> "On: waiting for the PC's sound…"
                        else -> "Play the PC's sound on this phone instead of its own speakers."
                    },
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.weight(1f),
                )
                Switch(checked = state.on, enabled = ready, onCheckedChange = { PcSpeaker.set(it) })
            }
            PcSpeaker.explain(state.error)?.let {
                Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
            }
        }
    }
}
