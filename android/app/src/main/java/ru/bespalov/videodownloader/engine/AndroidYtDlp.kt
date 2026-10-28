package ru.bespalov.videodownloader.engine

import android.content.Context
import com.yausername.ffmpeg.FFmpeg
import com.yausername.youtubedl_android.YoutubeDL
import com.yausername.youtubedl_android.YoutubeDLRequest
import com.yausername.youtubedl_android.YoutubeDLResponse
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import org.json.JSONObject
import ru.bespalov.video.core.DownloadTask
import ru.bespalov.video.core.ErrorText
import ru.bespalov.video.core.FullScan
import ru.bespalov.video.core.PornHubCollections
import ru.bespalov.video.core.RssParser
import ru.bespalov.video.core.ScanParser
import ru.bespalov.video.core.Settings
import ru.bespalov.video.core.Urls
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.net.SocketTimeoutException
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

/** Android-specific process bridge. Publication and successful history belong to the service. */
class AndroidYtDlp(context: Context) {
    private val app = context.applicationContext
    private val initialization = Mutex()
    private val metadataWorker = Mutex()
    private val scanWorker = Mutex()
    private var initialized = false
    private val admission = Mutex()
    private var updating = false
    private val operations = ConcurrentHashMap<String, Operation>()
    private val executor = Executors.newCachedThreadPool { task -> Thread(task, "yt-dlp-android").apply { isDaemon = true } }
    private val galleries = PreviewGallery(app)
    private val pluginRoot = File(app.filesDir, "collection-plugins")

    suspend fun initialize() = withContext(Dispatchers.IO) {
        initialization.withLock {
            if (!initialized) {
                YoutubeDL.init(app)
                FFmpeg.init(app)
                val relative = "videodownloader/yt_dlp_plugins/extractor/vd_pornhub.py"
                val target = File(pluginRoot, relative)
                target.parentFile!!.mkdirs()
                app.assets.open(relative).use { input -> target.outputStream().use(input::copyTo) }
                initialized = true
            }
        }
    }

    suspend fun metadata(requestId: String, url: String, cookies: File?, onUpdate: (EngineMetadata) -> Unit = {}): EngineMetadata =
        withContext(Dispatchers.IO) {
            require(Urls.isHttp(url)) { "Expected an HTTP(S) URL" }
            metadataWorker.withLock {
                initialize()
                galleries.get(url)?.let { return@withLock it }
                withTimeout(30_000) {
                    val response = execute(requestId, request(url, YtDlpArguments.metadata(), cookies), 30_000)
                    var json = JSONObject(response.out)
                    if (!json.has("formats") && json.optJSONArray("entries")?.length()?.let { it > 0 } == true)
                        json = json.getJSONArray("entries").getJSONObject(0)
                    galleries.fetch(url, json, onUpdate)
                }
            }
        }

