package ru.bespalov.videodownloader

import android.app.Application
import android.content.Intent
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.net.Uri
import android.provider.DocumentsContract
import android.provider.OpenableColumns
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.serialization.decodeFromString
import ru.bespalov.video.core.*
import ru.bespalov.videodownloader.download.DownloadService
import ru.bespalov.videodownloader.ui.*
import java.io.File
import java.util.Locale
import java.util.UUID

/** Activity observes state; download lifetime belongs exclusively to DownloadService. */
class DownloaderViewModel(application: Application) : AndroidViewModel(application), MobileActions {
    private val app = application as DownloaderApplication
    private val graph = app.graph
    private val repository = graph.repository
    private val mutable = MutableStateFlow(MobileUiState())
    val ui = mutable.asStateFlow()
    private val startGate = Mutex()
    private var decision: CompletableDeferred<DuplicateDecision>? = null
    private var checkJob: Job? = null
    private var checkRequest: String? = null
    private var maintenanceJob: Job? = null

    init {
        viewModelScope.launch { repository.state.collect { refresh() } }
        viewModelScope.launch { graph.previews.metadata.collect { refreshPreview() } }
        viewModelScope.launch { graph.previews.errors.collect { refreshPreview() } }
        viewModelScope.launch { graph.runtimeVersion.collect { version -> mutable.update { it.copy(runtimeVersion = version) } } }
        viewModelScope.launch { graph.previews.activeUrls.collect { refreshPreview() } }
        viewModelScope.launch { graph.importing.collect { importing ->
            mutable.update { it.copy(importing = importing) }
        } }
        viewModelScope.launch { graph.logs.collect { logs -> mutable.update { it.copy(logs = logs) } } }
        viewModelScope.launch { DownloadService.isRunning.collect { running -> mutable.update { it.copy(running = running) } } }
        viewModelScope.launch { DownloadService.isPaused.collect { paused -> mutable.update { it.copy(paused = paused) } } }
        viewModelScope.launch { DownloadService.progress.collect { refresh() } }
        viewModelScope.launch {
            graph.ready.await()
            mutable.update { it.copy(runtimeReady = graph.runtimeReady.value, readOnly = repository.readOnly,
                recoveryRequired = repository.state.value.queue.any { task -> task.status == DownloadStatus.INTERRUPTED }) }
            if (graph.startupError.value.isNotBlank()) message(graph.startupError.value)
            else if (repository.readOnly) message(repository.error.ifBlank { "Хранилище доступно только для чтения. Экспортируйте данные перед восстановлением." })
            repository.state.value.queue.filter { it.status != DownloadStatus.COMPLETED }.forEach { graph.previews.request(it.url, it.title) }
        }
    }

    private fun refresh() {
        val snapshot = repository.state.value
        val live = DownloadService.progress.value
        val shown = snapshot.copy(queue = snapshot.queue.map { task ->
            live[task.id]?.let { progress -> task.copy(progress = progress.percent, speed = progress.speed,
                downloaded = formatBytes(progress.downloadedBytes), eta = progress.eta,
                transferredBytes = progress.transferredBytes,
                title = progress.title?.takeIf(String::isNotBlank) ?: task.title) } ?: task
        })
        val names = snapshot.subscriptions.associate { it.id to it.title.ifBlank { it.shortName } }
        mutable.update { it.copy(snapshot = shown, readOnly = repository.readOnly,
            duplicateNotices = snapshot.items.filter { row -> row.state == EntryState.ALREADY_PROCESSED && !row.duplicateNotified }
                .map { row -> DuplicateNotice(row.title, row.url, names[row.duplicateSubscriptionId].orEmpty()) }) }
    }

    private fun refreshPreview() {
        mutable.update { state ->
            val current = state.preview ?: return@update state
            val data = graph.previews.metadata.value[current.url]
            val error = graph.previews.errors.value[current.url].orEmpty()
            state.copy(preview = current.copy(title = data?.title?.takeIf(String::isNotBlank) ?: current.title,
                uploader = data?.uploader.orEmpty(), durationSeconds = data?.durationSeconds ?: 0.0,
                imageFiles = data?.images?.map(File::getAbsolutePath).orEmpty(),
                loading = current.url in graph.previews.activeUrls.value || (data == null && error.isBlank()), error = error))
        }
    }

