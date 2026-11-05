package ru.bespalov.videodownloader

import android.app.Application
import android.content.Context
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.serialization.encodeToString
import ru.bespalov.video.core.AppState
import ru.bespalov.video.core.JsonStateRepository
import ru.bespalov.video.core.DiagnosticJournal
import ru.bespalov.video.core.JournalProfile
import ru.bespalov.video.core.JournalRecord
import ru.bespalov.video.core.StateRepository
import ru.bespalov.video.core.Urls
import ru.bespalov.videodownloader.engine.AndroidYtDlp
import ru.bespalov.videodownloader.engine.EngineMetadata
import ru.bespalov.videodownloader.journal.JournalFileWriter
import java.io.File
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.util.UUID

class DownloaderApplication : Application() {
    val graph: AppGraph by lazy { AppGraph(this) }
}

class AppGraph(context: Context) {
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    val dataOperation = Mutex()
    val importing = MutableStateFlow(false)
    val repository = AppRepository(File(context.filesDir, "state.json"))
    val engine = AndroidYtDlp(context.applicationContext)
    val journal = DiagnosticJournal()
    val journalFiles = JournalFileWriter(File(runCatching { context.filesDir.canonicalFile }.getOrDefault(context.filesDir), "logs"), scope, journal::fileWarning)
    private val mutableLogs = MutableStateFlow<List<String>>(emptyList())
    val logs: StateFlow<List<String>> = mutableLogs.asStateFlow()
    val runtimeReady = MutableStateFlow(false)
    val runtimeVersion = MutableStateFlow("")
    val startupError = MutableStateFlow("")
    val previews = PreviewDispatcher(engine, repository, scope, ::log)
    val ready = CompletableDeferred<Unit>()

    init {
        journal.fileSink = { journalFiles.enqueue(it); Unit }
        scope.launch {
            repository.awaitInitialized()
            repository.state.collect { journal.profile = JournalProfile.parse(it.settings.logProfile) }
        }
        scope.launch {
            var displayedRevision = -1L
            while (true) {
                val current = journal.revision
                if (current != displayedRevision) {
                    mutableLogs.value = journal.snapshotLines(maxEntries = 2000, maxBytes = 512 * 1024)
                    displayedRevision = current
                }
                delay(200)
            }
        }
        scope.launch {
            try {
                repository.initialize()
                engine.initialize()
                runtimeVersion.value = engine.version()
                log("yt-dlp ${runtimeVersion.value}; APK ${BuildConfig.VERSION_NAME}; bundled ${BuildConfig.YTDLP_VERSION}")
                runtimeReady.value = true
            } catch (error: Exception) {
                startupError.value = error.message ?: error.javaClass.simpleName
                log("Initialization: ${error.stackTraceToString()}")
            } finally { ready.complete(Unit) }
        }
    }

    fun log(message: String) = journal.writeMessage(message)
    fun log(record: JournalRecord) = journal.write(record)
}

/** Opening/parsing a potentially large snapshot never blocks Activity startup. */
class AppRepository(private val file: File) : StateRepository {
    private val gate = Mutex()
    private val initialized = CompletableDeferred<Unit>()
    @Volatile private var delegate: JsonStateRepository? = null
    private val mutable = MutableStateFlow(AppState())
    override val state = mutable.asStateFlow()
    override val readOnly: Boolean get() = delegate?.readOnly ?: true
    @Volatile private var openingError = ""
    override val error: String get() = delegate?.error ?: openingError

    suspend fun awaitInitialized() { initialized.await() }

    suspend fun initialize() = withContext(Dispatchers.IO) {
        gate.withLock {
            try {
                if (delegate == null) {
                    delegate = JsonStateRepository(file)
                    mutable.value = delegate!!.state.value
                }
            } catch (error: Exception) {
                openingError = error.message ?: "Не удалось открыть хранилище."
                throw error
            } finally { initialized.complete(Unit) }
        }
    }

    override suspend fun update(transform: (AppState) -> AppState): AppState {
        initialized.await()
        return gate.withLock {
          currentCoroutineContext().ensureActive()
          withContext(NonCancellable) {
            val current = checkNotNull(delegate) { error.ifBlank { "Хранилище недоступно." } }
            current.update(transform).also { mutable.value = it }
          }
        }
    }

    suspend fun exportBytes(): ByteArray = withContext(Dispatchers.IO) {
        initialized.await()
        gate.withLock {
            if (readOnly && file.exists()) file.readBytes()
            else JsonStateRepository.JSON.encodeToString(state.value).toByteArray(Charsets.UTF_8)
        }
    }

