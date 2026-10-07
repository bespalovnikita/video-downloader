package com.bespalovnikita.videodownloader.data

import android.content.Context
import android.util.AtomicFile
import com.bespalovnikita.videodownloader.model.AppState
import com.bespalovnikita.videodownloader.model.DownloadSettings
import com.bespalovnikita.videodownloader.model.HistoryItem
import com.bespalovnikita.videodownloader.model.PreviewData
import com.bespalovnikita.videodownloader.model.QueueItem
import com.bespalovnikita.videodownloader.model.QueueStatus
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

class AppStore(context: Context) {
    private val file = AtomicFile(File(context.filesDir, "android-state.json"))
    private val mutex = Mutex()
    private val _state = MutableStateFlow(loadState())
    val state: StateFlow<AppState> = _state.asStateFlow()

    suspend fun addUrls(rawUrls: List<String>): List<QueueItem> = mutex.withLock {
        val current = _state.value
        val existing = current.queue.map { it.url }.toMutableSet()
        val added = rawUrls
            .asSequence()
            .map { it.trim() }
            .filter { it.startsWith("http://") || it.startsWith("https://") }
            .filter { it.isNotBlank() && existing.add(it) }
            .map { QueueItem(url = it) }
            .toList()

        if (added.isNotEmpty()) {
            publish(current.copy(queue = current.queue + added))
        }
        added
    }

    suspend fun removeQueueItem(id: String) = mutex.withLock {
        publish(_state.value.copy(queue = _state.value.queue.filterNot { it.id == id }))
    }

    suspend fun moveQueueItem(id: String, delta: Int) = mutex.withLock {
        val list = _state.value.queue.toMutableList()
        val from = list.indexOfFirst { it.id == id }
        if (from < 0) return@withLock
        val to = (from + delta).coerceIn(0, list.lastIndex)
        if (from == to) return@withLock
        val item = list.removeAt(from)
        list.add(to, item)
        publish(_state.value.copy(queue = list))
    }

    suspend fun updateQueueItem(id: String, transform: (QueueItem) -> QueueItem) = mutex.withLock {
        val updated = _state.value.queue.map { if (it.id == id) transform(it) else it }
        publish(_state.value.copy(queue = updated))
    }

    suspend fun updatePreview(url: String, preview: PreviewData) = mutex.withLock {
        val updated = _state.value.queue.map { item ->
            if (item.url != url) item
            else item.copy(
                title = preview.title,
                uploader = preview.uploader,
                thumbnailUrl = preview.thumbnailUrl,
                localThumbnailPath = preview.localThumbnailPath,
                durationSeconds = preview.durationSeconds,
                maxHeight = preview.maxHeight,
                maxFps = preview.maxFps,
                codecs = preview.codecs,
                hdr = preview.hdr,
                previewFetchedAt = preview.savedAtEpochMs,
                previewError = ""
            )
        }
        publish(_state.value.copy(queue = updated))
    }

    suspend fun setPreviewError(url: String, error: String) = mutex.withLock {
        val updated = _state.value.queue.map {
            if (it.url == url) it.copy(previewError = error) else it
        }
        publish(_state.value.copy(queue = updated))
    }

    suspend fun updateSettings(transform: (DownloadSettings) -> DownloadSettings) = mutex.withLock {
        publish(_state.value.copy(settings = transform(_state.value.settings)))
    }

    suspend fun addHistory(item: HistoryItem) = mutex.withLock {
        val next = (listOf(item) + _state.value.history.filterNot { it.outputUri == item.outputUri })
            .take(500)
        publish(_state.value.copy(history = next))
    }

    suspend fun retryItem(id: String) = updateQueueItem(id) {
        it.copy(
            status = QueueStatus.QUEUED,
            progress = 0f,
            etaSeconds = -1,
            statusLine = "",
            error = "",
            outputUri = ""
        )
    }

    suspend fun clearDone() = mutex.withLock {
        publish(_state.value.copy(queue = _state.value.queue.filterNot { it.status == QueueStatus.DONE }))
    }

    suspend fun resumePaused() = mutex.withLock {
        val updated = _state.value.queue.map {
            if (it.status == QueueStatus.PAUSED) it.copy(status = QueueStatus.QUEUED, statusLine = "Queued") else it
        }
        publish(_state.value.copy(queue = updated))
    }

    suspend fun recoverInterrupted() = mutex.withLock {
        val active = setOf(
            QueueStatus.STARTING,
            QueueStatus.DOWNLOADING,
            QueueStatus.RETRYING,
            QueueStatus.FINALIZING
        )
        val updated = _state.value.queue.map {
            if (it.status in active) {
                it.copy(
                    status = QueueStatus.QUEUED,
                    progress = 0f,
                    etaSeconds = -1,
                    statusLine = "Recovered after restart"
                )
            } else it
        }
        publish(_state.value.copy(queue = updated))
    }

    private fun publish(state: AppState) {
        _state.value = state
        persist(state)
    }

    private fun persist(state: AppState) {
        val out = file.startWrite()
        try {
            out.bufferedWriter(Charsets.UTF_8).use { it.write(state.toJson().toString()) }
            file.finishWrite(out)
        } catch (t: Throwable) {
            file.failWrite(out)
            throw t
        }
    }

    private fun loadState(): AppState {
        if (!file.baseFile.exists()) return AppState()
        return runCatching {
            file.openRead().bufferedReader(Charsets.UTF_8).use {
                stateFromJson(JSONObject(it.readText()))
            }
        }.getOrElse { AppState() }
    }
}

private fun AppState.toJson() = JSONObject().apply {
    put("queue", JSONArray().apply { queue.forEach { put(it.toJson()) } })
    put("history", JSONArray().apply { history.forEach { put(it.toJson()) } })
    put("settings", settings.toJson())
}

