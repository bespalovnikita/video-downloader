package ru.bespalov.videodownloader.engine

import java.io.File

enum class EngineStage { STARTING, DOWNLOADING, RETRYING, PROCESSING, VERIFYING, PUBLISHING }

data class EngineProgress(
    val percent: Float = 0f,
    val speed: String = "",
    val downloadedBytes: Long = 0,
    val totalBytes: Long = 0,
    val eta: String = "",
    val title: String? = null,
    val transferredBytes: Long = 0,
    val componentPercent: Float? = null,
    val component: String = "",
    val detail: String = "",
    val stage: EngineStage = EngineStage.STARTING,
    val attempt: Int = 1,
    val quality: String = "",
    val playlistIndex: Int? = null,
    val playlistCount: Int? = null,
    val componentIndex: Int? = null,
    val componentCount: Int? = null,
    val planKnown: Boolean = false,
    val invocation: Int = 1,
)

enum class EngineTraceLevel { INFORMATION, WARNING, ERROR, PROGRESS, VERBOSE }

/** Raw diagnostic text is sanitised by the journal before any retention or export. */
data class EngineTrace(
    val category: String,
    val event: String,
    val message: String = "",
    val level: EngineTraceLevel = EngineTraceLevel.INFORMATION,
    val attempt: Int = 1,
    val invocation: Int = 1,
    val quality: String = "",
    val component: String = "",
    // The Android bridge combines child output; it cannot prove stdout/stderr origin.
    val source: String = "combined",
)

data class EngineDownloadResult(
    val primary: File,
    val stagedFiles: List<File>,
    val transferredBytes: Long,
)

data class EngineMetadata(
    val url: String,
    val title: String = "",
    val uploader: String = "",
    val durationSeconds: Double = 0.0,
    val images: List<File> = emptyList(),
    val extractor: String = "",
    val maxHeight: Int = 0,
    val previewError: String = "",
    val savedAtMillis: Long = 0,
)

/** Original diagnostics are retained; presentation localization belongs to the UI. */
class EngineException(message: String, cause: Throwable? = null) : Exception(message, cause)
