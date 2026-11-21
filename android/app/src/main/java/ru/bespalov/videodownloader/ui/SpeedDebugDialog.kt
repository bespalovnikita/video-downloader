package ru.bespalov.videodownloader.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import ru.bespalov.videodownloader.engine.SpeedDebug

@Composable
internal fun SpeedDebugDialog(
    state: SpeedDebugUiState,
    ready: Boolean,
    start: (String) -> Unit,
    cancel: () -> Unit,
    close: () -> Unit,
) {
    var urls by rememberSaveable { mutableStateOf(SpeedDebug.defaultUrls) }
    val focus = LocalFocusManager.current
    val best = state.results.filter { it.measured == SpeedDebug.SOURCE_COUNT && it.failures == 0 }
        .maxByOrNull { it.mebibytesPerSecond }
    AlertDialog(
        onDismissRequest = { if (!state.running) close() },
        modifier = Modifier.testTag("speedDebugDialog"),
        title = { Text("Speed debug mode") },
        text = {
            Column(Modifier.heightIn(max = 460.dp).verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("Тест выбирает четыре ссылки и сравнивает параметры скачивания за минуту. " +
                    "Используются сохранённые настройки формата и cookies. Ограничение скорости выключено.")
                OutlinedTextField(
                    value = urls,
                    onValueChange = { if (it.length <= 65_536) urls = it },
                    label = { Text("Ссылки, по одной на строку") },
                    modifier = Modifier.fillMaxWidth().testTag("speedDebugUrls"),
                    minLines = 4,
                    maxLines = 6,
                    enabled = !state.running,
                )
                if (state.running) {
                    LinearProgressIndicator(Modifier.fillMaxWidth())
                    Text(if (state.cancelling) "Остановка и очистка…" else state.profile?.label ?: "Подготовка…")
                } else if (!ready) {
                    Text("Дождитесь окончания других операций перед запуском.")
                }
                if (state.cancelled) Text("Тест отменён.")
                if (state.error.isNotBlank()) Text(state.error, color = MaterialTheme.colorScheme.error)
                if (state.results.isNotEmpty() && !state.running && !state.cancelled && state.error.isBlank()) {
                    if (best == null) Text("Для выбора лучшего профиля нужны данные от всех четырёх видео.")
                    else Text("Лучший результат: ${speedText(best.mebibytesPerSecond)} · ${best.profile.label}",
                        style = MaterialTheme.typography.titleSmall, modifier = Modifier.testTag("speedDebugBest"))
                }
                state.results.forEach { result ->
                    Text(result.profile.label, style = MaterialTheme.typography.labelLarge)
                    val size = result.bytes / (1024.0 * 1024)
                    Text("${speedText(result.mebibytesPerSecond)} · ${"%.2f".format(size)} МиБ · " +
                        "${"%.1f".format(result.millis / 1000.0)} с\nПолучены данные: ${result.measured} из ${result.started} начатых, ошибок: ${result.failures}")
                    if (result.error.isNotBlank()) Text(result.error, color = MaterialTheme.colorScheme.error)
                }
            }
        },
        confirmButton = {
            if (state.running) {
                TextButton(onClick = cancel, enabled = !state.cancelling) { Text("Отменить") }
            } else {
                TextButton(onClick = { focus.clearFocus(); start(urls) }, enabled = ready && urls.isNotBlank(),
                    modifier = Modifier.testTag("speedDebugStart")) { Text("Запустить") }
            }
        },
        dismissButton = {
            if (!state.running) TextButton(onClick = close) { Text("Закрыть") }
        },
    )
}

private fun speedText(value: Double) =
    "${"%.2f".format(value)} МиБ/с (${"%.1f".format(value * 8_388_608 / 1_000_000)} Мбит/с)"
