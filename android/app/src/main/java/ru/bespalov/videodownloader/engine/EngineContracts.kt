package ru.bespalov.videodownloader.engine

import java.io.File

data class EngineProgress(
    val percent: Float = 0f,
    val speed: String = "",
    val downloadedBytes: Long = 0,
    val totalBytes: Long = 0,
    val eta: String = "",
    val title: String? = null,
    val transferredBytes: Long = 0,
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
)

/** Original diagnostics are retained; presentation localization belongs to the UI. */
class EngineException(message: String, cause: Throwable? = null) : Exception(message, cause)
