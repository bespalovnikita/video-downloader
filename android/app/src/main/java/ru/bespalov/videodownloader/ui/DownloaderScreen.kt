package ru.bespalov.videodownloader.ui

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
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
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import ru.bespalov.video.core.DownloadStatus
import ru.bespalov.video.core.DownloadTask
import java.text.DateFormat
import java.util.Date
import kotlinx.coroutines.delay
import ru.bespalov.video.core.ErrorText

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DownloaderScreen(ui: MobileUiState, actions: MobileActions) {
    var tab by rememberSaveable { mutableIntStateOf(0) }
    var showLogs by rememberSaveable { mutableStateOf(false) }
    var cookiesHelp by rememberSaveable { mutableStateOf(false) }
    var pendingStart by remember { mutableStateOf<List<String>?>(null) }
    val context = LocalContext.current
    val notifications = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
        actions.start(pendingStart)
        pendingStart = null
    }
    val listPicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { it?.let(actions::importList) }
    val cookiesPicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { it?.let(actions::importCookies) }
    val outputPicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { it?.let(actions::selectOutput) }
    val exportPicker = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/json")) { it?.let(actions::exportBackup) }
    val backupPicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { it?.let(actions::importBackup) }
    val start: (List<String>?) -> Unit = { ids ->
        if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            pendingStart = ids
            notifications.launch(Manifest.permission.POST_NOTIFICATIONS)
        } else actions.start(ids)
    }
    val titles = listOf("Очередь", "Загрузки", "Подписки", "Настройки")
    val tags = listOf("queueTab", "downloadsTab", "subscriptionsTab", "settingsTab")
    MaterialTheme(colorScheme = if (androidx.compose.foundation.isSystemInDarkTheme()) darkColorScheme() else lightColorScheme()) {
        Scaffold(
            topBar = {
                TopAppBar(title = { Text("Video Downloader") }, actions = {
                    TextButton(onClick = { showLogs = true }) { Text("Журнал") }
                })
            },
            bottomBar = {
                NavigationBar {
                    titles.forEachIndexed { index, name ->
                        NavigationBarItem(
                            selected = tab == index,
                            onClick = { tab = index },
                            icon = { Text(listOf("≡", "↓", "★", "⚙")[index]) },
                            label = { Text(name, maxLines = 1) },
                            modifier = Modifier.testTag(tags[index]),
                        )
                    }
                }
            },
        ) { padding ->
            Column(Modifier.fillMaxSize().padding(padding)) {
                if (ui.readOnly) {
                    Surface(color = MaterialTheme.colorScheme.errorContainer) {
                        Text("Сохранённые данные не удалось прочитать. Изменения заблокированы. Можно восстановить резервную копию в настройках.", Modifier.padding(12.dp))
                    }
                }
                if (!ui.runtimeReady) {
                    Row(Modifier.fillMaxWidth().padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
                        CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                        Text("Подготовка загрузчика…", Modifier.padding(start = 10.dp))
                    }
                }
                ui.operation?.let { OperationCard(it, actions::cancelCheck) }
                when (tab) {
                    0 -> QueueScreen(ui, actions, start, { listPicker.launch(arrayOf("text/*", "application/octet-stream")) })
                    1 -> DownloadsScreen(ui, actions, start)
                    2 -> SubscriptionsScreen(ui, actions)
                    else -> SettingsScreen(
                        ui, actions,
                        pickOutput = { outputPicker.launch(null) },
                        pickCookies = { cookiesPicker.launch(arrayOf("text/*", "application/octet-stream")) },
                        exportBackup = { exportPicker.launch("video-downloader-backup.json") },
                        importBackup = { backupPicker.launch(arrayOf("application/json", "text/*")) },
                        cookiesHelp = { cookiesHelp = true },
                    )
                }
            }
        }
        ui.preview?.let { PreviewDialog(it, actions::closePreview) }
        ui.duplicatePrompt?.let { DuplicateDialog(it, actions::resolveDuplicate) }
        if (ui.duplicateNotices.isNotEmpty()) {
            AlertDialog(onDismissRequest = actions::acknowledgeDuplicates,
                title = { Text("Уже обработано в другой подписке") },
                text = { LazyColumn { items(ui.duplicateNotices) { item ->
                    Text((item.title.ifBlank { item.url }) + item.sourceTitle.takeIf(String::isNotBlank)?.let { "\n$it" }.orEmpty(), Modifier.padding(bottom = 12.dp))
                } } },
                confirmButton = { TextButton(onClick = actions::acknowledgeDuplicates) { Text("Понятно") } })
        }
        ui.message?.let { message ->
            var details by remember(message) { mutableStateOf(false) }
            AlertDialog(onDismissRequest = actions::dismissMessage, title = { Text("Video Downloader") },
                text = { Column(Modifier.heightIn(max = 420.dp).verticalScroll(rememberScrollState())) {
                    Text(message.text)
                    if (details && message.details.isNotBlank()) {
                        SelectionContainerCompat(message.details, Modifier.padding(top = 12.dp))
                    }
                } },
                confirmButton = { TextButton(onClick = actions::dismissMessage) { Text("Закрыть") } },
                dismissButton = if (message.details.isNotBlank()) ({ TextButton(onClick = { details = !details }) { Text("Диагностика") } }) else null)
        }
        if (ui.recoveryRequired) {
            AlertDialog(onDismissRequest = { actions.recoverQueue(false) },
                title = { Text("Восстановить очередь?") },
                text = { Text("Найдены незавершённые задания. После восстановления они будут ждать кнопки Start; готовые загрузки повторно не запускаются.") },
                confirmButton = { TextButton(onClick = { actions.recoverQueue(true) }) { Text("Восстановить") } },
                dismissButton = { TextButton(onClick = { actions.recoverQueue(false) }) { Text("Не восстанавливать") } })
        }
        if (showLogs) {
            AlertDialog(onDismissRequest = { showLogs = false }, title = { Text("Журнал") },
                text = { LazyColumn(Modifier.heightIn(max = 450.dp)) {
                    if (ui.logs.isEmpty()) item { Text("Сообщений пока нет") }
                    items(ui.logs.takeLast(300)) { Text(it, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(bottom = 8.dp)) }
                } }, confirmButton = { TextButton(onClick = { showLogs = false }) { Text("Закрыть") } })
        }
        if (cookiesHelp) CookiesHelpDialog { cookiesHelp = false }
    }
}