    suspend fun scan(requestId: String, url: String, feedUrl: String? = null, cookies: File?, onProgress: (String) -> Unit = {}): FullScan =
        withContext(Dispatchers.IO) {
            require(Urls.isHttp(url)) { "Expected an HTTP(S) source URL" }
            scanWorker.withLock {
                initialize()
                val special = PornHubCollections.listingUrl(url) != null
                withTimeout(if (special) 15 * 60_000L else 180_000L) {
                    report(onProgress, "Enumerating")
                    val arguments = mutableListOf("--ignore-config", "--dump-single-json", "--skip-download", "--abort-on-error",
                        "--no-color", "--no-progress", "--encoding", "utf-8", "--socket-timeout", "20")
                    if (special) arguments += listOf("--no-plugin-dirs", "--plugin-dirs", pluginRoot.path,
                        "--use-extractors", "videodownloader:pornhub:channel", "--flat-playlist",
                        "--extractor-args", "VideoDownloaderPornHubChannel:max_items=10000;max_pages=500")
                    else arguments += listOf("--no-flat-playlist", "--playlist-end", "10001")
                    val response = execute(requestId, request(url, arguments, cookies), if (special) 900_000 else 180_000)
                    if (Regex("\\bERROR:|\\bincomplete\\b|\\btruncat\\w*|\\bpartial (?:playlist|collection)|\\bgiving up after", RegexOption.IGNORE_CASE)
                            .containsMatchIn(response.err)) throw EngineException("Full collection enumeration was incomplete: " + response.err.take(2000))
                    report(onProgress, "Parsing")
                    var scan = ScanParser.parse(response.out, url)
                    currentCoroutineContext().ensureActive()
                    val feed = feedUrl?.takeIf(String::isNotBlank) ?: scan.feedUrl
                    if (feed.isNotBlank()) {
                        report(onProgress, "Feed")
                        try {
                            require(Urls.isHttp(feed)) { "Invalid feed URL" }
                            val xml = withTimeout(12_000) { httpBytes(feed, 2 * 1024 * 1024, 12_000).toString(Charsets.UTF_8) }
                            scan = RssParser.enrich(scan, RssParser.parse(xml)).copy(feedUrl = feed)
                        } catch (cancel: CancellationException) {
                            currentCoroutineContext().ensureActive()
                            scan = scan.copy(feedUrl = feed, warning = "Лента не ответила вовремя; использован полный обход yt-dlp.")
                        } catch (error: Exception) {
                            scan = scan.copy(feedUrl = feed, warning = "Лента недоступна; использован полный обход yt-dlp. " + error.message.orEmpty())
                        }
                    }
                    currentCoroutineContext().ensureActive()
                    report(onProgress, "Ready")
                    scan
                }
            }
        }

    suspend fun download(requestId: String, task: DownloadTask, settings: Settings, stage: File,
                         onProgress: (EngineProgress) -> Unit = {}): EngineDownloadResult = withContext(Dispatchers.IO) {
        require(Urls.isHttp(task.url)) { "Expected an HTTP(S) URL" }
        initialize()
        validateStage(stage)
        stage.mkdirs()
        var transferred = 0L
        val levels = YtDlpArguments.qualityLevels(settings)
        for ((qualityIndex, quality) in levels.withIndex()) {
            for (attempt in 0..3) {
                currentCoroutineContext().ensureActive()
                var title = task.title
                val finalPaths = linkedSetOf<String>()
                val counters = mutableMapOf<String, Long>()
                try {
                    execute(requestId, request(task.url, YtDlpArguments.download(settings, stage, quality), cookie(settings.cookiesFile)),
                        6 * 60 * 60_000L, outputLimit = 4 * 1024 * 1024) { line ->
                        when (val marker = ProgressMarkers.parse(line)) {
                            is MarkerEvent.Title -> { title = marker.value; report(onProgress, EngineProgress(title = title, transferredBytes = transferred)) }
                            is MarkerEvent.Media -> finalPaths += marker.path
                            is MarkerEvent.Progress -> {
                                val old = counters.put(marker.component, marker.value.downloadedBytes) ?: 0L
                                val now = marker.value.downloadedBytes
                                transferred += if (now < old) now else now - old
                                report(onProgress, marker.value.copy(title = title, transferredBytes = transferred))
                            }
                            null -> Unit
                        }
                    }
                    currentCoroutineContext().ensureActive()
                    if (finalPaths.isEmpty()) throw EngineException("yt-dlp finished without reporting a downloaded media file")
                    val mediaFiles = finalPaths.map(::File)
                    mediaFiles.forEach { media ->
                        currentCoroutineContext().ensureActive()
                        validateOwned(stage, media)
                        NativeMedia.verify(app, media)
                        if (!settings.audioOnly && settings.container != "Auto" && !media.extension.equals(settings.container, ignoreCase = true))
                            throw EngineException("yt-dlp did not produce the requested container: ${settings.container}")
                    }
                    val files = stage.walkTopDown().onEnter { folder -> validateOwned(stage, folder, allowRoot = true); true }
                        .filter(File::isFile).onEach { validateOwned(stage, it) }
                        .filterNot { it.name.endsWith(".part") || it.name.contains(".part-Frag") || it.name.endsWith(".ytdl") || it.name.endsWith(".temp") }
                        .toList()
                    val manifest = files.map(File::getCanonicalFile).toSet()
                    require(mediaFiles.all { it.canonicalFile in manifest }) { "Downloaded media was outside the staging manifest" }
                    // Keep the same lexical path spelling as the walked
                    // manifest. yt-dlp may report Android's canonical OS alias.
                    val primary = files.first { it.canonicalFile == mediaFiles.first().canonicalFile }
                    currentCoroutineContext().ensureActive()
                    return@withContext EngineDownloadResult(primary, files, transferred)
                } catch (cancel: CancellationException) { throw cancel }
                catch (error: Exception) {
                    val diagnostic = error.message.orEmpty()
                    if (diagnostic.contains("Requested format is not available", ignoreCase = true) && qualityIndex < levels.lastIndex) break
                    if (attempt < 3 && ErrorText.isTransient(diagnostic)) { delay(5_000L shl attempt); continue }
                    throw error
                }
            }
        }
        throw EngineException("No suitable quality available")
    }