    /** Only an explicit, validated import may replace a damaged snapshot. */
    suspend fun replaceFromBackup(value: AppState) = withContext(Dispatchers.IO) {
        initialized.await()
        gate.withLock {
          currentCoroutineContext().ensureActive()
          withContext(NonCancellable) {
            JsonStateRepository.validate(value)
            val bytes = JsonStateRepository.JSON.encodeToString(value).toByteArray(Charsets.UTF_8)
            require(bytes.size <= JsonStateRepository.MAX_BYTES) { "Резервная копия слишком велика." }
            val temporary = File(file.parentFile, ".restore-${UUID.randomUUID()}.tmp")
            file.parentFile?.mkdirs()
            try {
                temporary.outputStream().use { it.write(bytes); it.fd.sync() }
                if (file.exists()) file.copyTo(File(file.parentFile, "state-before-import-${System.currentTimeMillis()}.json"))
                Files.move(temporary.toPath(), file.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING)
                delegate = JsonStateRepository(file)
                check(!delegate!!.readOnly) { delegate!!.error }
                mutable.value = delegate!!.state.value
            } finally { temporary.delete() }
          }
        }
    }
}

/** Metadata acquisition is replaceable independently from its process-wide scheduling policy. */
internal fun interface PreviewLoader {
    suspend fun load(requestId: String, url: String, cookies: File?,
        onDiagnostic: (ru.bespalov.videodownloader.engine.PreviewDiagnostic) -> Unit,
        onUpdate: (EngineMetadata) -> Unit): EngineMetadata
}

