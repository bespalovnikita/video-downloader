package ru.bespalov.video.core

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.io.File
import java.io.IOException
import java.nio.file.AtomicMoveNotSupportedException
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.util.UUID

/** One atomic Android snapshot. A damaged original is preserved and read-only. */
class JsonStateRepository(stateFile: File) : StateRepository {
    val file: File = stateFile.absoluteFile
    val backupFile: File = File(file.parentFile, "${file.name}.bak")
    private val mutex = Mutex()
    private val mutable: MutableStateFlow<AppState>
    override val state: StateFlow<AppState>
    override val readOnly: Boolean
    override val error: String

    init {
        var issue = ""
        val initial = try {
            if (file.exists()) {
                require(file.length() <= MAX_BYTES) { "Файл состояния превышает 32 МиБ." }
                JSON.decodeFromString<AppState>(file.readText(Charsets.UTF_8)).also(::validate)
            } else AppState()
        } catch (failure: Exception) {
            issue = "Не удалось прочитать состояние. Исходный файл сохранён: ${failure.message}"
            AppState()
        }
        readOnly = issue.isNotEmpty()
        error = issue
        mutable = MutableStateFlow(recoverInterrupted(initial))
        state = mutable.asStateFlow()
    }

    override suspend fun update(transform: (AppState) -> AppState): AppState = mutex.withLock {
        check(!readOnly) { error }
        withContext(Dispatchers.IO) {
            val next = transform(mutable.value)
            validate(next)
            if (next == mutable.value) return@withContext mutable.value
            val encoded = JSON.encodeToString(next).toByteArray(Charsets.UTF_8)
            require(encoded.size <= MAX_BYTES) { "Файл состояния превышает 32 МиБ." }
            if (!file.parentFile.isDirectory && !file.parentFile.mkdirs()) throw IOException("Не удалось создать папку настроек.")
            val temporary = File(file.parentFile, ".state-${UUID.randomUUID()}.tmp")
            try {
                temporary.outputStream().use { stream -> stream.write(encoded); stream.fd.sync() }
                if (file.exists()) Files.copy(file.toPath(), backupFile.toPath(), StandardCopyOption.REPLACE_EXISTING)
                try {
                    Files.move(temporary.toPath(), file.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING)
                } catch (_: AtomicMoveNotSupportedException) {
                    // Both files are in the same private directory. Never
                    // delete the existing state to make a failed rename work.
                    if (!temporary.renameTo(file)) throw IOException("Не удалось атомарно сохранить настройки.")
                }
                mutable.value = next
                next
            } finally { temporary.delete() }
        }
    }

    companion object {
        const val MAX_BYTES = 32 * 1024 * 1024
        val JSON = Json { encodeDefaults = true; ignoreUnknownKeys = true; prettyPrint = true }

        fun recoverInterrupted(state: AppState): AppState = state.copy(queue = state.queue.map {
            when (it.status) {
                // The success transaction precedes private staging cleanup.
                // Process death in that window must preserve history while
                // preventing a fresh download from orphaning the old stage.
                DownloadStatus.COMPLETED -> if (it.stageDirectory.isNotBlank())
                    it.copy(status = DownloadStatus.CLEANUP_REQUIRED, speed = "", eta = "") else it
                DownloadStatus.RUNNING, DownloadStatus.CANCELLING -> it.copy(status =
                    if (it.outputUris.isNotEmpty() || it.pendingRemoval) DownloadStatus.CLEANUP_REQUIRED else DownloadStatus.INTERRUPTED,
                    runId = "", speed = "", eta = "")
                else -> it
            }
        })

        fun validate(state: AppState) {
            require(state.version == 1) { "Неподдерживаемая версия состояния: ${state.version}" }
            SettingsRules.validate(state.settings)
            require(state.queue.map { it.id }.distinct().size == state.queue.size && state.queue.map { it.url }.distinct().size == state.queue.size) { "Повторные задания в очереди." }
            require(state.queue.all { validId(it.id) && Urls.isHttp(it.url) && it.progress.isFinite() && it.progress in 0f..100f && it.transferredBytes >= 0 &&
                ((it.subscriptionId.isEmpty() && it.subscriptionName.isEmpty()) || (validId(it.subscriptionId) && SubscriptionNames.isValid(it.subscriptionName))) }) { "Некорректная очередь." }
            state.queue.forEach { task -> task.settingsSnapshot?.let(SettingsRules::validate) }
            require(state.subscriptions.size <= 1000 && state.subscriptions.map { it.id }.distinct().size == state.subscriptions.size &&
                state.subscriptions.all { validId(it.id) && Urls.isHttp(it.url) && (it.shortName.isEmpty() || SubscriptionNames.isValid(it.shortName)) && (it.feedUrl.isEmpty() || Urls.isHttp(it.feedUrl)) }) { "Некорректные подписки." }
            val sourceIds = state.subscriptions.map { it.id }.toSet()
            require(state.items.map { it.subscriptionId to it.url }.distinct().size == state.items.size &&
                state.items.all { it.subscriptionId in sourceIds && Urls.isHttp(it.url) && it.discoveredAtUtc >= 0 }) { "Некорректные материалы подписок." }
            require(state.history.all { Urls.isHttp(it.url) && it.completedAtUtc >= 0 }) { "Некорректная история." }
        }

        private fun validId(value: String) = Regex("^[A-Za-z0-9_-]{1,80}$").matches(value)
    }
}
