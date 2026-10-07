package com.bespalovnikita.videodownloader.ui

import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowDownward
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import coil3.compose.AsyncImage
import com.bespalovnikita.videodownloader.model.AppState
import com.bespalovnikita.videodownloader.model.DownloadSettings
import com.bespalovnikita.videodownloader.model.HistoryItem
import com.bespalovnikita.videodownloader.model.QueueItem
import com.bespalovnikita.videodownloader.model.QueueStatus
import java.io.File
import java.text.DateFormat
import java.util.Date

private enum class Tab { QUEUE, HISTORY, SETTINGS }

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun VideoDownloaderAppScreen(viewModel: MainViewModel) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    var selectedTab by rememberSaveable { mutableStateOf(Tab.QUEUE) }

    MaterialTheme(
        colorScheme = darkColorScheme()
    ) {
        Scaffold(
            topBar = {
                TopAppBar(
                    title = {
                        Column {
                            Text("Video Downloader")
                            if (viewModel.engineInitError.isNotBlank()) {
                                Text(
                                    "yt-dlp init error",
                                    style = MaterialTheme.typography.labelSmall,
                                    color = MaterialTheme.colorScheme.error
                                )
                            }
                        }
                    }
                )
            },
            bottomBar = {
                NavigationBar {
                    NavigationBarItem(
                        selected = selectedTab == Tab.QUEUE,
                        onClick = { selectedTab = Tab.QUEUE },
                        icon = { Icon(Icons.Default.Download, null) },
                        label = { Text("Queue") }
                    )
                    NavigationBarItem(
                        selected = selectedTab == Tab.HISTORY,
                        onClick = { selectedTab = Tab.HISTORY },
                        icon = { Icon(Icons.Default.History, null) },
                        label = { Text("History") }
                    )
                    NavigationBarItem(
                        selected = selectedTab == Tab.SETTINGS,
                        onClick = { selectedTab = Tab.SETTINGS },
                        icon = { Icon(Icons.Default.Settings, null) },
                        label = { Text("Settings") }
                    )
                }
            }
        ) { padding ->
            Box(
                modifier = Modifier
                    .padding(padding)
                    .fillMaxSize()
            ) {
                when (selectedTab) {
                    Tab.QUEUE -> QueueScreen(state, viewModel)
                    Tab.HISTORY -> HistoryScreen(state.history)
                    Tab.SETTINGS -> SettingsScreen(state.settings, viewModel)
                }
            }
        }
    }
}

@Composable
private fun QueueScreen(state: AppState, viewModel: MainViewModel) {
    var input by remember { mutableStateOf("") }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(12.dp)
    ) {
        if (viewModel.engineInitError.isNotBlank()) {
            Card(modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.padding(12.dp)) {
                    Text(
                        "Downloader engine failed to initialize",
                        color = MaterialTheme.colorScheme.error
                    )
                    Text(
                        viewModel.engineInitError,
                        style = MaterialTheme.typography.bodySmall
                    )
                }
            }
            Spacer(Modifier.height(8.dp))
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically
        ) {
            OutlinedTextField(
                value = input,
                onValueChange = { input = it },
                modifier = Modifier.weight(1f),
                label = { Text("URL") },
                singleLine = true
            )
            Spacer(Modifier.width(8.dp))
            Button(
                onClick = {
                    viewModel.addText(input)
                    input = ""
                },
                enabled = input.isNotBlank()
            ) {
                Text("Add")
            }
        }

        Spacer(Modifier.height(8.dp))

        Row(
            modifier = Modifier
                .fillMaxWidth()
                .horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Button(
                onClick = viewModel::startDownloads,
                enabled = state.queue.any {
                    it.status == QueueStatus.QUEUED || it.status == QueueStatus.ERROR
                } && viewModel.engineInitError.isBlank()
            ) {
                Text("Start")
            }
            OutlinedButton(onClick = viewModel::pauseDownloads) { Text("Pause") }
            OutlinedButton(onClick = viewModel::resumeDownloads) { Text("Resume") }
            OutlinedButton(onClick = viewModel::stopDownloads) { Text("Stop") }
            OutlinedButton(
                onClick = viewModel::clearDone,
                enabled = state.queue.any { it.status == QueueStatus.DONE }
            ) {
                Text("Clear done")
            }
        }

        Spacer(Modifier.height(8.dp))

        if (state.queue.isEmpty()) {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text("Add a URL or use Share → Video Downloader")
            }
        } else {
            LazyColumn(
                verticalArrangement = Arrangement.spacedBy(8.dp),
                modifier = Modifier.fillMaxSize()
            ) {
                items(state.queue, key = { it.id }) { item ->
                    QueueCard(
                        item = item,
                        onClick = { viewModel.prioritizePreview(item.url) },
                        onRemove = { viewModel.remove(item.id) },
                        onRetry = { viewModel.retry(item.id) },
                        onMoveUp = { viewModel.move(item.id, -1) },
                        onMoveDown = { viewModel.move(item.id, 1) }
                    )
                }
            }
        }
    }
}

