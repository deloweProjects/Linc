package app.linc.android.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.text.style.TextAlign
import app.linc.android.ui.theme.Dimens

/**
 * M17a B1: the ONE card treatment. Tonal surface, no elevation, no outline — mixing those three
 * is most of why the app looked unfinished. Every card in the app is this composable.
 */
@Composable
fun LincCard(
    title: String? = null,
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit,
) {
    Surface(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(Dimens.radiusCard),
        color = MaterialTheme.colorScheme.surfaceContainer,
    ) {
        Column(Modifier.padding(Dimens.l), verticalArrangement = Arrangement.spacedBy(Dimens.m)) {
            if (title != null) {
                Text(title, style = MaterialTheme.typography.titleMedium)
            }
            content()
        }
    }
}

/**
 * M17a B2: every screen gets a top app bar and a scroll host from day one (the M5a/M16 lesson).
 * Pass scrollable = false only for a screen that manages its own scrolling internally.
 *
 * [actions] is the app bar's trailing slot (Logs uses it for Clear).
 * [navigationIcon] is its leading slot (Logs uses it for Back).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LincScreen(
    title: String,
    modifier: Modifier = Modifier,
    scrollable: Boolean = true,
    navigationIcon: @Composable () -> Unit = {},
    actions: @Composable androidx.compose.foundation.layout.RowScope.() -> Unit = {},
    content: @Composable ColumnScope.() -> Unit,
) {
    // M18 A4: observed on the Pixel 7 — with a flat transparent bar, scrolled content ran under the
    // title and was sliced mid-sentence with nothing separating the two. A pinned scroll behaviour
    // gives the bar `scrolledContainerColor` the moment content moves beneath it, so the title
    // always sits on its own surface. The bar stays pinned (it never scrolls away), so no screen
    // loses its title.
    val scrollBehavior = TopAppBarDefaults.pinnedScrollBehavior()
    Scaffold(
        modifier = modifier.nestedScroll(scrollBehavior.nestedScrollConnection),
        containerColor = MaterialTheme.colorScheme.surface,
        topBar = {
            TopAppBar(
                title = { Text(title, style = MaterialTheme.typography.headlineSmall) },
                navigationIcon = navigationIcon,
                actions = actions,
                scrollBehavior = scrollBehavior,
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = MaterialTheme.colorScheme.surface,
                    scrolledContainerColor = MaterialTheme.colorScheme.surfaceContainer,
                ),
            )
        },
    ) { inner ->
        val base = Modifier
            .fillMaxSize()
            .padding(inner)
            .padding(horizontal = Dimens.l)
        Column(
            modifier = if (scrollable) base.verticalScroll(rememberScrollState()) else base,
            verticalArrangement = Arrangement.spacedBy(Dimens.m),
        ) {
            Spacer(Modifier.height(Dimens.s))
            content()
            Spacer(Modifier.height(Dimens.xl))
        }
    }
}

/** M17a B2: a real empty state — icon + one sentence — never a bare "nothing here". */
@Composable
fun LincEmpty(icon: @Composable () -> Unit, text: String) {
    Column(
        Modifier.fillMaxWidth().padding(vertical = Dimens.xxl),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(Dimens.m),
    ) {
        icon()
        Text(
            text,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            // M18 A4: observed on the Pixel 7 — a two-line empty state left-aligned itself under a
            // centred icon, which read as a layout bug. The sentence is centred under its icon.
            textAlign = TextAlign.Center,
        )
    }
}

/**
 * M17a B1: a section label above a group of cards. `labelSmall`, uppercase-free, muted —
 * the one caption style in the app.
 */
@Composable
fun LincSectionLabel(text: String, modifier: Modifier = Modifier) {
    Text(
        text,
        modifier = modifier.padding(start = Dimens.xs, top = Dimens.s),
        style = MaterialTheme.typography.labelSmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
}