    private fun display(raw: String) = if (raw.isBlank()) "" else ErrorText.display(raw, Locale.getDefault().language)
    private fun message(raw: String) {
        mutable.update { it.copy(message = UiMessage(display(raw), raw)) }
        graph.log(raw)
    }
    private fun work(block: suspend () -> Unit) {
        viewModelScope.launch {
            // Persist shared/added URLs as soon as storage opens; first-run native
            // extraction must not hold incoming links only in Activity memory.
            try { repository.awaitInitialized(); block() }
            catch (cancel: CancellationException) { throw cancel }
            catch (error: Exception) { message(error.message ?: error.javaClass.simpleName) }
        }
    }
    private fun writable() {
        check(!graph.importing.value) { "Дождитесь восстановления резервной копии." }
        check(!repository.readOnly) { repository.error.ifBlank { "Хранилище доступно только для чтения." } }
    }
    private fun idle() { check(!DownloadService.isRunning.value && checkJob?.isActive != true && maintenanceJob?.isActive != true) { "Дождитесь окончания загрузки или проверки." } }

    override fun addUrls(text: String) = work {
        if (isQueueTransfer(text)) importPortable(text) else add(Urls.extract(text))
    }

    private fun isQueueTransfer(text: String): Boolean = PortableQueue.isPortable(text)

    private suspend fun queueText(): String = withContext(Dispatchers.IO) {
        PortableQueue.encode(repository.state.value.queue.map { PortableQueue.Entry(it.url, it.title) })
    }

    override fun copyQueue() = work {
        val text = queueText()
        require(text.length <= PortableQueue.MAX_CLIPBOARD_TEXT) {
            "Очередь слишком велика для буфера обмена Android. Выберите «Сохранить .vdqueue» и перенесите файл."
        }
        withContext(Dispatchers.Main) {
            try {
                val clipboard = app.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                clipboard.setPrimaryClip(ClipData.newPlainText("Video Downloader queue", text))
            } catch (error: Exception) {
                throw IllegalStateException("Не удалось скопировать очередь в буфер обмена. Выберите «Сохранить .vdqueue» и перенесите файл.", error)
            }
        }
        mutable.update { it.copy(message = UiMessage("Очередь скопирована. На другом клиенте выберите «Вставить очередь» или «Вставить».")) }
    }

    override fun exportQueue(uri: Uri) = work {
        val text = queueText()
        withContext(Dispatchers.IO) {
            app.contentResolver.openOutputStream(uri, "wt")?.use { it.write(text.toByteArray(Charsets.UTF_8)) }
                ?: error("Не удалось открыть файл очереди для записи.")
        }
        mutable.update { it.copy(message = UiMessage("Очередь сохранена. Этот файл можно открыть в Android- или Windows-клиенте.")) }
    }

    override fun importQueueText(text: String) = work { importPortable(text) }

    private suspend fun importPortable(text: String) {
        val entries = withContext(Dispatchers.IO) { PortableQueue.decode(text) }
        var added = 0
        val changed = graph.dataOperation.withLock {
            writable()
            repository.update { state -> PortableQueue.merge(state, entries).also { added = it.queue.size - state.queue.size } }
        }
        withContext(Dispatchers.IO) {
            val titles = changed.queue.associate { it.url to it.title }
            entries.forEach { entry -> graph.previews.request(entry.url, titles[entry.url].orEmpty()) }
        }
        mutable.update { it.copy(message = UiMessage("Очередь добавлена: $added новых ссылок. Загрузка запускается кнопкой Start.")) }
    }
    private suspend fun add(urls: List<String>) {
        writable()
        require(urls.isNotEmpty()) { "Не найдено ссылок HTTP(S)." }
        val changed = repository.update { state ->
            val known = state.queue.map { it.url }.toSet()
            state.copy(queue = state.queue + urls.filterNot { it in known }.map { url ->
                DownloadTask(url = url, title = graph.previews.metadata.value[url]?.title.orEmpty(),
                    deferredNextStart = DownloadService.isRunning.value)
            })
        }
        urls.forEach { url -> graph.previews.request(url, changed.queue.firstOrNull { it.url == url }?.title.orEmpty()) }
    }

