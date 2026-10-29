package ru.bespalov.video.core

import kotlinx.serialization.Serializable
import java.util.UUID

@Serializable
data class Settings(
    val profile: String = "Universal",
    val quality: String = "Best",
    val codec: String = "Auto",
    val container: String = "Auto",
    val threads: Int = 2,
    val fragments: Int = 4,
    val rateLimit: String = "Unlimited",
    val filenameTemplate: String = "%(title)s [%(id)s].%(ext)s",
    val subtitleLangs: String = "ru.*,en.*",
    val writeSubs: Boolean = false,
    val autoSubs: Boolean = false,
    val embedSubs: Boolean = false,
    val embedThumbnail: Boolean = false,
    val embedMetadata: Boolean = true,
    val embedChapters: Boolean = true,
    val audioOnly: Boolean = false,
    val archive: Boolean = false,
    val sponsor: Boolean = false,
    val cookiesFile: String = "",
    val outputTreeUri: String = "",
    val showTitles: Boolean = true,
)

@Serializable
enum class DownloadStatus { QUEUED, RUNNING, PAUSED, COMPLETED, FAILED, INTERRUPTED, CANCELLING, CLEANUP_REQUIRED }

@Serializable
data class DownloadTask(
    val id: String = UUID.randomUUID().toString(),
    val url: String,
    val title: String = "",
    val status: DownloadStatus = DownloadStatus.QUEUED,
    val progress: Float = 0f,
    val speed: String = "",
    val downloaded: String = "",
    val eta: String = "",
    val transferredBytes: Long = 0,
    val rawError: String = "",
    val subscriptionId: String = "",
    val subscriptionName: String = "",
    val outputUris: List<String> = emptyList(),
    val stageDirectory: String = "",
    val runId: String = "",
    val deferredNextStart: Boolean = false,
    val pendingRemoval: Boolean = false,
    val settingsSnapshot: Settings? = null,
    val archiveDate: String = "",
)

@Serializable
data class Subscription(
    val id: String = UUID.randomUUID().toString(),
    val url: String,
    val title: String = "",
    val shortName: String = "",
    val feedUrl: String = "",
    val downloadExisting: Boolean = false,
    val initialized: Boolean = false,
    val paused: Boolean = false,
    val lastAttemptUtc: Long? = null,
    val lastSuccessUtc: Long? = null,
    val lastError: String = "",
    val lastWarning: String = "",
)

@Serializable
enum class EntryState { BASELINE, NEW, QUEUED, PROCESSED, ALREADY_PROCESSED, SKIPPED }

@Serializable
data class SubscriptionEntry(
    val subscriptionId: String,
    val url: String,
    val title: String = "",
    val state: EntryState = EntryState.NEW,
    val discoveredAtUtc: Long = System.currentTimeMillis(),
    val initialQueuePending: Boolean = false,
    val successAttributed: Boolean = false,
    val duplicateSubscriptionId: String = "",
    val duplicateNotified: Boolean = false,
)

@Serializable
data class SuccessRecord(
    val url: String,
    val completedAtUtc: Long = System.currentTimeMillis(),
    val subscriptionId: String = "",
    val outputUris: List<String> = emptyList(),
)

@Serializable
data class AppState(
    val version: Int = 1,
    val settings: Settings = Settings(),
    val queue: List<DownloadTask> = emptyList(),
    val subscriptions: List<Subscription> = emptyList(),
    val items: List<SubscriptionEntry> = emptyList(),
    val history: List<SuccessRecord> = emptyList(),
)

data class DiscoveredMedia(val url: String, val title: String = "")
data class FullScan(
    val title: String,
    val items: List<DiscoveredMedia>,
    val feedUrl: String = "",
    val warning: String = "",
    val suggestedName: String = "",
)

@Serializable
data class PreviewMetadata(
    val url: String,
    val title: String = "",
    val uploader: String = "",
    val durationSeconds: Double = 0.0,
    val imageUrls: List<String> = emptyList(),
)
