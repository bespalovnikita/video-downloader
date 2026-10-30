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
import ru.bespalov.video.core.StateRepository
import ru.bespalov.video.core.Urls
import ru.bespalov.videodownloader.engine.AndroidYtDlp
import ru.bespalov.videodownloader.engine.EngineMetadata
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
    private val mutableLogs = MutableStateFlow<List<String>>(emptyList())
    val logs: StateFlow<List<String>> = mutableLogs.asStateFlow()
    val runtimeReady = MutableStateFlow(false)
    val runtimeVersion = MutableStateFlow("")
    val startupError = MutableStateFlow("")
    val previews = PreviewDispatcher(engine, repository, scope, ::log)
    val ready = CompletableDeferred<Unit>()

    init {
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

    fun log(message: String) = synchronized(mutableLogs) {
        mutableLogs.value = (mutableLogs.value + message).takeLast(250)
    }
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

/** One independent metadata worker; hovering/swiping never starts another request. */
class PreviewDispatcher(
    private val engine: AndroidYtDlp,
    private val repository: StateRepository,
    private val scope: CoroutineScope,
    private val log: (String) -> Unit,
) {
    private val gate = Any()
    private val pending = LinkedHashMap<String, String>()
    private val active = HashSet<String>()
    private val completedAt = HashMap<String, Long>()
    private val signal = Channel<Unit>(Channel.CONFLATED)
    private val mutable = MutableStateFlow<Map<String, EngineMetadata>>(emptyMap())
    val metadata = mutable.asStateFlow()
    private val failures = MutableStateFlow<Map<String, String>>(emptyMap())
    val errors = failures.asStateFlow()
    private val loading = MutableStateFlow<Set<String>>(emptySet())
    val activeUrls = loading.asStateFlow()
    private val lastTitle = HashMap<String, String>()

    init {
        scope.launch {
            for (ignored in signal) {
                while (true) {
                    val next = synchronized(gate) {
                        pending.entries.firstOrNull()?.let { (url, title) ->
                            pending.remove(url)
                            active.add(url)
                            loading.value = active.toSet()
                            url to title
                        }
                    } ?: break
                    val (url, knownTitle) = next
                    try {
                        val cookie = repository.state.value.settings.cookiesFile.takeIf(String::isNotBlank)?.let(::File)
                        val result = engine.metadata("preview-${UUID.randomUUID()}", url, cookie) { update -> publish(update) }
                        publish(result)
                        synchronized(gate) {
                            completedAt[url] = System.currentTimeMillis()
                            completedAt.keys.retainAll(mutable.value.keys)
                        }
                        failures.value = failures.value - url
                    } catch (error: Exception) {
                        failures.value = (failures.value + (url to (error.message ?: "Не удалось получить метаданные"))).entries.toList().takeLast(128).associate { it.toPair() }
                        log("Preview $url: ${error.message}")
                    } finally { synchronized(gate) { active.remove(url); loading.value = active.toSet() } }
                }
            }
        }
    }

    fun request(url: String, knownTitle: String = "", priority: Boolean = false) {
        if (!Urls.isHttp(url)) return
        synchronized(gate) {
            if (url in active) return
            if (url in mutable.value && url !in failures.value &&
                System.currentTimeMillis() - (completedAt[url] ?: 0L) < 72 * 60 * 60_000L) {
                mutable.value[url]?.title?.takeIf { it.isNotBlank() && it != url }?.let { title ->
                    scope.launch { patchTitle(url, title) }
                }
                return
            }
            if (priority) {
                val previous = pending.toMap()
                pending.clear()
                pending[url] = knownTitle
                previous.filterKeys { it != url }.forEach { (key, value) -> pending[key] = value }
            } else pending.putIfAbsent(url, knownTitle)
        }
        signal.trySend(Unit)
    }

    private fun publish(value: EngineMetadata) {
        mutable.value = (mutable.value - value.url + (value.url to value)).entries.toList().takeLast(128).associate { it.toPair() }
        val title = value.title.trim()
        if (title.isBlank() || title == value.url || lastTitle[value.url] == title) return
        lastTitle[value.url] = title
        if (lastTitle.size > 256) lastTitle.remove(lastTitle.keys.first())
        scope.launch {
            patchTitle(value.url, title)
        }
    }

    private suspend fun patchTitle(url: String, title: String) {
        try {
            repository.update { state -> state.copy(queue = state.queue.map { task ->
                if (task.url == url && (task.title.isBlank() || task.title == task.url)) task.copy(title = title) else task
            }, items = state.items.map { item ->
                if (item.url == url && (item.title.isBlank() || item.title == item.url)) item.copy(title = title) else item
            }) }
        } catch (error: Exception) { log("Preview title checkpoint: ${error.message}") }
    }
}