    override fun importList(uri: Uri) = work {
        val name = withContext(Dispatchers.IO) { documentName(uri) }
        val bytes = readBounded(uri, PortableQueue.MAX_TEXT_BYTES)
        val text = bytes.toString(Charsets.UTF_8).removePrefix("\uFEFF")
        if (name.endsWith(".vdqueue", true) || isQueueTransfer(text)) importPortable(text)
        else {
            require(bytes.size <= 16 * 1024 * 1024) { "Файл ссылок превышает 16 МиБ." }
            add(Urls.fromFile(name, text))
        }
    }

    override fun importCookies(uri: Uri) = work {
        writable()
        val bytes = readBounded(uri, 8 * 1024 * 1024)
        val text = bytes.toString(Charsets.UTF_8).removePrefix("\uFEFF")
        require(text.lineSequence().take(4).any { it.contains("Netscape HTTP Cookie File", true) || it.contains("HTTP Cookie File", true) }) {
            "Нужен cookies.txt в формате Netscape, а не JSON или файл профиля браузера."
        }
        val file = withContext(Dispatchers.IO) {
            val directory = File(app.noBackupFilesDir, "cookies").apply { check(mkdirs() || isDirectory) }
            File(directory, "${UUID.randomUUID()}.txt").apply { outputStream().use { it.write(bytes); it.fd.sync() } }
        }
        writable()
        repository.update { it.copy(settings = it.settings.copy(cookiesFile = file.absolutePath)) }
    }

    override fun selectOutput(uri: Uri) = work {
        writable()
        require(uri.scheme == "content" && DocumentsContract.isTreeUri(uri)) { "Выберите папку в системном диалоге." }
        app.contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
        repository.update { it.copy(settings = it.settings.copy(outputTreeUri = uri.toString())) }
    }

    override fun saveSettings(settings: Settings) = work {
        writable()
        FilenameTemplates.validate(settings.filenameTemplate)
        repository.update { it.copy(settings = settings) }
    }

    override fun start(taskIds: List<String>?) = work {
        writable()
        check(graph.runtimeReady.value) { "Движок ещё не готов." }
        check(!mutable.value.recoveryRequired) { "Сначала выберите, восстанавливать ли прерванную очередь." }
        check(maintenanceJob?.isActive != true) { "Дождитесь обновления движка." }
        startGate.withLock {
          graph.dataOperation.withLock admission@{
            writable()
            if (DownloadService.isRunning.value) return@admission
            val candidates = repository.state.value.queue.filter { task ->
                (taskIds == null || task.id in taskIds) && !task.pendingRemoval &&
                    task.status in setOf(DownloadStatus.QUEUED, DownloadStatus.FAILED, DownloadStatus.INTERRUPTED, DownloadStatus.PAUSED, DownloadStatus.COMPLETED)
            }.filter { taskIds != null || it.status != DownloadStatus.COMPLETED }
            if (candidates.isEmpty()) return@admission
            val history = repository.state.value.history.groupBy { it.url }
            val repeats = candidates.filter { it.url in history }
            val rejected = mutableSetOf<String>()
            var all: Boolean? = null
            try {
                for ((index, task) in repeats.withIndex()) {
                    val accepted = all ?: run {
                        val pending = CompletableDeferred<DuplicateDecision>()
                        decision = pending
                        mutable.update { it.copy(duplicatePrompt = DuplicatePrompt(task.url, task.title,
                            history[task.url]?.maxOfOrNull { record -> record.completedAtUtc }, index + 1, repeats.size)) }
                        when (pending.await()) {
                            DuplicateDecision.YES -> true
                            DuplicateDecision.NO -> false
                            DuplicateDecision.YES_ALL -> { all = true; true }
                            DuplicateDecision.NO_ALL -> { all = false; false }
                            DuplicateDecision.CANCEL -> return@admission
                        }
                    }
                    if (!accepted) rejected += task.id
                }
            } finally {
                decision = null
                mutable.update { it.copy(duplicatePrompt = null) }
            }
            // A refusal excludes this run; queue/history/files are retained.
            val current = repository.state.value.queue.associateBy { it.id }
            val allowed = candidates.filterNot { it.id in rejected }.filter { candidate ->
                current[candidate.id]?.let { now -> now.url == candidate.url && !now.pendingRemoval &&
                    now.status == candidate.status && now.stageDirectory == candidate.stageDirectory } == true
            }.map { it.id }
            if (allowed.isNotEmpty() && !DownloadService.isRunning.value) DownloadService.start(app, allowed)
          }
        }
    }