private fun QueueItem.toJson() = JSONObject().apply {
    put("id", id)
    put("url", url)
    put("title", title)
    put("uploader", uploader)
    put("thumbnailUrl", thumbnailUrl)
    put("localThumbnailPath", localThumbnailPath)
    put("durationSeconds", durationSeconds)
    put("maxHeight", maxHeight)
    put("maxFps", maxFps)
    put("codecs", codecs)
    put("hdr", hdr)
    put("previewError", previewError)
    put("previewFetchedAt", previewFetchedAt)
    put("status", status.name)
    put("progress", progress.toDouble())
    put("etaSeconds", etaSeconds)
    put("statusLine", statusLine)
    put("outputUri", outputUri)
    put("error", error)
    put("addedAt", addedAt)
}

private fun HistoryItem.toJson() = JSONObject().apply {
    put("id", id)
    put("sourceUrl", sourceUrl)
    put("title", title)
    put("outputUri", outputUri)
    put("fileName", fileName)
    put("mimeType", mimeType)
    put("completedAt", completedAt)
}

private fun DownloadSettings.toJson() = JSONObject().apply {
    put("quality", quality)
    put("videoCodec", videoCodec)
    put("container", container)
    put("parallelDownloads", parallelDownloads)
    put("fragments", fragments)
    put("rateLimit", rateLimit)
    put("audioOnly", audioOnly)
    put("sponsorBlock", sponsorBlock)
    put("writeSubtitles", writeSubtitles)
    put("writeAutoSubtitles", writeAutoSubtitles)
    put("embedSubtitles", embedSubtitles)
    put("subtitleLangs", subtitleLangs)
    put("embedThumbnail", embedThumbnail)
    put("embedMetadata", embedMetadata)
    put("embedChapters", embedChapters)
    put("filenameTemplate", filenameTemplate)
    put("retries", retries)
    put("retryDelaySeconds", retryDelaySeconds)
}

private fun stateFromJson(root: JSONObject): AppState {
    val queueArray = root.optJSONArray("queue") ?: JSONArray()
    val historyArray = root.optJSONArray("history") ?: JSONArray()
    return AppState(
        queue = buildList {
            for (i in 0 until queueArray.length()) add(queueFromJson(queueArray.getJSONObject(i)))
        },
        history = buildList {
            for (i in 0 until historyArray.length()) add(historyFromJson(historyArray.getJSONObject(i)))
        },
        settings = settingsFromJson(root.optJSONObject("settings") ?: JSONObject())
    )
}

private fun queueFromJson(o: JSONObject) = QueueItem(
    id = o.optString("id"),
    url = o.optString("url"),
    title = o.optString("title"),
    uploader = o.optString("uploader"),
    thumbnailUrl = o.optString("thumbnailUrl"),
    localThumbnailPath = o.optString("localThumbnailPath"),
    durationSeconds = o.optLong("durationSeconds"),
    maxHeight = o.optInt("maxHeight"),
    maxFps = o.optDouble("maxFps"),
    codecs = o.optString("codecs"),
    hdr = o.optString("hdr"),
    previewError = o.optString("previewError"),
    previewFetchedAt = o.optLong("previewFetchedAt"),
    status = runCatching { QueueStatus.valueOf(o.optString("status")) }.getOrDefault(QueueStatus.QUEUED),
    progress = o.optDouble("progress").toFloat(),
    etaSeconds = o.optLong("etaSeconds", -1),
    statusLine = o.optString("statusLine"),
    outputUri = o.optString("outputUri"),
    error = o.optString("error"),
    addedAt = o.optLong("addedAt", System.currentTimeMillis())
)

private fun historyFromJson(o: JSONObject) = HistoryItem(
    id = o.optString("id"),
    sourceUrl = o.optString("sourceUrl"),
    title = o.optString("title"),
    outputUri = o.optString("outputUri"),
    fileName = o.optString("fileName"),
    mimeType = o.optString("mimeType"),
    completedAt = o.optLong("completedAt")
)

private fun settingsFromJson(o: JSONObject): DownloadSettings {
    val defaults = DownloadSettings()
    return DownloadSettings(
        quality = o.optString("quality", defaults.quality),
        videoCodec = o.optString("videoCodec", defaults.videoCodec),
        container = o.optString("container", defaults.container),
        parallelDownloads = o.optInt("parallelDownloads", defaults.parallelDownloads).coerceIn(1, 4),
        fragments = o.optInt("fragments", defaults.fragments).coerceIn(1, 8),
        rateLimit = o.optString("rateLimit", defaults.rateLimit),
        audioOnly = o.optBoolean("audioOnly", defaults.audioOnly),
        sponsorBlock = o.optBoolean("sponsorBlock", defaults.sponsorBlock),
        writeSubtitles = o.optBoolean("writeSubtitles", defaults.writeSubtitles),
        writeAutoSubtitles = o.optBoolean("writeAutoSubtitles", defaults.writeAutoSubtitles),
        embedSubtitles = o.optBoolean("embedSubtitles", defaults.embedSubtitles),
        subtitleLangs = o.optString("subtitleLangs", defaults.subtitleLangs),
        embedThumbnail = o.optBoolean("embedThumbnail", defaults.embedThumbnail),
        embedMetadata = o.optBoolean("embedMetadata", defaults.embedMetadata),
        embedChapters = o.optBoolean("embedChapters", defaults.embedChapters),
        filenameTemplate = o.optString("filenameTemplate", defaults.filenameTemplate),
        retries = o.optInt("retries", defaults.retries).coerceIn(0, 10),
        retryDelaySeconds = o.optInt("retryDelaySeconds", defaults.retryDelaySeconds).coerceIn(1, 120)
    )
}