    /** Marks cancellation; completion is still awaited before the caller may clean its stage. */
    fun cancel(requestId: String): Boolean {
        val operation = operations[requestId] ?: return false
        operation.cancelled.set(true)
        YoutubeDL.destroyProcessById(operation.processId)
        return true
    }

    suspend fun update(): String = withContext(Dispatchers.IO) {
        initialize()
        admission.withLock {
            check(!updating && operations.isEmpty()) { "Дождитесь окончания получения метаданных и других операций yt-dlp перед обновлением." }
            updating = true
        }
        try {
            YoutubeDL.updateYoutubeDL(app, YoutubeDL.UpdateChannel.STABLE)
            YoutubeDL.version(app).orEmpty()
        } finally {
            withContext(NonCancellable) { admission.withLock { updating = false } }
        }
    }

    private suspend fun execute(requestId: String, request: YoutubeDLRequest, timeout: Long,
                                outputLimit: Int = 32 * 1024 * 1024, line: (String) -> Unit = {}): YoutubeDLResponse {
        currentCoroutineContext().ensureActive()
        val operation = Operation(sha256(requestId))
        admission.withLock {
            check(!updating) { "Сейчас обновляется yt-dlp. Повторите действие после обновления." }
            check(operations.putIfAbsent(requestId, operation) == null) { "A request with this ID is already running" }
        }
        val completed = CompletableDeferred<Result<YoutubeDLResponse>>()
        executor.execute {
            completed.complete(runCatching {
                if (operation.cancelled.get()) throw CancellationException("Request cancelled")
                val count = AtomicLong()
                val response = YoutubeDL.execute(request, operation.processId, callback = { _, _, text ->
                    val size = count.addAndGet(text.toByteArray(Charsets.UTF_8).size.toLong() + 1)
                    if (size > outputLimit) { operation.tooLarge.set(true); operation.cancelled.set(true); YoutubeDL.destroyProcessById(operation.processId) }
                    else if (!operation.cancelled.get()) line(text)
                })
                if (response.out.toByteArray(Charsets.UTF_8).size.toLong() + response.err.toByteArray(Charsets.UTF_8).size > outputLimit)
                    throw EngineException("yt-dlp output exceeds the configured limit")
                response
            })
        }
        try {
            val response = withTimeout(timeout) {
                while (!completed.isCompleted) {
                    if (operation.cancelled.get()) YoutubeDL.destroyProcessById(operation.processId)
                    delay(50)
                }
                completed.await().getOrThrow()
            }
            if (operation.tooLarge.get()) throw EngineException("yt-dlp output exceeds the configured limit")
            if (operation.cancelled.get()) throw CancellationException("Request cancelled")
            return response
        } catch (cancel: CancellationException) {
            operation.cancelled.set(true)
            // Do not return cancellation while yt-dlp still owns files. The
            // dependency waits for its stdout/stderr readers; unlike Windows
            // Job Objects it cannot guarantee killing every detached child.
            withContext(NonCancellable) {
                while (!completed.isCompleted) { YoutubeDL.destroyProcessById(operation.processId); delay(50) }
                completed.await()
            }
            throw cancel
        } catch (error: Exception) {
            if (operation.tooLarge.get()) throw EngineException("yt-dlp output exceeds the configured limit", error)
            if (operation.cancelled.get() || error is YoutubeDL.CanceledException) throw CancellationException("Request cancelled", error)
            throw EngineException(error.message ?: "yt-dlp request failed", error)
        } finally {
            withContext(NonCancellable) { admission.withLock { operations.remove(requestId, operation) } }
        }
    }

