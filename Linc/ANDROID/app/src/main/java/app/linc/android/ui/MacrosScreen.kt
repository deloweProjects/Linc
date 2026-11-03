package app.linc.android.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.FilledIconButton
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.linc.android.service.AppInventory
import app.linc.android.service.Macro
import app.linc.android.service.MacroBook
import app.linc.android.service.MacroKind
import app.linc.android.service.MacroKinds
import app.linc.android.service.MacroRunner
import app.linc.android.service.MacroStep
import app.linc.android.service.MacroStore
import app.linc.android.ui.kit.EmptyState
import app.linc.android.ui.kit.HelperText
import app.linc.android.ui.kit.Kit
import app.linc.android.ui.kit.LincAlertDialog
import app.linc.android.ui.kit.LincScaffold
import app.linc.android.ui.kit.SectionHeader
import app.linc.android.ui.kit.pagePadding
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/*
 * Control → Macros (the owner, 2026-11-03: "add a macros section in Tools on PC and phone; on the
 * phone add it under Control as one of the options under pages"): a few steps run with one tap.
 * The list, each with ▶; tap one to change its steps. Kept on this phone only.
 */

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun MacrosScreen(onBack: (() -> Unit)? = null, onSettings: (() -> Unit)? = null, onMirror: () -> Unit) {
    val context = LocalContext.current
    LaunchedEffect(Unit) { MacroStore.init(context) }
    val macros by MacroStore.macros.collectAsState()
    val run by MacroRunner.run.collectAsState()
    var editingId by remember { mutableStateOf<String?>(null) }

    val editing = macros.firstOrNull { it.id == editingId }
    if (editing != null) {
        MacroEditor(
            macro = editing,
            onChange = { MacroStore.put(context, it) },
            onDelete = { MacroStore.remove(context, editing.id); editingId = null },
            onRun = { MacroRunner.start(context, editing, onMirror) },
            onClose = { editingId = null },
            run = run?.takeIf { it.macroId == editing.id },
            onSettings = onSettings,
        )
        return
    }

    LincScaffold(
        title = "Macros",
        onBack = onBack,
        onSettings = onSettings,
        floatingActionButton = {
            ExtendedFloatingActionButton(
                onClick = {
                    val macro = Macro(UUID.randomUUID().toString().take(8), "New macro")
                    MacroStore.put(context, macro)
                    editingId = macro.id
                },
                icon = { Icon(LincSymbols.Add, contentDescription = null) },
                text = { Text("New macro") },
            )
        },
    ) { inner ->
        LazyColumn(contentPadding = pagePadding(inner), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            run?.let { r -> item { RunCard(r, Modifier.padding(horizontal = Kit.gutter)) } }
            item {
                HelperText("Tap ▶ to run a macro, or tap it to change its steps. PC steps need the PC connected to Linc.",
                    modifier = Modifier.padding(horizontal = Kit.gutter))
            }
            if (macros.isEmpty()) {
                item { EmptyState("No macros yet", "Make one with New macro: Do Not Disturb on, PC volume 50, mirror the PC.", LincSymbols.Play) }
            }
            items(macros, key = { it.id }) { macro ->
                MacroCard(
                    macro = macro,
                    running = run?.running == true && run?.macroId == macro.id,
                    onOpen = { editingId = macro.id },
                    onRun = { MacroRunner.start(context, macro, onMirror) },
                    modifier = Modifier.padding(horizontal = Kit.gutter),
                )
            }
        }
    }
}

