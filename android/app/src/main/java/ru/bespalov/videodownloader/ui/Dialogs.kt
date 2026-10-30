package ru.bespalov.videodownloader.ui

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import coil.compose.AsyncImage
import kotlinx.coroutines.launch
import ru.bespalov.video.core.DownloadStatus
import ru.bespalov.video.core.ErrorText
import java.io.File
import java.text.DateFormat
import java.util.Date

/** Paging reads only downloaded local files. Changing a page never requests metadata. */
@Composable
internal fun PreviewDialog(preview: UiPreview, close: () -> Unit) {
    val images = preview.imageFiles.take(15)
    val pager = rememberPagerState(pageCount = { images.size })
    val scope = rememberCoroutineScope()
    LaunchedEffect(preview.url) { if (images.isNotEmpty()) pager.scrollToPage(0) }
    AlertDialog(
        onDismissRequest = close,
        title = { Text(preview.title.ifBlank { "Превью" }) },
        text = {
            Column(
                Modifier.fillMaxWidth().heightIn(max = 520.dp).verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                SelectionContainerCompat(preview.url)
                if (preview.uploader.isNotBlank()) Text(preview.uploader, style = MaterialTheme.typography.bodyMedium)
                if (preview.durationSeconds > 0) Text("Длительность: ${duration(preview.durationSeconds)}", style = MaterialTheme.typography.bodySmall)
                if (images.isNotEmpty()) {
                    HorizontalPager(
                        state = pager,
                        key = { images[it] },
                        modifier = Modifier.fillMaxWidth().aspectRatio(16f / 9f).testTag("previewGallery"),
                    ) { index ->
                        AsyncImage(
                            model = File(images[index]),
                            contentDescription = "Превью ${index + 1} из ${images.size}",
                            modifier = Modifier.fillMaxSize(),
                            contentScale = ContentScale.Fit,
                        )
                    }
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween) {
                        TextButton(onClick = { scope.launch { pager.animateScrollToPage((pager.currentPage - 1 + images.size) % images.size) } }, enabled = images.size > 1) { Text("‹ Назад") }
                        Text("${pager.currentPage.coerceAtMost(images.lastIndex) + 1} / ${images.size}", style = MaterialTheme.typography.labelMedium)
                        TextButton(onClick = { scope.launch { pager.animateScrollToPage((pager.currentPage + 1) % images.size) } }, enabled = images.size > 1) { Text("Далее ›") }
                    }
                    if (images.size > 1) Text("Листай изображения свайпом или кнопками.", style = MaterialTheme.typography.bodySmall)
                } else if (!preview.loading && preview.error.isBlank()) {
                    Text("Сайт не предоставил подходящих изображений.", style = MaterialTheme.typography.bodySmall)
                }
                if (preview.loading) Row(verticalAlignment = Alignment.CenterVertically) {
                    CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                    Text("Загрузка метаданных и превью…", Modifier.padding(start = 8.dp), style = MaterialTheme.typography.bodySmall)
                }
                if (preview.error.isNotBlank()) {
                    Text(ErrorText.display(preview.error, LocalConfiguration.current.locales[0].language), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
                    var details by remember(preview.url) { mutableStateOf(false) }
                    TextButton(onClick = { details = !details }) { Text("Исходная диагностика") }
                    if (details) SelectionContainerCompat(preview.error)
                }
            }
        },
        confirmButton = { TextButton(onClick = close) { Text("Закрыть") } },
    )
}

@Composable
internal fun DuplicateDialog(prompt: DuplicatePrompt, decide: (DuplicateDecision) -> Unit) {
    AlertDialog(
        onDismissRequest = { decide(DuplicateDecision.CANCEL) },
        title = { Text("Скачать повторно?") },
        text = {
            Column(Modifier.heightIn(max = 300.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(prompt.title.ifBlank { prompt.url })
                if (prompt.title.isNotBlank()) SelectionContainerCompat(prompt.url)
                Text("Эта ссылка уже есть в истории успешных загрузок.")
                prompt.completedAtMillis?.let { Text("Скачано: ${DateFormat.getDateTimeInstance().format(Date(it))}", style = MaterialTheme.typography.bodySmall) }
                if (prompt.total > 1) Text("Повтор ${prompt.position} из ${prompt.total}", style = MaterialTheme.typography.labelMedium)
                Text("При повторном скачивании существующие файлы сохранятся; новый результат получит свободное имя.", style = MaterialTheme.typography.bodySmall)
            }
        },
        confirmButton = {
            Column(horizontalAlignment = Alignment.End) {
                Row {
                    TextButton(onClick = { decide(DuplicateDecision.NO) }) { Text("Нет") }
                    TextButton(onClick = { decide(DuplicateDecision.YES) }) { Text("Да") }
                }
                if (prompt.total > 1) Row {
                    TextButton(onClick = { decide(DuplicateDecision.NO_ALL) }) { Text("Нет для всех") }
                    TextButton(onClick = { decide(DuplicateDecision.YES_ALL) }) { Text("Да для всех") }
                }
                TextButton(onClick = { decide(DuplicateDecision.CANCEL) }) { Text("Отменить запуск") }
            }
        },
    )
}

internal fun StatusLabel(status: DownloadStatus): String = when (status) {
    DownloadStatus.QUEUED -> "В очереди"
    DownloadStatus.RUNNING -> "Скачивание"
    DownloadStatus.PAUSED -> "Пауза"
    DownloadStatus.COMPLETED -> "Готово"
    DownloadStatus.FAILED -> "Ошибка"
    DownloadStatus.INTERRUPTED -> "Прервано — можно продолжить"
    DownloadStatus.CANCELLING -> "Отмена и очистка"
    DownloadStatus.CLEANUP_REQUIRED -> "Требуется очистка"
}

@Composable
internal fun StatusColor(status: DownloadStatus): Color = when (status) {
    DownloadStatus.COMPLETED -> MaterialTheme.colorScheme.primary
    DownloadStatus.FAILED, DownloadStatus.CLEANUP_REQUIRED -> MaterialTheme.colorScheme.error
    DownloadStatus.RUNNING -> MaterialTheme.colorScheme.tertiary
    else -> MaterialTheme.colorScheme.onSurfaceVariant
}

private fun duration(seconds: Double): String {
    val total = seconds.toLong().coerceAtLeast(0)
    val hours = total / 3600
    val minutes = total / 60 % 60
    val remainder = total % 60
    return if (hours > 0) "%d:%02d:%02d".format(hours, minutes, remainder) else "%d:%02d".format(minutes, remainder)
}