    private fun request(url: String, arguments: List<String>, cookies: File?): YoutubeDLRequest = YoutubeDLRequest(url).apply {
        addCommands(arguments)
        cookies?.takeIf(File::isFile)?.let { addOption("--cookies", it.path) }
    }

    private fun cookie(path: String): File? = path.takeIf(String::isNotBlank)?.let(::File)

    private fun validateStage(stage: File) {
        PrivatePaths.childOfAny(listOf(File(app.filesDir, "staging"), File(app.cacheDir, "staging")), stage)
    }

    private fun validateOwned(stage: File, file: File, allowRoot: Boolean = false) {
        validateStage(stage)
        PrivatePaths.child(stage, file, allowRoot)
    }

    private fun <T> report(observer: (T) -> Unit, value: T) { runCatching { observer(value) } }
    private data class Operation(val processId: String, val cancelled: AtomicBoolean = AtomicBoolean(), val tooLarge: AtomicBoolean = AtomicBoolean())
}

internal fun sha256(text: String): String = MessageDigest.getInstance("SHA-256").digest(text.toByteArray(Charsets.UTF_8))
    .joinToString("") { "%02x".format(it) }

private val httpDeadlines = Executors.newSingleThreadScheduledExecutor { task ->
    Thread(task, "preview-http-deadline").apply { isDaemon = true }
}

internal suspend fun httpBytes(address: String, maxBytes: Int, timeoutMs: Int): ByteArray = withContext(Dispatchers.IO) {
    require(Urls.isHttp(address)) { "Expected an HTTP(S) address" }
    require(maxBytes > 0 && timeoutMs > 0)
    val connection = URL(address).openConnection() as HttpURLConnection
    connection.connectTimeout = timeoutMs
    connection.readTimeout = timeoutMs
    connection.setRequestProperty("User-Agent", "Mozilla/5.0 VideoDownloader-Android")
    // A per-read timeout alone lets a server keep a worker forever by sending
    // one byte before every timeout. Bound headers and the complete body too.
    val timedOut = AtomicBoolean(false)
    val deadline = httpDeadlines.schedule({ timedOut.set(true); connection.disconnect() }, timeoutMs.toLong(), TimeUnit.MILLISECONDS)
    try {
        connection.connect()
        if (connection.responseCode !in 200..299) throw IOException("HTTP Error ${connection.responseCode}: ${connection.responseMessage}")
        if (connection.contentLengthLong > maxBytes) throw IOException("HTTP response exceeds its size limit")
        connection.inputStream.use { input ->
            val output = ByteArrayOutputStream()
            val buffer = ByteArray(16 * 1024)
            while (true) {
                currentCoroutineContext().ensureActive()
                val size = input.read(buffer)
                if (size < 0) break
                if (output.size().toLong() + size > maxBytes) throw IOException("HTTP response exceeds its size limit")
                output.write(buffer, 0, size)
            }
            if (timedOut.get()) throw SocketTimeoutException("HTTP response timed out")
            output.toByteArray()
        }
    } catch (error: IOException) {
        if (timedOut.get()) throw SocketTimeoutException("HTTP response timed out").apply { initCause(error) }
        throw error
    } finally { deadline.cancel(false); connection.disconnect() }
}
