package app.linc.android.ui.kit

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.Modifier
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import app.linc.android.ui.LincSymbols
import app.linc.android.ui.theme.LincTheme

// Light/dark previews of the kit (D-082). No screenshot library — these are for Android Studio.

@Composable
private fun KitSampler() {
    Surface(color = MaterialTheme.colorScheme.surface) {
        Column(Modifier.padding(vertical = 16.dp), verticalArrangement = Arrangement.spacedBy(Kit.groupGap)) {
            HeroCard {
                Text("Your PC", style = MaterialTheme.typography.headlineSmall)
                StatusChip("Connected", Quality.Good)
            }
            SectionHeader("Connection")
            SettingsGroup(rows = listOf(
                { s -> SettingsRow("Background connection", s, LincSymbols.Wifi, supporting = "Find the PC on Wi-Fi by itself.", trailing = { Switch(true, {}) }) },
                { s -> SettingsRow("Stay connected in the background", s, LincSymbols.BatteryAlert, supporting = "Android may cut the link when the screen is off.", onClick = {}, trailing = { Chevron() }) },
                { s -> SettingsRow("Allow USB", s, LincSymbols.Usb, value = "On") },
            ))
            TileGrid(tiles = listOf(
                { m -> ToolTile("Remote", LincSymbols.Remote, m, state = "Trackpad and keys", onClick = {}) },
                { m -> ToolTile("Voice Access", LincSymbols.VoiceAccess, m, state = "On", active = true, onClick = {}) },
                { m -> ToolTile("Microphone", LincSymbols.Mic, m, state = "Update the PC", enabled = false, onClick = {}) },
            ))
            ConnectedChoice(listOf(true to "Phone", false to "PC"), true, {}, Modifier.padding(horizontal = Kit.gutter))
            ProgressLine(0.42f, "Downloading the update", Modifier.padding(horizontal = Kit.gutter))
            BlockedState("Connect to a PC", "Linc hasn't been set up with a PC yet.", LincSymbols.Computer, action = "Set up", onAction = {})
            EmptyState("Nothing copied yet", "Text you copy on either device shows up here.", LincSymbols.Clipboard)
        }
    }
}

@Preview(name = "Kit — light", widthDp = 360, heightDp = 1500)
@Composable
private fun KitLight() = LincTheme(darkTheme = false) { KitSampler() }

@Preview(name = "Kit — dark", widthDp = 360, heightDp = 1500)
@Composable
private fun KitDark() = LincTheme(darkTheme = true) { KitSampler() }

@Preview(name = "Kit — explanations off", widthDp = 360, heightDp = 1500)
@Composable
private fun KitNoExplanations() = LincTheme {
    CompositionLocalProvider(LocalShowExplanations provides false) { KitSampler() }
}
