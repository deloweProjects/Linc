package app.linc.android.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.linc.android.R
import app.linc.android.ui.theme.Dimens

/**
 * M17b B4 — the OPTIONAL update card. Dismissible: *Skip this version* records that one version,
 * so the next release asks again.
 *
 * Uses M17a's design system (`LincCard`, `Dimens`) rather than a look of its own.
 */
@Composable
fun UpdateAvailableCard(
    version: String?,
    notes: String?,
    problem: String?,
    onUpdate: () -> Unit,
    onSkip: () -> Unit,
    modifier: Modifier = Modifier,
) {
    LincCard(title = stringResource(R.string.update_available_title), modifier = modifier) {
        if (!version.isNullOrBlank()) {
            Text(version, style = MaterialTheme.typography.bodyMedium)
        }
        if (!notes.isNullOrBlank()) {
            Text(
                notes,
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        if (!problem.isNullOrBlank()) {
            // Plain language only — a refused download says what happened, never a hash or a code.
            Text(problem, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.error)
        }
        Row(horizontalArrangement = Arrangement.spacedBy(Dimens.s)) {
            Button(
                onClick = onUpdate,
                modifier = Modifier.heightIn(min = Dimens.touchTarget),
            ) {
                Text(stringResource(R.string.update_action))
            }
            TextButton(
                onClick = onSkip,
                modifier = Modifier.heightIn(min = Dimens.touchTarget),
            ) {
                Text(stringResource(R.string.update_skip))
            }
        }
    }
}

/**
 * M17b B4 — the FORCED update screen. It cannot be dismissed and has no Skip button.
 *
 * It blocks the app's FUNCTION, not the process: this is drawn as a normal screen, so the system
 * Back button and the launcher still behave normally and the user is never trapped.
 */
@Composable
fun UpdateRequiredScreen(
    notes: String?,
    problem: String?,
    onUpdate: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Surface(modifier = modifier.fillMaxSize(), color = MaterialTheme.colorScheme.surface) {
        Column(
            modifier = Modifier.fillMaxSize().padding(Dimens.xl),
            verticalArrangement = Arrangement.spacedBy(Dimens.l, Alignment.CenterVertically),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Text(
                stringResource(R.string.update_forced_title),
                style = MaterialTheme.typography.headlineSmall,
            )
            Text(
                stringResource(R.string.update_forced_body),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            if (!notes.isNullOrBlank()) {
                Text(
                    notes,
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            if (!problem.isNullOrBlank()) {
                Text(problem, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.error)
            }
            Button(
                onClick = onUpdate,
                modifier = Modifier.fillMaxWidth().heightIn(min = Dimens.touchTarget),
            ) {
                Text(stringResource(R.string.update_action))
            }
            Text(
                stringResource(R.string.update_forced_closable),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}