@Composable
private fun OperationCard(operation: UiOperation, cancel: () -> Unit) {
    var elapsed by remember(operation.startedAtMillis) { mutableLongStateOf(0L) }
    LaunchedEffect(operation.startedAtMillis) {
        while (true) {
            elapsed = ((System.currentTimeMillis() - operation.startedAtMillis) / 1000).coerceAtLeast(0)
            delay(1_000)
        }
    }
    Card(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp)) {
        Column(Modifier.padding(12.dp)) {
            Text(operation.title, style = MaterialTheme.typography.titleSmall)
            if (operation.stage.isNotBlank()) Text(operation.stage, style = MaterialTheme.typography.bodySmall)
            if (operation.total > 0) Text("Завершено источников: ${operation.completed} / ${operation.total}")
            Text("Прошло: ${elapsed / 60} мин ${elapsed % 60} с", style = MaterialTheme.typography.bodySmall)
            LinearProgressIndicator(Modifier.fillMaxWidth().padding(top = 8.dp))
            if (operation.cancellable) TextButton(onClick = cancel) { Text("Отменить проверку") }
        }
    }
}

@Composable
private fun QueueScreen(ui: MobileUiState, actions: MobileActions, start: (List<String>?) -> Unit, importList: () -> Unit) {
    var input by rememberSaveable { mutableStateOf("") }
    val clipboard = LocalClipboardManager.current
    var clear by remember { mutableStateOf(false) }
    Column(Modifier.fillMaxSize().padding(horizontal = 12.dp)) {
        OutlinedTextField(value = input, onValueChange = { input = it }, label = { Text("URL видео или плейлиста") },
            modifier = Modifier.fillMaxWidth().testTag("urlInput"), minLines = 1, maxLines = 3, enabled = !ui.locked)
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Button(onClick = { actions.addUrls(input); input = "" }, enabled = input.isNotBlank() && !ui.locked,
                modifier = Modifier.testTag("addUrls")) { Text("Добавить") }
            TextButton(onClick = { clipboard.getText()?.text?.let(actions::addUrls) }, enabled = !ui.locked) { Text("Вставить") }
            TextButton(onClick = importList, enabled = !ui.locked) { Text("Из файла") }
        }
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Checkbox(checked = ui.snapshot.settings.showTitles, onCheckedChange = { actions.saveSettings(ui.snapshot.settings.copy(showTitles = it)) }, enabled = !ui.locked)
            Text("Названия вместо URL", Modifier.weight(1f), style = MaterialTheme.typography.bodySmall)
            TextButton(onClick = { clear = true }, enabled = !ui.locked && !ui.running && ui.snapshot.queue.isNotEmpty()) { Text("Очистить") }
        }
        DownloadControls(ui, start, actions)
        Text("В очереди: ${ui.snapshot.queue.size}", style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(vertical = 8.dp))
        LazyColumn(Modifier.weight(1f).testTag("queueList"), verticalArrangement = Arrangement.spacedBy(8.dp), contentPadding = PaddingValues(bottom = 12.dp)) {
            if (ui.snapshot.queue.isEmpty()) item { EmptyState("Добавь ссылку или поделись ею из браузера в Video Downloader.") }
            items(ui.snapshot.queue, key = { it.id }) { task -> TaskCard(task, ui, actions, start, reorder = true) }
        }
    }
    if (clear) AlertDialog(onDismissRequest = { clear = false }, title = { Text("Очистить очередь?") },
        text = { Text("Скачанные файлы и история останутся на месте. Задания с незавершённой очисткой будут сохранены.") },
        confirmButton = { TextButton(onClick = { clear = false; actions.clearQueue() }) { Text("Очистить") } },
        dismissButton = { TextButton(onClick = { clear = false }) { Text("Отмена") } })
}

