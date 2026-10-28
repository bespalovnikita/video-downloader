package ru.bespalov.videodownloader.ui

import android.net.Uri
import ru.bespalov.video.core.AppState
import ru.bespalov.video.core.Settings

data class EntryKey(val subscriptionId: String, val url: String)

data class UiPreview(
    val url: String,
    val title: String = "",
    val uploader: String = "",
    val durationSeconds: Double = 0.0,
    val imageFiles: List<String> = emptyList(),
    val loading: Boolean = false,
    val error: String = "",
)

data class UiOperation(
    val title: String,
    val stage: String = "",
    val completed: Int = 0,
    val total: Int = 0,
    val startedAtMillis: Long = System.currentTimeMillis(),
    val cancellable: Boolean = false,
)

data class DuplicatePrompt(
    val url: String,
    val title: String = "",
    val completedAtMillis: Long? = null,
    val position: Int = 1,
    val total: Int = 1,
)

enum class DuplicateDecision { YES, NO, YES_ALL, NO_ALL, CANCEL }

data class DuplicateNotice(val title: String, val url: String, val sourceTitle: String = "")

data class UiMessage(val text: String, val details: String = "")

data class MobileUiState(
    val snapshot: AppState = AppState(),
    val runtimeReady: Boolean = false,
    val running: Boolean = false,
    val paused: Boolean = false,
    val operation: UiOperation? = null,
    val preview: UiPreview? = null,
    val duplicatePrompt: DuplicatePrompt? = null,
    val duplicateNotices: List<DuplicateNotice> = emptyList(),
    val message: UiMessage? = null,
    val logs: List<String> = emptyList(),
    val readOnly: Boolean = false,
    val importing: Boolean = false,
    val recoveryRequired: Boolean = false,
)

internal val MobileUiState.locked: Boolean get() = readOnly || importing

interface MobileActions {
    fun addUrls(text: String)
    fun importList(uri: Uri)
    fun importCookies(uri: Uri)
    fun selectOutput(uri: Uri)
    fun saveSettings(settings: Settings)
    fun start(taskIds: List<String>? = null)
    fun resolveDuplicate(decision: DuplicateDecision)
    fun pause()
    fun resume()
    fun stop()
    fun remove(taskId: String, deleteFiles: Boolean)
    fun clearQueue()
    fun move(taskId: String, delta: Int)
    fun showPreview(url: String, knownTitle: String = "")
    fun closePreview()
    fun addSubscription(url: String, feedUrl: String, shortName: String, downloadExisting: Boolean)
    fun checkSubscriptions(ids: List<String>)
    fun cancelCheck()
    fun toggleSource(id: String)
    fun renameSource(id: String, shortName: String)
    fun removeSource(id: String)
    fun queueEntries(keys: List<EntryKey>)
    fun skipEntries(keys: List<EntryKey>)
    fun acknowledgeDuplicates()
    fun dismissMessage()
    fun exportBackup(uri: Uri)
    fun importBackup(uri: Uri)
    fun updateRuntime()
    fun recoverQueue(restore: Boolean)
}
