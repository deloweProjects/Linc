package app.linc.android.ui

import android.text.format.DateUtils
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
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
import app.linc.android.service.PcGate
import app.linc.android.service.TransportStore
import app.linc.android.ui.kit.HelperText
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.SettingsGroup
import app.linc.android.ui.kit.SettingsRow

/**
 * The PCs this phone knows (D-088), in the Connection sheet: pick the one to connect to, or
 * "Any of my PCs" (whichever finds the phone first), and forget the ones you no longer use.
 * Linc is with one PC at a time either way; picking a PC drops a link to any other at once.
 */
@Composable
fun PcsSection() {
    val context = LocalContext.current
    var pcs by remember { mutableStateOf(TransportStore.pcs(context)) }
    var selected by remember { mutableStateOf(TransportStore.selectedPcId(context)) }
    var forgetting by remember { mutableStateOf<TransportStore.KnownPc?>(null) }
    val connectedName by PcGate.connectedName.collectAsState()

    fun refresh() {
        pcs = TransportStore.pcs(context)
        selected = TransportStore.selectedPcId(context)
    }
    LaunchedEffect(Unit) {
        while (true) {
            refresh()
            kotlinx.coroutines.delay(2_000)
        }
    }
    fun pick(id: String?) {
        PcGate.instance?.pick(id) ?: TransportStore.selectPc(context, id)
        refresh()
    }

    if (pcs.isEmpty()) return
    Column {
        Text("PCs", style = MaterialTheme.typography.titleMedium,
            modifier = Modifier.padding(horizontal = Kit.gutter).padding(bottom = 8.dp))
        SettingsGroup(rows = buildList {
            add { s ->
                SettingsRow("Any of my PCs", s, LincSymbols.Devices,
                    supporting = "Whichever PC finds the phone first",
                    onClick = { pick(null) },
                    trailing = { RadioButton(selected == null, onClick = { pick(null) }) })
            }
            for (pc in pcs) add { s ->
                val connectedHere = connectedName != null && connectedName.equals(pc.name, ignoreCase = true)
                SettingsRow(pc.name, s, LincSymbols.Computer,
                    value = when {
                        connectedHere -> "Connected"
                        pc.lastSeen > 0 -> "Last connected " + DateUtils.getRelativeTimeSpanString(
                            pc.lastSeen, System.currentTimeMillis(), DateUtils.MINUTE_IN_MILLIS)
                        else -> null
                    },
                    onClick = { pick(pc.id) },
                    trailing = {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            IconButton(onClick = { forgetting = pc }) {
                                Icon(LincSymbols.Delete, contentDescription = "Forget ${pc.name}")
                            }
                            RadioButton(selected == pc.id, onClick = { pick(pc.id) })
                        }
                    })
            }
        })
        HelperText(
            "Linc is with one PC at a time. Another PC that tries meanwhile is told this phone is busy.",
            modifier = Modifier.padding(horizontal = Kit.gutter).padding(top = 8.dp),
        )
    }

    forgetting?.let { pc ->
        AlertDialog(
            onDismissRequest = { forgetting = null },
            icon = { Icon(LincSymbols.Delete, contentDescription = null) },
            title = { Text("Forget ${pc.name}?") },
            text = { Text("This phone stops connecting to it directly. To use it again, connect from Linc on that PC (USB or Add a phone).") },
            confirmButton = {
                TextButton(onClick = {
                    PcGate.instance?.forget(pc.id) ?: TransportStore.forgetPc(context, pc.id)
                    forgetting = null
                    refresh()
                }) { Text("Forget") }
            },
            dismissButton = { TextButton(onClick = { forgetting = null }) { Text("Cancel") } },
        )
    }
}