@Composable
private fun DownloadControls(ui: MobileUiState, start: (List<String>?) -> Unit, actions: MobileActions) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) {
        Button(onClick = { start(null) }, enabled = !ui.locked && ui.runtimeReady && !ui.running && ui.snapshot.queue.any { it.status !in setOf(DownloadStatus.COMPLETED, DownloadStatus.CLEANUP_REQUIRED, DownloadStatus.CANCELLING) },
            modifier = Modifier.testTag("startDownloads")) { Text("Start") }
        if (ui.paused) OutlinedButton(onClick = actions::resume, enabled = !ui.locked) { Text("Продолжить") }
        else OutlinedButton(onClick = actions::pause, enabled = ui.running && !ui.locked) { Text("Пауза") }
        TextButton(onClick = actions::stop, enabled = ui.running && !ui.locked) { Text("Стоп") }
    }
}

@Composable
private fun DownloadsScreen(ui: MobileUiState, actions: MobileActions, start: (List<String>?) -> Unit) {
    var history by rememberSaveable { mutableStateOf(false) }
    Column(Modifier.fillMaxSize().padding(horizontal = 12.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            FilterChip(selected = !history, onClick = { history = false }, label = { Text("Задания") })
            Spacer(Modifier.width(8.dp))
            FilterChip(selected = history, onClick = { history = true }, label = { Text("История (${ui.snapshot.history.size})") })
        }
        DownloadControls(ui, start, actions)
        if (history) LazyColumn(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(8.dp), contentPadding = PaddingValues(vertical = 12.dp)) {
            if (ui.snapshot.history.isEmpty()) item { EmptyState("История появится после успешного сохранения файлов.") }
            items(ui.snapshot.history.asReversed()) { record ->
                Card(Modifier.fillMaxWidth().clickable { actions.showPreview(record.url) }) {
                    Column(Modifier.padding(12.dp)) {
                        Text(record.url, maxLines = 2, overflow = TextOverflow.Ellipsis)
                        Text(DateFormat.getDateTimeInstance().format(Date(record.completedAtUtc)), style = MaterialTheme.typography.bodySmall)
                        if (record.outputUris.isNotEmpty()) {
                            val context = LocalContext.current
                            TextButton(onClick = { OpenUri(context, record.outputUris.first(), true) }) { Text("Открыть файл") }
                        }
                    }
                }
            }
        } else LazyColumn(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(8.dp), contentPadding = PaddingValues(vertical = 12.dp)) {
            val rows = ui.snapshot.queue.filter { it.status != DownloadStatus.QUEUED }
            if (rows.isEmpty()) item { EmptyState("Здесь появятся активные и завершённые загрузки.") }
            items(rows, key = { it.id }) { TaskCard(it, ui, actions, start, reorder = false) }
        }
    }
}

