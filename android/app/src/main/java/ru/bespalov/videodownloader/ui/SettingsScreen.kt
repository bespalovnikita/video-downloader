package ru.bespalov.videodownloader.ui

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import ru.bespalov.video.core.Profiles
import java.io.File

@Composable
internal fun SettingsScreen(
    ui: MobileUiState,
    actions: MobileActions,
    pickOutput: () -> Unit,
    pickCookies: () -> Unit,
    exportBackup: () -> Unit,
    importBackup: () -> Unit,
    cookiesHelp: () -> Unit,
) {
    var draft by remember(ui.snapshot.settings) { mutableStateOf(ui.snapshot.settings) }
    var restore by remember { mutableStateOf(false) }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text("Настройки скачивания", style = MaterialTheme.typography.titleLarge)
        Text("Изменения применятся при следующем запуске задания. Параметры текущей загрузки сохраняются до её завершения.", style = MaterialTheme.typography.bodySmall)
        ChoiceField("Профиль", draft.profile, Profiles.names, !ui.locked) { draft = Profiles.apply(draft, it) }
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            ChoiceField("Качество", draft.quality, linkedMapOf("Best" to "Лучшее", "2160" to "2160p", "1440" to "1440p", "1080" to "1080p", "720" to "720p"), !ui.locked, Modifier.weight(1f)) { draft = draft.copy(quality = it) }
            ChoiceField("Кодек", draft.codec, listOf("Auto", "H264", "VP9", "AV1").associateWith { it }, !ui.locked, Modifier.weight(1f)) { draft = draft.copy(codec = it) }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            ChoiceField("Контейнер", draft.container, listOf("Auto", "MP4", "MKV", "WebM").associateWith { it }, !ui.locked, Modifier.weight(1f)) { draft = draft.copy(container = it) }
            ChoiceField("Параллельно", draft.threads.toString(), (1..4).associate { it.toString() to it.toString() }, !ui.locked, Modifier.weight(1f)) { draft = draft.copy(threads = it.toInt()) }
        }
        ChoiceField("Фрагментов на видео", draft.fragments.toString(), (1..8).associate { it.toString() to it.toString() }, !ui.locked) { draft = draft.copy(fragments = it.toInt()) }
        ChoiceField("Ограничение скорости", draft.rateLimit, linkedMapOf("Unlimited" to "Без ограничения", "1M" to "1 МБ/с", "5M" to "5 МБ/с", "10M" to "10 МБ/с", "25M" to "25 МБ/с"), !ui.locked) { draft = draft.copy(rateLimit = it) }
        SettingToggle("Только аудио MP3", draft.audioOnly, !ui.locked) { draft = draft.copy(audioOnly = it) }
        SettingToggle("Подпапка по дате", draft.archive, !ui.locked) { draft = draft.copy(archive = it) }
        OutlinedTextField(value = draft.filenameTemplate, onValueChange = { draft = draft.copy(filenameTemplate = it) }, label = { Text("Шаблон имени файла") },
            supportingText = { Text("Относительный путь без «..». По умолчанию: название [id].расширение") }, modifier = Modifier.fillMaxWidth(), enabled = !ui.locked)

        HorizontalDivider(Modifier.padding(vertical = 8.dp))
        Text("Папка загрузок", style = MaterialTheme.typography.titleMedium)
        Text(if (draft.outputTreeUri.isBlank()) "По умолчанию: системные Загрузки на Android 10+; каталог приложения на Android 8–9." else "Выбранная папка: ${draft.outputTreeUri}", style = MaterialTheme.typography.bodySmall)
        Row {
            OutlinedButton(onClick = { actions.saveSettings(draft); pickOutput() }, enabled = !ui.locked) { Text("Выбрать папку") }
            TextButton(onClick = { draft = draft.copy(outputTreeUri = "") }, enabled = !ui.locked && draft.outputTreeUri.isNotBlank()) { Text("По умолчанию") }
        }
        Text("Имя подписки создаёт отдельную подпапку. Изменение настроек не переносит уже скачанные файлы.", style = MaterialTheme.typography.bodySmall)

        HorizontalDivider(Modifier.padding(vertical = 8.dp))
        Text("Субтитры и метаданные", style = MaterialTheme.typography.titleMedium)
        OutlinedTextField(value = draft.subtitleLangs, onValueChange = { draft = draft.copy(subtitleLangs = it) }, label = { Text("Языки субтитров") }, enabled = !ui.locked, modifier = Modifier.fillMaxWidth(), singleLine = true)
        SettingToggle("Скачать субтитры", draft.writeSubs, !ui.locked) { draft = draft.copy(writeSubs = it) }
        SettingToggle("Автоматические субтитры", draft.autoSubs, !ui.locked) { draft = draft.copy(autoSubs = it) }
        SettingToggle("Встроить субтитры", draft.embedSubs, !ui.locked) { draft = draft.copy(embedSubs = it) }
        SettingToggle("Встроить обложку", draft.embedThumbnail, !ui.locked) { draft = draft.copy(embedThumbnail = it) }
        SettingToggle("Встроить метаданные", draft.embedMetadata, !ui.locked) { draft = draft.copy(embedMetadata = it) }
        SettingToggle("Встроить главы", draft.embedChapters, !ui.locked) { draft = draft.copy(embedChapters = it) }
        SettingToggle("Удалять спонсорские вставки", draft.sponsor, !ui.locked) { draft = draft.copy(sponsor = it) }

        HorizontalDivider(Modifier.padding(vertical = 8.dp))
        Text("Cookies", style = MaterialTheme.typography.titleMedium)
        Text(if (draft.cookiesFile.isBlank()) "Файл не выбран" else "Подключён: ${File(draft.cookiesFile).name}", style = MaterialTheme.typography.bodySmall)
        Row {
            OutlinedButton(onClick = { actions.saveSettings(draft); pickCookies() }, enabled = !ui.locked) { Text("Импорт .txt") }
            TextButton(onClick = { draft = draft.copy(cookiesFile = "") }, enabled = !ui.locked && draft.cookiesFile.isNotBlank()) { Text("Отключить") }
        }
        TextButton(onClick = cookiesHelp) { Text("Как получить cookies из браузера") }

        Button(onClick = { actions.saveSettings(draft) }, enabled = !ui.locked, modifier = Modifier.fillMaxWidth().testTag("saveSettings")) { Text("Сохранить настройки") }
        HorizontalDivider(Modifier.padding(vertical = 8.dp))
        Text("Загрузчик и данные", style = MaterialTheme.typography.titleMedium)
        Text("yt-dlp: ${ui.runtimeVersion.ifBlank { if (ui.runtimeReady) "версия неизвестна" else "получение версии…" }}",
            style = MaterialTheme.typography.bodySmall, modifier = Modifier.testTag("runtimeVersion"))
        OutlinedButton(onClick = actions::updateRuntime, enabled = ui.runtimeReady && ui.operation == null && !ui.running && !ui.locked) { Text("Обновить yt-dlp") }
        OutlinedButton(onClick = exportBackup) { Text("Экспорт резервной копии") }
        OutlinedButton(onClick = { restore = true }, enabled = !ui.running && ui.operation == null) { Text("Восстановить из резервной копии") }
        Text("Резервная копия содержит настройки, очередь, подписки и историю. Скачанные видео и содержимое файла cookies в неё не входят.", style = MaterialTheme.typography.bodySmall)
        Spacer(Modifier.height(12.dp))
    }
    if (restore) AlertDialog(onDismissRequest = { restore = false }, title = { Text("Восстановить данные?") },
        text = { Text("Текущие настройки, очередь, подписки и история будут заменены выбранной резервной копией. Скачанные файлы сохранятся. Сначала можно экспортировать текущие данные.") },
        confirmButton = { TextButton(onClick = { restore = false; importBackup() }) { Text("Выбрать копию") } },
        dismissButton = { TextButton(onClick = { restore = false }) { Text("Отмена") } })
}

