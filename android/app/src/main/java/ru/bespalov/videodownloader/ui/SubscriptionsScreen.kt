package ru.bespalov.videodownloader.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import ru.bespalov.video.core.EntryState
import ru.bespalov.video.core.ErrorText
import ru.bespalov.video.core.Subscription
import java.text.DateFormat
import java.util.Date

@Composable
internal fun SubscriptionsScreen(ui: MobileUiState, actions: MobileActions) {
    var add by rememberSaveable { mutableStateOf(false) }
    var selectedSource by rememberSaveable { mutableStateOf("") }
    var filter by rememberSaveable { mutableIntStateOf(0) }
    var selected by remember(selectedSource, filter) { mutableStateOf(setOf<EntryKey>()) }
    val source = ui.snapshot.subscriptions.firstOrNull { it.id == selectedSource }
    val sources = ui.snapshot.subscriptions
    Column(Modifier.fillMaxSize().padding(horizontal = 12.dp)) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Button(onClick = { add = true }, enabled = !ui.locked, modifier = Modifier.testTag("addSubscription")) { Text("Добавить") }
            TextButton(onClick = { actions.checkSubscriptions(sources.filterNot { it.paused }.map { it.id }) },
                enabled = !ui.locked && ui.runtimeReady && ui.operation == null && sources.any { !it.paused }) { Text("Проверить все") }
        }
        if (source != null) Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = { selectedSource = "" }) { Text("‹ Источники") }
            Text(source.title.ifBlank { source.url }, Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.titleSmall)
        }
        if (source == null) {
            LazyColumn(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(8.dp), contentPadding = PaddingValues(vertical = 8.dp)) {
                if (sources.isEmpty()) item { EmptyState("Добавь канал или плейлист. Проверки запускаются вручную; скачивание — кнопкой Start.") }
                items(sources, key = { it.id }) { item ->
                    SourceCard(item, ui, actions, onClick = { selectedSource = item.id })
                }
            }
        } else {
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                listOf("Новые", "Пропущенные", "Все").forEachIndexed { index, label ->
                    FilterChip(selected = filter == index, onClick = { filter = index }, label = { Text(label) })
                }
            }
            val entries = ui.snapshot.items.filter { it.subscriptionId == source.id && when (filter) {
                0 -> it.state == EntryState.NEW
                1 -> it.state == EntryState.SKIPPED
                else -> true
            } }
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                TextButton(onClick = { actions.queueEntries(selected.toList()); selected = emptySet() }, enabled = !ui.locked && selected.isNotEmpty()) { Text("В очередь (${selected.size})") }
                TextButton(onClick = { actions.skipEntries(selected.toList()); selected = emptySet() }, enabled = !ui.locked && selected.isNotEmpty()) { Text("Пропустить") }
            }
            Text("Скачивание начнётся только после Start.", style = MaterialTheme.typography.bodySmall)
            LazyColumn(Modifier.weight(1f).testTag("subscriptionInbox"), verticalArrangement = Arrangement.spacedBy(8.dp), contentPadding = PaddingValues(vertical = 8.dp)) {
                if (entries.isEmpty()) item { EmptyState("В этом списке пока нет материалов. После проверки новые URL появятся здесь.") }
                items(entries, key = { it.url }) { entry ->
                    val key = EntryKey(entry.subscriptionId, entry.url)
                    val eligible = entry.state in setOf(EntryState.NEW, EntryState.QUEUED, EntryState.SKIPPED)
                    Card(Modifier.fillMaxWidth().clickable { actions.showPreview(entry.url, entry.title) }) {
                        Row(Modifier.padding(8.dp), verticalAlignment = Alignment.CenterVertically) {
                            Checkbox(checked = key in selected, onCheckedChange = { checked -> selected = if (checked) selected + key else selected - key }, enabled = eligible && !ui.locked)
                            Column(Modifier.weight(1f)) {
                                Text(entry.title.ifBlank { entry.url }, maxLines = 2, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.titleSmall)
                                Text(EntryLabel(entry.state), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                            TextButton(onClick = { actions.showPreview(entry.url, entry.title) }) { Text("Превью") }
                        }
                    }
                }
            }
        }
    }
    if (add) AddSubscriptionDialog(onDismiss = { add = false }) { url, feed, name, existing ->
        add = false
        actions.addSubscription(url, feed, name, existing)
    }
}