@Composable
private fun TaskCard(task: DownloadTask, ui: MobileUiState, actions: MobileActions, start: (List<String>?) -> Unit, reorder: Boolean) {
    var menu by remember { mutableStateOf(false) }
    var remove by remember { mutableStateOf(false) }
    var diagnostic by remember { mutableStateOf(false) }
    var deleteFiles by remember(task.id, task.status) { mutableStateOf(task.status == DownloadStatus.CLEANUP_REQUIRED) }
    val context = LocalContext.current
    val clipboard = LocalClipboardManager.current
    Card(Modifier.fillMaxWidth().testTag("queueRow:${task.id}").clickable { actions.showPreview(task.url, task.title) }) {
        Column(Modifier.padding(12.dp)) {
            Row(verticalAlignment = Alignment.Top) {
                Column(Modifier.weight(1f)) {
                    Text(if (ui.snapshot.settings.showTitles) task.title.ifBlank { task.url } else task.url, maxLines = 2, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.titleSmall)
                    Text(StatusLabel(task.status), color = StatusColor(task.status), style = MaterialTheme.typography.labelMedium)
                    if (task.subscriptionName.isNotBlank()) Text("Подписка / папка: ${task.subscriptionName}", style = MaterialTheme.typography.bodySmall)
                }
                Box {
                    TextButton(onClick = { menu = true }) { Text("⋮") }
                    DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                        DropdownMenuItem(text = { Text("Превью") }, onClick = { menu = false; actions.showPreview(task.url, task.title) })
                        DropdownMenuItem(text = { Text("Копировать URL") }, onClick = { menu = false; clipboard.setText(AnnotatedString(task.url)) })
                        DropdownMenuItem(text = { Text("Открыть URL") }, onClick = { menu = false; OpenUri(context, task.url) })
                        if (!ui.running && !ui.locked && task.status != DownloadStatus.CLEANUP_REQUIRED) DropdownMenuItem(text = { Text("Скачать / повторить") }, onClick = { menu = false; start(listOf(task.id)) })
                        if (reorder && !ui.running && !ui.locked) {
                            DropdownMenuItem(text = { Text("Выше") }, onClick = { menu = false; actions.move(task.id, -1) })
                            DropdownMenuItem(text = { Text("Ниже") }, onClick = { menu = false; actions.move(task.id, 1) })
                        }
                        if (task.outputUris.isNotEmpty()) DropdownMenuItem(text = { Text("Открыть файл") }, onClick = { menu = false; OpenUri(context, task.outputUris.first(), true) })
                        if (task.rawError.isNotBlank()) DropdownMenuItem(text = { Text("Исходная диагностика") }, onClick = { menu = false; diagnostic = true })
                        DropdownMenuItem(text = { Text(if (task.status == DownloadStatus.CLEANUP_REQUIRED) "Повторить удаление" else "Удалить") }, onClick = { menu = false; remove = true }, enabled = !ui.locked)
                    }
                }
            }
            if (task.status == DownloadStatus.RUNNING) {
                LinearProgressIndicator(progress = { (task.progress / 100f).coerceIn(0f, 1f) }, modifier = Modifier.fillMaxWidth().padding(vertical = 6.dp))
                Text("${task.progress.toInt()}% · ${task.speed} · ${task.downloaded} · ${task.eta}", style = MaterialTheme.typography.bodySmall)
            }
            if (task.rawError.isNotBlank()) Text(ErrorText.display(task.rawError, androidx.compose.ui.platform.LocalConfiguration.current.locales[0].language), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
        }
    }
    if (remove) AlertDialog(onDismissRequest = { remove = false }, title = { Text("Удалить задание?") },
        text = { Column {
            Text(if (task.outputUris.isNotEmpty()) "Задание исчезнет из очереди. История успешного скачивания сохранится." else "Активная загрузка будет отменена, её временные файлы удалены. Другие задания продолжат работу.")
            if (task.outputUris.isNotEmpty()) Row(verticalAlignment = Alignment.CenterVertically) {
                Checkbox(checked = deleteFiles, onCheckedChange = { deleteFiles = it }, enabled = task.status != DownloadStatus.CLEANUP_REQUIRED)
                Text("Удалить файлы этого задания", style = MaterialTheme.typography.bodySmall)
            }
        } },
        confirmButton = { TextButton(onClick = { remove = false; actions.remove(task.id, deleteFiles) }) { Text("Удалить") } },
        dismissButton = { TextButton(onClick = { remove = false }) { Text("Отмена") } })
    if (diagnostic) AlertDialog(onDismissRequest = { diagnostic = false }, title = { Text("Исходная диагностика") },
        text = { SelectionContainerCompat(task.rawError, Modifier.heightIn(max = 420.dp).verticalScroll(rememberScrollState())) }, confirmButton = { TextButton(onClick = { diagnostic = false }) { Text("Закрыть") } })
}

@Composable
internal fun EmptyState(text: String) { Text(text, Modifier.padding(vertical = 20.dp), color = MaterialTheme.colorScheme.onSurfaceVariant) }

@Composable
internal fun SelectionContainerCompat(text: String, modifier: Modifier = Modifier) {
    androidx.compose.foundation.text.selection.SelectionContainer(modifier) { Text(text, style = MaterialTheme.typography.bodySmall) }
}

internal fun OpenUri(context: android.content.Context, value: String, file: Boolean = false) {
    runCatching {
        val uri = Uri.parse(value)
        val intent = Intent(Intent.ACTION_VIEW).apply {
            if (file) setDataAndType(uri, context.contentResolver.getType(uri) ?: "video/*") else data = uri
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        context.startActivity(intent)
    }.onFailure { Toast.makeText(context, "Нет приложения для открытия ссылки или файла", Toast.LENGTH_LONG).show() }
}