@Composable
private fun MacroCard(macro: Macro, running: Boolean, onOpen: () -> Unit, onRun: () -> Unit, modifier: Modifier = Modifier) {
    Surface(shape = RoundedCornerShape(24.dp), color = MaterialTheme.colorScheme.surfaceContainer, modifier = modifier.fillMaxWidth()) {
        Row(Modifier.clickable(onClick = onOpen).padding(start = 18.dp, end = 10.dp, top = 12.dp, bottom = 12.dp),
            verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(macro.name, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold,
                    maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(summary(macro), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            FilledIconButton(onClick = onRun, enabled = !running, modifier = Modifier.semantics { contentDescription = "Run ${macro.name}" }) {
                Icon(LincSymbols.Play, contentDescription = null)
            }
        }
    }
}

private fun summary(macro: Macro): String = when (macro.steps.size) {
    0 -> "No steps yet"
    1 -> MacroBook.describe(macro.steps[0])
    else -> "${macro.steps.size} steps · " + macro.steps.joinToString(", ") { MacroBook.describe(it) }
}

/** What is running now, or how the last run went (a failure names its step). */
@Composable
private fun RunCard(run: MacroRunner.Run, modifier: Modifier = Modifier) {
    Surface(
        shape = RoundedCornerShape(20.dp),
        color = if (run.failed) MaterialTheme.colorScheme.errorContainer else MaterialTheme.colorScheme.secondaryContainer,
        contentColor = if (run.failed) MaterialTheme.colorScheme.onErrorContainer else MaterialTheme.colorScheme.onSecondaryContainer,
        modifier = modifier.fillMaxWidth(),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (run.failed) Icon(LincSymbols.Error, contentDescription = null, modifier = Modifier.padding(end = 10.dp))
                Text(run.text, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f))
                if (run.running) {
                    TextButton(onClick = { MacroRunner.stop() }) { Text("Stop") }
                } else {
                    IconButton(onClick = { MacroRunner.clear() }) { Icon(LincSymbols.Close, contentDescription = "Dismiss") }
                }
            }
            if (run.running) LinearProgressIndicator(progress = { run.progress }, modifier = Modifier.fillMaxWidth())
        }
    }
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun MacroEditor(
    macro: Macro,
    onChange: (Macro) -> Unit,
    onDelete: () -> Unit,
    onRun: () -> Unit,
    onClose: () -> Unit,
    run: MacroRunner.Run?,
    onSettings: (() -> Unit)?,
) {
    BackHandler(onBack = onClose)
    var name by remember(macro.id) { mutableStateOf(macro.name) }
    var adding by remember { mutableStateOf(false) }
    var confirmDelete by remember { mutableStateOf(false) }

    LincScaffold(
        title = macro.name,
        onBack = onClose,
        onSettings = onSettings,
        actions = {
            IconButton(onClick = { confirmDelete = true }) { Icon(LincSymbols.Delete, contentDescription = "Delete macro") }
        },
        floatingActionButton = {
            ExtendedFloatingActionButton(
                onClick = onRun,
                icon = { Icon(LincSymbols.Play, contentDescription = null) },
                text = { Text("Run") },
                expanded = run?.running != true,
            )
        },
    ) { inner ->
        LazyColumn(contentPadding = pagePadding(inner), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            run?.let { r -> item { RunCard(r, Modifier.padding(horizontal = Kit.gutter)) } }
            item {
                OutlinedTextField(
                    value = name,
                    onValueChange = {
                        name = it
                        onChange(macro.copy(name = it.trim().ifEmpty { "Macro" }))
                    },
                    label = { Text("Name") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = Kit.gutter),
                )
            }
            item {
                SectionHeader("Steps") {
                    TextButton(onClick = { adding = true }) {
                        Icon(LincSymbols.Add, contentDescription = null, modifier = Modifier.size(18.dp))
                        Text("Add step", modifier = Modifier.padding(start = 6.dp))
                    }
                }
            }
            if (macro.steps.isEmpty()) {
                item { HelperText("No steps yet. Add step picks what happens, in order.", modifier = Modifier.padding(horizontal = Kit.gutter)) }
            }
            itemsIndexed(macro.steps) { i, step ->
                StepRow(
                    index = i,
                    step = step,
                    last = i == macro.steps.lastIndex,
                    onMove = { to -> onChange(macro.copy(steps = MacroBook.moved(macro.steps, i, to))) },
                    onRemove = { onChange(macro.copy(steps = macro.steps.filterIndexed { j, _ -> j != i })) },
                    modifier = Modifier.padding(horizontal = Kit.gutter),
                )
            }
        }
    }

    if (adding) {
        AddStepDialog(
            onDismiss = { adding = false },
            onAdd = { onChange(macro.copy(steps = macro.steps + it)); adding = false },
        )
    }
    if (confirmDelete) {
        LincAlertDialog(
            onDismissRequest = { confirmDelete = false },
            title = { Text("Delete \"${macro.name}\"?") },
            text = { Text("Its steps go too.") },
            confirmButton = { TextButton(onClick = { confirmDelete = false; onDelete() }) { Text("Delete") } },
            dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text("Cancel") } },
        )
    }
}

