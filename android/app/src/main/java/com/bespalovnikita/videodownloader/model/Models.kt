package com.bespalovnikita.videodownloader.model

import java.util.UUID

enum class QueueStatus {
    QUEUED,
    STARTING,
    DOWNLOADING,
    PAUSED,
    RETRYING,
    FINALIZING,
    DONE,
    ERROR
}

data class QueueItem(
    val id: String = UUID.randomUUID().toString(),
    val url: String,
    val title: String = "",
    val uploader: String = "",
    val thumbnailUrl: String = "",
    val localThumbnailPath: String = "",
    val durationSeconds: Long = 0,
    val maxHeight: Int = 0,
    val maxFps: Double = 0.0,
    val codecs: String = "",
    val hdr: String = "",
    val previewError: String = "",
    val previewFetchedAt: Long = 0,
    val status: QueueStatus = QueueStatus.QUEUED,
    val progress: Float = 0f,
    val etaSeconds: Long = -1,
    val statusLine: String = "",
    val outputUri: String = "",
    val error: String = "",
    val addedAt: Long = System.currentTimeMillis()
)

data class HistoryItem(
    val id: String = UUID.randomUUID().toString(),
    val sourceUrl: String,
    val title: String,
    val outputUri: String,
    val fileName: String,
    val mimeType: String,
    val completedAt: Long = System.currentTimeMillis()
)

data class DownloadSettings(
    val quality: String = "Best",
    val videoCodec: String = "Auto",
    val container: String = "Auto",
    val parallelDownloads: Int = 2,
    val fragments: Int = 2,
    val rateLimit: String = "",
    val audioOnly: Boolean = false,
    val sponsorBlock: Boolean = false,
    val writeSubtitles: Boolean = false,
    val writeAutoSubtitles: Boolean = false,
    val embedSubtitles: Boolean = false,
    val subtitleLangs: String = "ru.*,en.*",
    val embedThumbnail: Boolean = false,
    val embedMetadata: Boolean = true,
    val embedChapters: Boolean = true,
    val filenameTemplate: String = "%(title)s [%(id)s].%(ext)s",
    val retries: Int = 3,
    val retryDelaySeconds: Int = 5
)

data class AppState(
    val queue: List<QueueItem> = emptyList(),
    val history: List<HistoryItem> = emptyList(),
    val settings: DownloadSettings = DownloadSettings()
)

data class PreviewData(
    val url: String,
    val title: String = "",
    val uploader: String = "",
    val thumbnailUrl: String = "",
    val localThumbnailPath: String = "",
    val durationSeconds: Long = 0,
    val maxHeight: Int = 0,
    val maxFps: Double = 0.0,
    val codecs: String = "",
    val hdr: String = "",
    val savedAtEpochMs: Long = System.currentTimeMillis()
)

data class DownloadProgress(
    val percent: Float,
    val etaSeconds: Long,
    val line: String
)

data class DownloadResult(
    val finalPath: String,
    val commandOutput: String
)
