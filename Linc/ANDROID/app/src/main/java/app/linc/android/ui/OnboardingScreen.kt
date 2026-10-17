package app.linc.android.ui

import android.content.ActivityNotFoundException
import android.content.Intent
import android.provider.Settings
import androidx.annotation.StringRes
import kotlinx.coroutines.delay
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.material3.Button
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import app.linc.android.R
import app.linc.android.service.CompanionStateHolder
import app.linc.android.service.CompanionStateHolder.ServiceState
import app.linc.android.ui.theme.Dimens

private data class OnboardStep(
    @StringRes val title: Int,
    @StringRes val body: Int,
    @StringRes val actionLabel: Int? = null,
    val settingsAction: String? = null,
    /** Show live "is it paired yet?" checks under the instructions. */
    val pairChecks: Boolean = false,
)

private val steps = listOf(
    OnboardStep(R.string.onboard_welcome_title, R.string.onboard_welcome_body),
    OnboardStep(
        R.string.onboard_devoptions_title, R.string.onboard_devoptions_body,
        R.string.onboard_devoptions_action, Settings.ACTION_DEVICE_INFO_SETTINGS,
    ),
    OnboardStep(
        R.string.onboard_wireless_title, R.string.onboard_wireless_body,
        R.string.onboard_open_devsettings_action, Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS,
    ),
    OnboardStep(
        R.string.onboard_pair_title, R.string.onboard_pair_body,
        R.string.onboard_open_devsettings_action, Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS,
        pairChecks = true,
    ),
    OnboardStep(R.string.onboard_done_title, R.string.onboard_done_body),
)

@Composable
fun OnboardingScreen(onDone: () -> Unit) {
    val context = LocalContext.current
    var stepIndex by rememberSaveable { mutableIntStateOf(0) }
    val step = steps[stepIndex]
    val isLast = stepIndex == steps.lastIndex

    LincScreen(title = stringResource(step.title)) {
        LinearProgressIndicator(
            progress = { (stepIndex + 1f) / steps.size },
            modifier = Modifier.fillMaxWidth(),
        )
        LincCard {
            Text(stringResource(step.body), style = MaterialTheme.typography.bodyMedium)
            if (step.pairChecks) PairChecks()
            if (step.actionLabel != null && step.settingsAction != null) {
                OutlinedButton(
                    modifier = Modifier.fillMaxWidth().heightIn(min = Dimens.touchTarget),
                    onClick = {
                        try {
                            context.startActivity(Intent(step.settingsAction))
                        } catch (_: ActivityNotFoundException) {
                            // Some OEM builds hide these screens; the text instructions still apply.
                        }
                    },
                ) {
                    Text(stringResource(step.actionLabel))
                }
            }
        }
        Row(
            modifier = Modifier.fillMaxWidth().heightIn(min = Dimens.touchTarget),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            if (stepIndex > 0) {
                TextButton(onClick = { stepIndex-- }) { Text(stringResource(R.string.onboard_back)) }
            } else {
                Spacer(modifier = Modifier.weight(1f))
            }
            Button(onClick = { if (isLast) onDone() else stepIndex++ }) {
                Text(stringResource(if (isLast) R.string.onboard_finish else R.string.onboard_next))
            }
        }
    }
}

/**
 * Live answers to "did pairing work?": whether Wireless debugging is switched on, and whether the
 * PC has actually reached Linc. Re-checked every second while this step is showing; advisory
 * only, so Next is never blocked (USB, or pairing later, are both fine).
 */
@Composable
private fun PairChecks() {
    val context = LocalContext.current
    val connection by CompanionStateHolder.state.collectAsState()
    var wirelessOn by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        while (true) {
            wirelessOn = runCatching {
                Settings.Global.getInt(context.contentResolver, "adb_wifi_enabled", 0) == 1
            }.getOrDefault(false)
            delay(1_000)
        }
    }
    val linked = connection is ServiceState.Connected
    Column(verticalArrangement = Arrangement.spacedBy(Dimens.xs)) {
        CheckLine(ok = wirelessOn || linked,
            text = stringResource(if (wirelessOn) R.string.onboard_pair_check_wireless_on else R.string.onboard_pair_check_wireless_off))
        CheckLine(ok = linked,
            text = stringResource(if (linked) R.string.onboard_pair_check_linked else R.string.onboard_pair_check_waiting))
    }
}

@Composable
private fun CheckLine(ok: Boolean, text: String) {
    Text(
        (if (ok) "✓  " else "•  ") + text,
        style = MaterialTheme.typography.bodyMedium,
        color = if (ok) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
    )
}