@Composable
private fun StepRow(index: Int, step: MacroStep, last: Boolean, onMove: (Int) -> Unit, onRemove: () -> Unit, modifier: Modifier = Modifier) {
    Surface(shape = RoundedCornerShape(18.dp), color = MaterialTheme.colorScheme.surfaceContainer, modifier = modifier.fillMaxWidth()) {
        Row(Modifier.padding(start = 16.dp, end = 4.dp, top = 4.dp, bottom = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Text("${index + 1}", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(end = 14.dp))
            Column(Modifier.weight(1f)) {
                Text(MacroBook.describe(step), style = MaterialTheme.typography.bodyLarge, maxLines = 2, overflow = TextOverflow.Ellipsis)
                val needs = MacroKinds.find(step.kind)?.pcVersion ?: 0
                Text(if (needs > 0) "On the PC" else "On this phone", style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            IconButton(onClick = { onMove(index - 1) }, enabled = index > 0) { Icon(LincSymbols.ArrowUp, contentDescription = "Move up") }
            IconButton(onClick = { onMove(index + 1) }, enabled = !last) { Icon(LincSymbols.ArrowDown, contentDescription = "Move down") }
            IconButton(onClick = onRemove) { Icon(LincSymbols.Close, contentDescription = "Remove step") }
        }
    }
}

/** Add step: pick what it does, then fill in what it acts on (an app from a list for "Open an app"). */
@Composable
private fun AddStepDialog(onDismiss: () -> Unit, onAdd: (MacroStep) -> Unit) {
    val context = LocalContext.current
    var kind by remember { mutableStateOf<MacroKind?>(null) }
    var value by remember { mutableStateOf("") }
    var problem by remember { mutableStateOf<String?>(null) }
    var apps by remember { mutableStateOf<List<AppInventory.Entry>>(emptyList()) }
    LaunchedEffect(kind) {
        if (kind?.kind == MacroKinds.PHONE_APP && apps.isEmpty()) {
            apps = withContext(Dispatchers.IO) { AppInventory.launchable(context).sortedBy { it.label.lowercase() } }
        }
    }
    fun add(k: MacroKind, text: String) {
        val step = MacroBook.fromInput(k.kind, text)
        problem = MacroBook.problem(step)
        if (problem == null) onAdd(step)
    }

    LincAlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(kind?.title ?: "Add step") },
        text = {
            val k = kind
            if (k == null) {
                LazyColumn(Modifier.heightIn(max = 420.dp)) {
                    items(MacroKinds.all, key = { it.kind }) { option ->
                        Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp))
                            .clickable { if (option.hasValue) { kind = option; value = "" } else add(option, "") }
                            .padding(horizontal = 12.dp, vertical = 14.dp)) {
                            Column {
                                Text(option.title, style = MaterialTheme.typography.bodyLarge)
                                Text(if (option.pcVersion > 0) "On the PC" else "On this phone", style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                        }
                    }
                }
            } else {
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    OutlinedTextField(
                        value = value,
                        onValueChange = { value = it; problem = null },
                        label = { Text(k.valueLabel) },
                        placeholder = { Text(k.example) },
                        singleLine = true,
                        isError = problem != null,
                        supportingText = problem?.let { { Text(it) } },
                        modifier = Modifier.fillMaxWidth(),
                    )
                    if (k.valueLabel == "on or off") {
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            FilledTonalButton(onClick = { add(k, "on") }) { Text("On") }
                            FilledTonalButton(onClick = { add(k, "off") }) { Text("Off") }
                        }
                    }
                    if (k.kind == MacroKinds.PC_MEDIA) {
                        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                            for (a in MacroKinds.mediaActions) FilledTonalButton(onClick = { add(k, a) }) { Text(a) }
                        }
                    }
                    if (k.kind == MacroKinds.PHONE_APP) {
                        HorizontalDivider()
                        val shown = apps.filter { value.isBlank() || it.label.contains(value, true) || it.packageName.contains(value, true) }
                        LazyColumn(Modifier.heightIn(max = 280.dp)) {
                            items(shown, key = { it.packageName }) { app ->
                                Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).clickable { add(k, app.packageName) }
                                    .padding(horizontal = 12.dp, vertical = 10.dp)) {
                                    Text(app.label, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                    Text(app.packageName, style = MaterialTheme.typography.bodySmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                }
                            }
                        }
                    }
                }
            }
        },
        confirmButton = { kind?.let { k -> TextButton(onClick = { add(k, value) }) { Text("Add") } } },
        dismissButton = { TextButton(onClick = { if (kind != null) { kind = null; problem = null } else onDismiss() }) {
            Text(if (kind != null) "Back" else "Cancel") } },
    )
}