@Composable
private fun QueueCard(
    item: QueueItem,
    onClick: () -> Unit,
    onRemove: () -> Unit,
    onRetry: () -> Unit,
    onMoveUp: () -> Unit,
    onMoveDown: () -> Unit
) {
    val context = LocalContext.current
    val active = item.status == QueueStatus.STARTING ||
        item.status == QueueStatus.DOWNLOADING ||
        item.status == QueueStatus.RETRYING ||
        item.status == QueueStatus.FINALIZING

    Card(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
    ) {
        Row(Modifier.padding(10.dp)) {
            val model: Any? = when {
                item.localThumbnailPath.isNotBlank() -> File(item.localThumbnailPath)
                item.thumbnailUrl.isNotBlank() -> item.thumbnailUrl
                else -> null
            }

            AsyncImage(
                model = model,
                contentDescription = null,
                modifier = Modifier
                    .size(width = 120.dp, height = 72.dp)
                    .clip(RoundedCornerShape(8.dp)),
                contentScale = ContentScale.Crop
            )

            Spacer(Modifier.width(10.dp))

            Column(Modifier.weight(1f)) {
                Text(
                    item.title.ifBlank { item.url },
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                    style = MaterialTheme.typography.titleSmall
                )
                if (item.uploader.isNotBlank()) {
                    Text(
                        item.uploader,
                        style = MaterialTheme.typography.bodySmall,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }

                val previewBits = buildList {
                    if (item.maxHeight > 0) add(item.maxHeight.toString() + "p")
                    if (item.maxFps > 0) add(item.maxFps.toInt().toString() + "fps")
                    if (item.codecs.isNotBlank()) add(item.codecs)
                    if (item.hdr.isNotBlank()) add(item.hdr)
                }
                if (previewBits.isNotEmpty()) {
                    Text(
                        previewBits.joinToString(" · "),
                        style = MaterialTheme.typography.labelSmall
                    )
                }

                Spacer(Modifier.height(4.dp))
                Text(
                    item.status.name.lowercase().replaceFirstChar { it.uppercase() } +
                        if (item.etaSeconds >= 0 && active) " · ETA " + item.etaSeconds + "s" else "",
                    style = MaterialTheme.typography.bodySmall
                )

                if (active || item.status == QueueStatus.DONE) {
                    LinearProgressIndicator(
                        progress = { (item.progress / 100f).coerceIn(0f, 1f) },
                        modifier = Modifier.fillMaxWidth()
                    )
                }

                if (item.statusLine.isNotBlank() && active) {
                    Text(
                        item.statusLine,
                        style = MaterialTheme.typography.labelSmall,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }

                if (item.previewError.isNotBlank()) {
                    Text(
                        "Preview: " + item.previewError,
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.error,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis
                    )
                }

                if (item.error.isNotBlank()) {
                    Text(
                        item.error,
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.error,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis
                    )
                }

                Row(
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    IconButton(onClick = onMoveUp, enabled = !active) {
                        Icon(Icons.Default.ArrowUpward, "Move up")
                    }
                    IconButton(onClick = onMoveDown, enabled = !active) {
                        Icon(Icons.Default.ArrowDownward, "Move down")
                    }
                    if (item.status == QueueStatus.ERROR) {
                        IconButton(onClick = onRetry) {
                            Icon(Icons.Default.Refresh, "Retry")
                        }
                    }
                    if (item.outputUri.isNotBlank()) {
                        OutlinedButton(
                            onClick = {
                                runCatching {
                                    context.startActivity(
                                        Intent(Intent.ACTION_VIEW).apply {
                                            data = Uri.parse(item.outputUri)
                                            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                                        }
                                    )
                                }
                            }
                        ) {
                            Text("Open")
                        }
                    }
                    Spacer(Modifier.weight(1f))
                    IconButton(onClick = onRemove, enabled = !active) {
                        Icon(Icons.Default.Delete, "Remove")
                    }
                }
            }
        }
    }
}

@Composable
private fun HistoryScreen(history: List<HistoryItem>) {
    val context = LocalContext.current

    if (history.isEmpty()) {
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text("No completed downloads yet")
        }
        return
    }

    LazyColumn(
        modifier = Modifier
            .fillMaxSize()
            .padding(12.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        items(history, key = { it.id }) { item ->
            Card(
                modifier = Modifier
                    .fillMaxWidth()
                    .clickable {
                        runCatching {
                            context.startActivity(
                                Intent(Intent.ACTION_VIEW).apply {
                                    setDataAndType(Uri.parse(item.outputUri), item.mimeType)
                                    addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                                }
                            )
                        }
                    }
            ) {
                Column(Modifier.padding(12.dp)) {
                    Text(item.title, style = MaterialTheme.typography.titleSmall)
                    Text(item.fileName, style = MaterialTheme.typography.bodySmall)
                    Text(
                        DateFormat.getDateTimeInstance().format(Date(item.completedAt)),
                        style = MaterialTheme.typography.labelSmall
                    )
                }
            }
        }
    }
}

@Composable
private fun SettingsScreen(settings: DownloadSettings, viewModel: MainViewModel) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp)
    ) {
        Text("Quality", style = MaterialTheme.typography.titleMedium)
        ChoiceRow(
            values = listOf("Best", "2160", "1440", "1080", "720"),
            selected = settings.quality
        ) { value -> viewModel.updateSettings { it.copy(quality = value) } }

        Spacer(Modifier.height(12.dp))
        Text("Codec preference", style = MaterialTheme.typography.titleMedium)
        ChoiceRow(
            values = listOf("Auto", "H264", "VP9", "AV1"),
            selected = settings.videoCodec
        ) { value -> viewModel.updateSettings { it.copy(videoCodec = value) } }

        Spacer(Modifier.height(12.dp))
        Text("Container", style = MaterialTheme.typography.titleMedium)
        ChoiceRow(
            values = listOf("Auto", "MP4", "MKV", "WebM"),
            selected = settings.container
        ) { value -> viewModel.updateSettings { it.copy(container = value) } }

        HorizontalDivider(Modifier.padding(vertical = 16.dp))

        Stepper(
            label = "Parallel downloads",
            value = settings.parallelDownloads,
            range = 1..4
        ) { value -> viewModel.updateSettings { it.copy(parallelDownloads = value) } }

        Stepper(
            label = "Fragments per download",
            value = settings.fragments,
            range = 1..8
        ) { value -> viewModel.updateSettings { it.copy(fragments = value) } }

        OutlinedTextField(
            value = settings.rateLimit,
            onValueChange = { value -> viewModel.updateSettings { it.copy(rateLimit = value) } },
            modifier = Modifier.fillMaxWidth(),
            label = { Text("Rate limit, e.g. 5M") },
            singleLine = true
        )

        HorizontalDivider(Modifier.padding(vertical = 16.dp))

        SwitchSetting("MP3 only", settings.audioOnly) {
            viewModel.updateSettings { s -> s.copy(audioOnly = it) }
        }
        SwitchSetting("SponsorBlock", settings.sponsorBlock) {
            viewModel.updateSettings { s -> s.copy(sponsorBlock = it) }
        }
        SwitchSetting("Write subtitles", settings.writeSubtitles) {
            viewModel.updateSettings { s -> s.copy(writeSubtitles = it) }
        }
        SwitchSetting("Auto subtitles", settings.writeAutoSubtitles) {
            viewModel.updateSettings { s -> s.copy(writeAutoSubtitles = it) }
        }
        SwitchSetting("Embed subtitles", settings.embedSubtitles) {
            viewModel.updateSettings { s -> s.copy(embedSubtitles = it) }
        }
        SwitchSetting("Embed thumbnail", settings.embedThumbnail) {
            viewModel.updateSettings { s -> s.copy(embedThumbnail = it) }
        }
        SwitchSetting("Embed metadata", settings.embedMetadata) {
            viewModel.updateSettings { s -> s.copy(embedMetadata = it) }
        }
        SwitchSetting("Embed chapters", settings.embedChapters) {
            viewModel.updateSettings { s -> s.copy(embedChapters = it) }
        }

        OutlinedTextField(
            value = settings.subtitleLangs,
            onValueChange = { value -> viewModel.updateSettings { it.copy(subtitleLangs = value) } },
            modifier = Modifier.fillMaxWidth(),
            label = { Text("Subtitle languages") },
            singleLine = true
        )

        OutlinedTextField(
            value = settings.filenameTemplate,
            onValueChange = { value -> viewModel.updateSettings { it.copy(filenameTemplate = value) } },
            modifier = Modifier.fillMaxWidth(),
            label = { Text("Filename template") }
        )

        Spacer(Modifier.height(16.dp))
        Text(
            "Files are staged privately, then published to Downloads/VideoDownloader through MediaStore.",
            style = MaterialTheme.typography.bodySmall
        )
        Spacer(Modifier.height(4.dp))
        Text(
            "Pause stops active yt-dlp processes; Resume restarts them and yt-dlp resumes partial files where possible.",
            style = MaterialTheme.typography.bodySmall
        )
    }
}

@Composable
private fun ChoiceRow(
    values: List<String>,
    selected: String,
    onSelect: (String) -> Unit
) {
    Row(
        modifier = Modifier.horizontalScroll(rememberScrollState()),
        horizontalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        values.forEach { value ->
            FilterChip(
                selected = value == selected,
                onClick = { onSelect(value) },
                label = { Text(value) }
            )
        }
    }
}

@Composable
private fun Stepper(
    label: String,
    value: Int,
    range: IntRange,
    onChange: (Int) -> Unit
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(label, modifier = Modifier.weight(1f))
        OutlinedButton(
            onClick = { onChange((value - 1).coerceAtLeast(range.first)) },
            enabled = value > range.first
        ) { Text("−") }
        Text(
            value.toString(),
            modifier = Modifier.padding(horizontal = 12.dp)
        )
        OutlinedButton(
            onClick = { onChange((value + 1).coerceAtMost(range.last)) },
            enabled = value < range.last
        ) { Text("+") }
    }
}

@Composable
private fun SwitchSetting(label: String, checked: Boolean, onChecked: (Boolean) -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 5.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(label, modifier = Modifier.weight(1f))
        Switch(checked = checked, onCheckedChange = onChecked)
    }
}