    override fun resolveDuplicate(decision: DuplicateDecision) { this.decision?.complete(decision) }
    override fun pause() { DownloadService.pauseAll(app) }
    override fun resume() { DownloadService.resumeAll(app) }
    override fun stop() { DownloadService.stopAll(app) }

    override fun remove(taskId: String, deleteFiles: Boolean) = work {
        writable()
        val task = repository.state.value.queue.firstOrNull { it.id == taskId } ?: return@work
        if (deleteFiles || task.stageDirectory.isNotBlank() || task.status in setOf(DownloadStatus.RUNNING, DownloadStatus.CANCELLING, DownloadStatus.CLEANUP_REQUIRED)) {
            DownloadService.removeTask(app, taskId, deleteFiles)
        } else repository.update { state -> state.copy(queue = state.queue.filterNot { it.id == taskId }) }
    }

    override fun clearQueue() = work {
        writable()
        repository.update { state -> state.copy(queue = state.queue.filter {
            it.pendingRemoval || it.stageDirectory.isNotBlank() || it.status in setOf(DownloadStatus.RUNNING, DownloadStatus.PAUSED, DownloadStatus.CANCELLING, DownloadStatus.CLEANUP_REQUIRED)
        }) }
    }

    override fun move(taskId: String, delta: Int) = work {
        writable()
        check(!DownloadService.isRunning.value) { "Порядок меняется после остановки загрузки." }
        repository.update { state ->
            val rows = state.queue.toMutableList()
            val from = rows.indexOfFirst { it.id == taskId }
            val to = (from + delta).coerceIn(0, (rows.size - 1).coerceAtLeast(0))
            if (from >= 0 && from != to) rows.add(to, rows.removeAt(from))
            state.copy(queue = rows)
        }
    }

    override fun showPreview(url: String, knownTitle: String) {
        mutable.update { it.copy(preview = UiPreview(url, knownTitle, loading = true)) }
        graph.previews.request(url, knownTitle, priority = true)
        refreshPreview()
    }
    override fun closePreview() { mutable.update { it.copy(preview = null) } }

    override fun addSubscription(url: String, feedUrl: String, shortName: String, downloadExisting: Boolean) = work {
        writable()
        require(Urls.isHttp(url) && (feedUrl.isBlank() || Urls.isHttp(feedUrl))) { "Укажите HTTP(S) URL коллекции и, при необходимости, RSS/Atom." }
        require(shortName.isBlank() || SubscriptionNames.isValid(shortName)) { "Некорректное краткое имя папки." }
        val source = Subscription(url = url, feedUrl = feedUrl, shortName = shortName, downloadExisting = downloadExisting)
        repository.update { state ->
            require(state.subscriptions.none { it.url == url }) { "Такая подписка уже добавлена." }
            require(shortName.isBlank() || state.subscriptions.none { it.shortName.equals(shortName, true) }) { "Краткое имя уже занято." }
            state.copy(subscriptions = state.subscriptions + source)
        }
        checkSubscriptions(listOf(source.id))
    }