@Composable
internal fun ChoiceField(label: String, selected: String, choices: Map<String, String>, enabled: Boolean = true, modifier: Modifier = Modifier, onSelect: (String) -> Unit) {
    var expanded by remember { mutableStateOf(false) }
    Column(modifier) {
        Text(label, style = MaterialTheme.typography.labelMedium)
        Box {
            OutlinedButton(onClick = { expanded = true }, enabled = enabled, modifier = Modifier.fillMaxWidth()) { Text(choices[selected] ?: selected) }
            DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
                choices.forEach { (key, title) ->
                    DropdownMenuItem(text = { Text(title) }, onClick = { expanded = false; onSelect(key) })
                }
            }
        }
    }
}

@Composable
internal fun SettingToggle(label: String, checked: Boolean, enabled: Boolean = true, onChange: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Text(label, Modifier.weight(1f))
        Switch(checked = checked, onCheckedChange = onChange, enabled = enabled)
    }
}

@Composable
internal fun CookiesHelpDialog(close: () -> Unit) {
    val context = LocalContext.current
    AlertDialog(onDismissRequest = close, title = { Text("Cookies из браузера") },
        text = { Column(Modifier.heightIn(max = 420.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text("1. Открой нужное видео в своём браузерном профиле и войди в аккаунт. Проверь, что видео воспроизводится.")
            Text("2. Экспортируй cookies этого сайта в Netscape cookies.txt. На компьютере подходят Get cookies.txt LOCALLY для Chrome/Edge и cookies.txt для Firefox. Chrome на Android не поддерживает эти расширения — можно перенести готовый файл с компьютера.")
            Text("3. В настройках Video Downloader нажми «Импорт .txt» и выбери файл. Приложение сохранит его приватную копию; затем повтори проверку подписки или Start.")
            Text("JSON из DevTools и строка Cookie: не подходят. Не отправляй файл cookies в чат или вместе с логами: он может давать доступ к аккаунту.", style = MaterialTheme.typography.bodySmall)
            TextButton(onClick = { OpenUri(context, "https://github.com/yt-dlp/yt-dlp/wiki/FAQ#how-do-i-pass-cookies-to-yt-dlp") }) { Text("Официальная инструкция yt-dlp") }
        } }, confirmButton = { TextButton(onClick = close) { Text("Понятно") } })
}
