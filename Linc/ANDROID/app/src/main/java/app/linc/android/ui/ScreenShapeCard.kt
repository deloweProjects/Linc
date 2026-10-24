package app.linc.android.ui

import android.content.Context
import android.database.ContentObserver
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import app.linc.android.ui.kit.Kit

/**
 * "Fill this monitor" changes this phone's screen size and rotation for the PC mirror. The PC
 * puts them back when mirroring stops, and if the link drops, a small script the PC left on the
 * phone does it by itself a few seconds later (PC `Services/PhoneShapeGuard.cs`). While the screen
 * is changed, the PC's script keeps the global setting [ACTIVE] at "1"; this card shows then, and
 * its Reset button asks the script (setting [RESET]) to put the screen back now.
 */
object ScreenShape {
    const val ACTIVE = "linc_shape_active"
    const val RESET = "linc_shape_reset"

    fun active(context: Context): Boolean =
        runCatching { Settings.Global.getString(context.contentResolver, ACTIVE) == "1" }.getOrDefault(false)

    /** Asks the phone-side script to put the screen back. False when Linc lacks the permission. */
    fun requestReset(context: Context): Boolean =
        runCatching { Settings.Global.putString(context.contentResolver, RESET, "1") }.getOrDefault(false)
}

@Composable
fun ScreenShapeCard(modifier: Modifier = Modifier) {
    val context = LocalContext.current
    var active by remember { mutableStateOf(ScreenShape.active(context)) }
    var note by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()
    DisposableEffect(Unit) {
        val observer = object : ContentObserver(Handler(Looper.getMainLooper())) {
            override fun onChange(selfChange: Boolean) {
                active = ScreenShape.active(context)
                if (!active) note = null
            }
        }
        context.contentResolver.registerContentObserver(Settings.Global.getUriFor(ScreenShape.ACTIVE), false, observer)
        onDispose { context.contentResolver.unregisterContentObserver(observer) }
    }
    if (!active) return
    Surface(
        color = MaterialTheme.colorScheme.secondaryContainer,
        contentColor = MaterialTheme.colorScheme.onSecondaryContainer,
        shape = RoundedCornerShape(Kit.heroRadius),
        modifier = modifier.fillMaxWidth().padding(horizontal = Kit.gutter),
    ) {
        Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Icon(LincSymbols.Smartphone, contentDescription = null)
                Text("Screen set for the PC mirror", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
            }
            Text(
                note ?: "\"Fill this monitor\" changed this phone's screen size and rotation. It goes back by itself when mirroring ends; tap Reset to put it back now.",
                style = MaterialTheme.typography.bodyMedium,
            )
            Button(onClick = {
                if (!ScreenShape.requestReset(context)) {
                    note = "Linc can't do this on its own yet. Plug the phone into the PC once, or connect Linc on the PC: it puts the screen back."
                    return@Button
                }
                note = "Putting the screen back…"
                scope.launch {
                    delay(6_000)
                    if (ScreenShape.active(context)) {
                        note = "The phone restarted since, so it can't do this by itself. Connect Linc on the PC (or plug in the cable): it puts the screen back."
                    }
                }
            }) { Text("Reset screen") }
        }
    }
}