    override fun checkSubscriptions(ids: List<String>) {
        if (checkJob?.isActive == true || maintenanceJob?.isActive == true) return
        checkJob = viewModelScope.launch {
            try {
                graph.ready.await(); writable()
                check(graph.runtimeReady.value) { "Движок ещё не готов." }
                val sources = repository.state.value.subscriptions.filter { it.id in ids && !it.paused }
                val cookie = repository.state.value.settings.cookiesFile.takeIf(String::isNotBlank)?.let(::File)
                for ((index, source) in sources.withIndex()) {
                    val requestId = "scan-${UUID.randomUUID()}"
                    checkRequest = requestId
                    mutable.update { it.copy(operation = UiOperation("Проверка подписок", source.title.ifBlank { source.url }, index, sources.size, cancellable = true)) }
                    try {
                        val scan = graph.engine.scan(requestId, source.url, source.feedUrl, cookie) { stage ->
                            val translated = when (stage) { "Enumerating" -> "Полный обход"; "Parsing" -> "Разбор списка"; "Feed" -> "RSS/Atom"; "Ready" -> "Готово"; else -> stage }
                            mutable.update { it.copy(operation = it.operation?.copy(stage = "${source.title.ifBlank { source.url }}: $translated")) }
                        }
                        val applied = repository.update { state ->
                            if (state.subscriptions.none { it.id == source.id }) state else {
                                val scanned = SubscriptionRules.applyScan(state, source.id, scan)
                                val pending = scanned.items.filter { it.subscriptionId == source.id && it.initialQueuePending }
                                    .map { it.subscriptionId to it.url }.toSet()
                                SubscriptionRules.queueEntries(scanned, pending)
                            }
                        }
                        applied.items.filter { it.subscriptionId == source.id && it.state in setOf(EntryState.NEW, EntryState.QUEUED) }
                            .forEach { graph.previews.request(it.url, it.title) }
                    } catch (cancel: CancellationException) { throw cancel }
                    catch (error: Exception) {
                        val raw = error.message ?: error.javaClass.simpleName
                        repository.update { SubscriptionRules.recordFailure(it, source.id, raw) }
                        graph.log("Subscription ${source.id}: $raw")
                    } finally { checkRequest = null }
                }
            } catch (cancel: CancellationException) { throw cancel }
            catch (error: Exception) { message(error.message ?: error.javaClass.simpleName) }
            finally { checkRequest = null; mutable.update { it.copy(operation = null) } }
        }
    }

    override fun cancelCheck() { checkRequest?.let(graph.engine::cancel); checkJob?.cancel() }
    override fun toggleSource(id: String) = work {
        writable()
        repository.update { state -> state.copy(subscriptions = state.subscriptions.map { if (it.id == id) it.copy(paused = !it.paused) else it }) }
    }
    override fun renameSource(id: String, shortName: String) = work {
        writable()
        require(SubscriptionNames.isValid(shortName)) { "Некорректное краткое имя папки." }
        repository.update { state ->
            require(state.subscriptions.none { it.id != id && it.shortName.equals(shortName, true) }) { "Имя уже занято." }
            state.copy(subscriptions = state.subscriptions.map { if (it.id == id) it.copy(shortName = shortName) else it })
        }
    }
    override fun removeSource(id: String) = work { writable(); repository.update { SubscriptionRules.removeSource(it, id) } }
    override fun queueEntries(keys: List<EntryKey>) = work {
        writable()
        val state = repository.update { SubscriptionRules.queueEntries(it, keys.map { key -> key.subscriptionId to key.url }.toSet()) }
        state.queue.forEach { graph.previews.request(it.url, it.title) }
    }
    override fun skipEntries(keys: List<EntryKey>) = work {
        writable(); repository.update { SubscriptionRules.skipEntries(it, keys.map { key -> key.subscriptionId to key.url }.toSet()) }
    }
    override fun acknowledgeDuplicates() = work {
        writable(); repository.update { state -> SubscriptionRules.acknowledgeDuplicates(state,
            state.items.filter { it.state == EntryState.ALREADY_PROCESSED && !it.duplicateNotified }.map { it.subscriptionId to it.url }.toSet()) }
    }
    override fun dismissMessage() { mutable.update { it.copy(message = null) } }

