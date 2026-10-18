@file:OptIn(androidx.compose.material3.ExperimentalMaterial3ExpressiveApi::class)

package app.linc.android.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import app.linc.android.service.Explanations
import app.linc.android.ui.kit.BlockedState
import app.linc.android.ui.kit.HelperText
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.ProgressLine

/**
 * The OPTIONAL update card on Home (M17b B4, wired to a real check in 1.0.0.11). Skip records one
 * version, so the next release asks again. [progress] is the download, 0..1, while it runs.
 */
@Composable
fun UpdateAvailableCard(
    version: String,
    notes: String?,
    problem: String?,
    progress: Float?,
    onUpdate: () -> Unit,
    onSkip: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Surface(
        color = MaterialTheme.colorScheme.secondaryContainer,
        contentColor = MaterialTheme.colorScheme.onSecondaryContainer,
        shape = RoundedCornerShape(Kit.heroRadius),
        modifier = modifier.fillMaxWidth().padding(horizontal = Kit.gutter),
    ) {
        Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Icon(LincSymbols.SystemUpdate, contentDescription = null)
                Text("Linc $version is available", style = MaterialTheme.typography.titleMedium)
            }
            if (!notes.isNullOrBlank()) Text(notes, style = MaterialTheme.typography.bodyMedium)
            if (progress != null) {
                ProgressLine(progress, "Downloading")
            } else {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(onClick = onUpdate, shapes = ButtonDefaults.shapes(), modifier = Modifier.heightIn(min = Kit.touch)) { Text("Update") }
                    TextButton(onClick = onSkip, modifier = Modifier.heightIn(min = Kit.touch)) { Text("Skip this version") }
                }
            }
            // Plain language only — a refused download says what happened, never a hash or a code.
            problem?.let { HelperText(it, kind = Explanations.Kind.Error) }
        }
    }
}

/**
 * The FORCED update screen (M17b B4). It blocks the app's function, not the process: Back and the
 * launcher still work, so nobody is trapped.
 */
@Composable
fun UpdateRequiredScreen(notes: String?, problem: String?, progress: Float?, onUpdate: () -> Unit, modifier: Modifier = Modifier) {
    Surface(modifier = modifier.fillMaxSize(), color = MaterialTheme.colorScheme.surface) {
        Column(
            Modifier.fillMaxSize().padding(24.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp, Alignment.CenterVertically),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            BlockedState(
                title = "Linc needs to update",
                reason = "This version can no longer connect to your PC. Update to keep using Linc.",
                icon = LincSymbols.SystemUpdate,
            )
            if (!notes.isNullOrBlank()) Text(notes, style = MaterialTheme.typography.bodyMedium, textAlign = TextAlign.Center)
            if (progress != null) ProgressLine(progress, "Downloading")
            else Button(onClick = onUpdate, shapes = ButtonDefaults.shapes(), modifier = Modifier.fillMaxWidth().heightIn(min = 56.dp)) { Text("Update") }
            problem?.let { HelperText(it, kind = Explanations.Kind.Error, textAlign = TextAlign.Center) }
            HelperText("You can still close Linc with Back or Home.", textAlign = TextAlign.Center)
        }
    }
}