/** One independent metadata worker; hovering/swiping never starts another request. */
class PreviewDispatcher internal constructor(
    private val loader: PreviewLoader,
    private val repository: StateRepository,
    private val scope: CoroutineScope,
    private val log: (JournalRecord) -> Unit,
    private val clock: () -> Long = System::currentTimeMillis,
) {
    constructor(engine: AndroidYtDlp, repository: StateRepository, scope: CoroutineScope, log: (JournalRecord) -> Unit) :
        this(PreviewLoader { requestId, url, cookies, onDiagnostic, onUpdate ->
            engine.metadata(requestId, url, cookies, onDiagnostic, onUpdate)
        }, repository, scope, log)

    private val gate = Any()
    private val pending = LinkedHashMap<String, String>()
    private val active = HashSet<String>()
    private val signal = Channel<Unit>(Channel.CONFLATED)
    private val mutable = MutableStateFlow<Map<String, EngineMetadata>>(emptyMap())
    val metadata = mutable.asStateFlow()
    private val failures = MutableStateFlow<Map<String, String>>(emptyMap())
    val errors = failures.asStateFlow()
    private val loading = MutableStateFlow<Set<String>>(emptySet())
    val activeUrls = loading.asStateFlow()

    init {
        scope.launch {
            for (ignored in signal) {
                while (true) {
                    val next = synchronized(gate) {
                        pending.entries.firstOrNull()?.let { (url, title) ->
                            pending.remove(url)
                            url to title
                        }
                    } ?: break
                    val (url, knownTitle) = next
                    val cached = synchronized(gate) { mutable.value[url] }
                    // File validation belongs to this background owner, never the
                    // caller's UI thread. Paging already downloaded images does no I/O here.
                    if (cached != null && ru.bespalov.videodownloader.engine.PreviewCachePolicy.isFresh(cached, clock()) &&
                        cached.images.all(File::isFile)) {
                        val title = cached.title.takeIf { it.isNotBlank() && it != url } ?: knownTitle
                        if (title.isNotBlank() && title != url) patchTitle(url, title)
                        continue
                    }
                    synchronized(gate) { active.add(url); loading.value = active.toSet() }
                    try {
                        val cookie = repository.state.value.settings.cookiesFile.takeIf(String::isNotBlank)?.let(::File)
                        val result = loader.load("preview-${UUID.randomUUID()}", url, cookie, ::diagnostic) { update ->
                            if (update.url == url) publish(update, knownTitle)
                        }
                        require(result.url == url) { "Preview metadata belongs to a different URL" }
                        publish(result.copy(savedAtMillis = result.savedAtMillis.takeUnless { it == 0L } ?: clock()), knownTitle)
                    } catch (error: Exception) {
                        if (error is kotlinx.coroutines.CancellationException) currentCoroutineContext().ensureActive()
                        val reason = ru.bespalov.video.core.JournalSanitizer.redact(error.message ?: error.javaClass.simpleName)
                        val previous = synchronized(gate) { mutable.value[url] }
                        publish((previous ?: EngineMetadata(url, title = knownTitle)).copy(
                            previewError = reason, savedAtMillis = clock()), knownTitle)
                        diagnostic(ru.bespalov.videodownloader.engine.PreviewDiagnostic(
                            "metadata", url, reason = reason))
                    } finally { synchronized(gate) { active.remove(url); loading.value = active.toSet() } }
                }
            }
        }
    }

    fun request(url: String, knownTitle: String = "", priority: Boolean = false) {
        if (!Urls.isHttp(url)) return
        synchronized(gate) {
            if (url in active) return
            val title = pending[url]?.takeIf { it.isNotBlank() && it != url } ?: knownTitle
            if (priority) {
                val previous = pending.toMap()
                pending.clear()
                pending[url] = title
                previous.filterKeys { it != url }.forEach { (key, value) -> pending[key] = value }
            } else pending[url] = title
        }
        signal.trySend(Unit)
    }

    private fun publish(value: EngineMetadata, knownTitle: String) {
        val title = synchronized(gate) {
            val previous = mutable.value[value.url]
            val combined = value.copy(
                title = value.title.takeIf { it.isNotBlank() && it != value.url }
                    ?: previous?.title?.takeIf { it.isNotBlank() && it != value.url } ?: knownTitle,
                uploader = value.uploader.ifBlank { previous?.uploader.orEmpty() },
                durationSeconds = value.durationSeconds.takeIf { it.isFinite() && it > 0 } ?: previous?.durationSeconds ?: 0.0,
                images = value.images.filter(File::isFile).takeIf { it.isNotEmpty() }
                    ?: previous?.images.orEmpty().filter(File::isFile),
                extractor = value.extractor.ifBlank { previous?.extractor.orEmpty() },
                maxHeight = maxOf(value.maxHeight, previous?.maxHeight ?: 0),
                previewError = ru.bespalov.video.core.JournalSanitizer.redact(value.previewError),
            ).let { result -> result.copy(title = bounded(result.title, 4096), uploader = bounded(result.uploader, 1024),
                extractor = bounded(result.extractor, 256), images = result.images.take(15)) }
            mutable.value = (mutable.value - value.url + (value.url to combined)).entries.toList().takeLast(128).associate { it.toPair() }
            failures.value = (failures.value - value.url + if (combined.previewError.isNotBlank())
                mapOf(value.url to combined.previewError) else emptyMap()).filterKeys { it in mutable.value }
            val goodTitle = combined.title.trim()
            goodTitle.takeIf { it.isNotBlank() && it != value.url }
        }
        if (title != null) scope.launch { patchTitle(value.url, title) }
    }

    private fun bounded(value: String, length: Int): String {
        var size = minOf(value.length, length)
        if (size > 0 && value[size - 1].isHighSurrogate()) size--
        return value.take(size)
    }

    private fun diagnostic(value: ru.bespalov.videodownloader.engine.PreviewDiagnostic) {
        fun safe(text: String) = ru.bespalov.video.core.JournalSanitizer.redact(text)
        val message = "stage=${safe(value.stage)} image_index=${value.imageIndex} " +
            "http_status=${value.httpStatus ?: "n/a"} bytes=${value.bytes ?: "n/a"} " +
            "candidate=${safe(value.candidateUrl).ifBlank { "none" }} reason=${safe(value.reason)}"
        try { log(JournalRecord(ru.bespalov.video.core.JournalLevel.Warning, "preview", message,
            ru.bespalov.video.core.JournalContext(video = value.videoUrl))) }
        catch (_: Exception) { /* Optional diagnostics do not stop metadata acquisition. */ }
    }

    private suspend fun patchTitle(url: String, title: String) {
        try {
            val current = repository.state.value
            if (current.queue.none { it.url == url && (it.title.isBlank() || it.title == url) } &&
                current.items.none { it.url == url && (it.title.isBlank() || it.title == url) }) return
            repository.update { state -> state.copy(queue = state.queue.map { task ->
                if (task.url == url && (task.title.isBlank() || task.title == task.url)) task.copy(title = title) else task
            }, items = state.items.map { item ->
                if (item.url == url && (item.title.isBlank() || item.title == item.url)) item.copy(title = title) else item
            }) }
        } catch (error: Exception) {
            if (error is kotlinx.coroutines.CancellationException) currentCoroutineContext().ensureActive()
            diagnostic(ru.bespalov.videodownloader.engine.PreviewDiagnostic("title_checkpoint", url,
                reason = error.message ?: error.javaClass.simpleName))
        }
    }
}