    override fun exportBackup(uri: Uri) = work {
        val bytes = repository.exportBytes()
        withContext(Dispatchers.IO) { app.contentResolver.openOutputStream(uri, "wt")?.use { it.write(bytes) } ?: error("Не удалось открыть файл для записи.") }
        mutable.update { it.copy(message = UiMessage("Резервная копия сохранена. Cookies в неё не включены.")) }
    }

    override fun importBackup(uri: Uri) = work {
        graph.dataOperation.withLock {
            idle()
            check(decision == null) { "Сначала завершите подтверждение повторной загрузки." }
            graph.importing.value = true
            mutable.update { it.copy(operation = UiOperation("Восстановление резервной копии")) }
            try {
                val bytes = readBounded(uri, JsonStateRepository.MAX_BYTES)
                val safe = withContext(Dispatchers.IO) {
                    val imported = JsonStateRepository.JSON.decodeFromString<AppState>(bytes.toString(Charsets.UTF_8))
                    BackupImport.sanitize(imported)
                }
                // Service admission uses the same gate; a pending command also forbids replacement.
                idle()
                repository.replaceFromBackup(safe)
                mutable.update { it.copy(readOnly = false, recoveryRequired = safe.queue.any { row -> row.status == DownloadStatus.INTERRUPTED }) }
            } finally {
                graph.importing.value = false
                mutable.update { it.copy(operation = null) }
            }
        }
    }

    override fun updateRuntime() {
        if (maintenanceJob?.isActive == true) return
        maintenanceJob = viewModelScope.launch {
            try {
                check(!DownloadService.isRunning.value && checkJob?.isActive != true) { "Сначала остановите загрузки и проверку подписок." }
                graph.ready.await()
                writable()
                mutable.update { it.copy(operation = UiOperation("Обновление yt-dlp")) }
                val version = graph.engine.update()
                graph.runtimeVersion.value = version
                graph.log("yt-dlp updated: $version")
                mutable.update { it.copy(message = UiMessage("yt-dlp обновлён: $version")) }
            } catch (cancel: CancellationException) { throw cancel }
            catch (error: Exception) { message(error.message ?: error.javaClass.simpleName) }
            finally { mutable.update { it.copy(operation = null) } }
        }
    }

    override fun recoverQueue(restore: Boolean) = work {
        writable()
        if (!restore) {
            val cleaning = mutableListOf<String>()
            repository.update { state -> state.copy(queue = state.queue.mapNotNull { task ->
                when {
                    task.status == DownloadStatus.COMPLETED -> task
                    task.stageDirectory.isNotBlank() || task.outputUris.isNotEmpty() || task.pendingRemoval || task.status == DownloadStatus.CLEANUP_REQUIRED -> {
                        cleaning += task.id
                        task.copy(status = DownloadStatus.CLEANUP_REQUIRED, pendingRemoval = true)
                    }
                    else -> null
                }
            }) }
            cleaning.forEach { DownloadService.removeTask(app, it, false) }
        }
        mutable.update { it.copy(recoveryRequired = false) }
    }

    private suspend fun readBounded(uri: Uri, limit: Int): ByteArray = withContext(Dispatchers.IO) {
        app.contentResolver.openInputStream(uri)?.use { input ->
            val output = java.io.ByteArrayOutputStream()
            val buffer = ByteArray(8192)
            while (true) { val count = input.read(buffer); if (count < 0) break
                require(output.size().toLong() + count <= limit) { "Файл превышает допустимый размер ($limit байт)." }
                output.write(buffer, 0, count)
            }
            output.toByteArray()
        } ?: error("Не удалось открыть файл.")
    }
    private fun documentName(uri: Uri): String = app.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use {
        if (it.moveToFirst()) it.getString(0) else null
    } ?: uri.lastPathSegment.orEmpty()
    private fun formatBytes(value: Long): String = when {
        value >= 1024L * 1024 * 1024 -> "%.1f ГБ".format(value / (1024.0 * 1024 * 1024))
        value >= 1024 * 1024 -> "%.1f МБ".format(value / (1024.0 * 1024))
        else -> "${value / 1024} КБ"
    }
}