@Composable
private fun SourceCard(source: Subscription, ui: MobileUiState, actions: MobileActions, onClick: () -> Unit) {
    var menu by remember { mutableStateOf(false) }
    var rename by remember { mutableStateOf(false) }
    var remove by remember { mutableStateOf(false) }
    var diagnostic by remember { mutableStateOf(false) }
    Card(Modifier.fillMaxWidth().clickable(onClick = onClick)) {
        Row(Modifier.padding(12.dp), verticalAlignment = Alignment.Top) {
            Column(Modifier.weight(1f)) {
                Text(source.title.ifBlank { source.url }, style = MaterialTheme.typography.titleMedium, maxLines = 2, overflow = TextOverflow.Ellipsis)
                Text("Папка: ${source.shortName.ifBlank { "после проверки" }}", style = MaterialTheme.typography.bodySmall)
                Text(when {
                    source.paused -> "Приостановлена"
                    !source.initialized -> "Нужна первая проверка"
                    source.lastError.isNotBlank() -> "Ошибка проверки"
                    else -> "Активна"
                }, style = MaterialTheme.typography.labelMedium)
                if (source.lastWarning.isNotBlank()) Text("Есть предупреждение проверки", style = MaterialTheme.typography.bodySmall)
                if (source.lastError.isNotBlank()) Text(ErrorText.display(source.lastError, androidx.compose.ui.platform.LocalConfiguration.current.locales[0].language), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
                source.lastSuccessUtc?.let { Text("Успешная проверка: ${DateFormat.getDateTimeInstance().format(Date(it))}", style = MaterialTheme.typography.bodySmall) }
                val count = ui.snapshot.items.count { it.subscriptionId == source.id && it.state == EntryState.NEW }
                Text("Новых: $count", style = MaterialTheme.typography.bodySmall)
                TextButton(onClick = { actions.checkSubscriptions(listOf(source.id)) }, enabled = !ui.locked && !source.paused && ui.operation == null && ui.runtimeReady) { Text("Проверить") }
            }
            Box {
                TextButton(onClick = { menu = true }) { Text("⋮") }
                DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                    DropdownMenuItem(text = { Text("Материалы") }, onClick = { menu = false; onClick() })
                    DropdownMenuItem(text = { Text("Краткое имя папки") }, onClick = { menu = false; rename = true }, enabled = !ui.locked)
                    DropdownMenuItem(text = { Text(if (source.paused) "Возобновить проверки" else "Приостановить проверки") }, onClick = { menu = false; actions.toggleSource(source.id) }, enabled = !ui.locked)
                    if (source.lastError.isNotBlank() || source.lastWarning.isNotBlank()) DropdownMenuItem(text = { Text("Диагностика") }, onClick = { menu = false; diagnostic = true })
                    DropdownMenuItem(text = { Text("Удалить подписку") }, onClick = { menu = false; remove = true }, enabled = !ui.locked)
                }
            }
        }
    }
    if (rename) {
        var name by remember(source.id) { mutableStateOf(source.shortName) }
        AlertDialog(onDismissRequest = { rename = false }, title = { Text("Краткое имя папки") },
            text = { OutlinedTextField(value = name, onValueChange = { name = it }, singleLine = true, label = { Text("Имя") }) },
            confirmButton = { TextButton(onClick = { rename = false; actions.renameSource(source.id, name) }, enabled = name.isNotBlank()) { Text("Сохранить") } },
            dismissButton = { TextButton(onClick = { rename = false }) { Text("Отмена") } })
    }
    if (remove) AlertDialog(onDismissRequest = { remove = false }, title = { Text("Удалить подписку?") },
        text = { Text("Её список материалов будет удалён. Обычная очередь, история и скачанные файлы сохранятся.") },
        confirmButton = { TextButton(onClick = { remove = false; actions.removeSource(source.id) }) { Text("Удалить") } },
        dismissButton = { TextButton(onClick = { remove = false }) { Text("Отмена") } })
    if (diagnostic) AlertDialog(onDismissRequest = { diagnostic = false }, title = { Text("Диагностика проверки") },
        text = { SelectionContainerCompat(listOf(source.lastError, source.lastWarning).filter(String::isNotBlank).joinToString("\n\n"), Modifier.heightIn(max = 420.dp).verticalScroll(rememberScrollState())) },
        confirmButton = { TextButton(onClick = { diagnostic = false }) { Text("Закрыть") } })
}

@Composable
private fun AddSubscriptionDialog(onDismiss: () -> Unit, add: (String, String, String, Boolean) -> Unit) {
    var url by rememberSaveable { mutableStateOf("") }
    var feed by rememberSaveable { mutableStateOf("") }
    var name by rememberSaveable { mutableStateOf("") }
    var existing by rememberSaveable { mutableStateOf(false) }
    AlertDialog(onDismissRequest = onDismiss, title = { Text("Добавить подписку") },
        text = { Column(Modifier.heightIn(max = 420.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedTextField(value = url, onValueChange = { url = it }, label = { Text("URL канала / плейлиста") }, modifier = Modifier.testTag("subscriptionUrl"), maxLines = 3)
            OutlinedTextField(value = feed, onValueChange = { feed = it }, label = { Text("RSS / Atom — необязательно") }, maxLines = 2)
            OutlinedTextField(value = name, onValueChange = { name = it }, label = { Text("Краткое имя папки — необязательно") }, singleLine = true)
            Row(verticalAlignment = Alignment.CenterVertically) {
                Checkbox(checked = existing, onCheckedChange = { existing = it })
                Text("Все имеющиеся материалы добавить в очередь", style = MaterialTheme.typography.bodySmall)
            }
            Text(if (existing) "После полного обхода материалы попадут в очередь. Скачивание — кнопкой Start." else "Текущий набор станет точкой отсчёта. При следующих ручных проверках появятся новые материалы.", style = MaterialTheme.typography.bodySmall)
        } },
        confirmButton = { TextButton(onClick = { add(url, feed, name, existing) }, enabled = url.isNotBlank()) { Text("Добавить и проверить") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Отмена") } })
}

internal fun EntryLabel(state: EntryState): String = when (state) {
    EntryState.BASELINE -> "Исходный набор"
    EntryState.NEW -> "Новое"
    EntryState.QUEUED -> "В очереди"
    EntryState.PROCESSED -> "Обработано"
    EntryState.ALREADY_PROCESSED -> "Обработано в другой подписке"
    EntryState.SKIPPED -> "Пропущено"
}
