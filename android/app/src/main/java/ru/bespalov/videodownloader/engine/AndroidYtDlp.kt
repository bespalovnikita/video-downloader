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
import ru.bespalov.video.core.FullScan
import ru.bespalov.video.core.ScanMetadata
import ru.bespalov.video.core.SubscriptionScanCoordinator
import ru.bespalov.video.core.SubscriptionScanLimits
import ru.bespalov.video.core.Settings
import ru.bespalov.video.core.Urls
import ru.bespalov.videodownloader.BuildConfig
import ru.bespalov.videodownloader.R
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
import java.util.UUID

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
                RuntimeArchive.installIfOlder(runtimeArchive(), BuildConfig.YTDLP_VERSION, BuildConfig.YTDLP_SHA256) {
                    app.resources.openRawResource(R.raw.ytdlp)
                }
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

    private fun runtimeArchive() = File(app.noBackupFilesDir, "youtubedl-android/yt-dlp/yt-dlp")

    suspend fun version(): String = withContext(Dispatchers.IO) {
        initialize()
        execute("version-${UUID.randomUUID()}", YoutubeDLRequest(emptyList<String>()).apply { addOption("--version") }, 15_000).out.trim()
    }

    suspend fun metadata(requestId: String, url: String, cookies: File?,
                         onDiagnostic: (PreviewDiagnostic) -> Unit = {},
                         onUpdate: (EngineMetadata) -> Unit = {}): EngineMetadata =
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
                    galleries.fetch(url, json, onUpdate, onDiagnostic)
                }
            }
        }

    suspend fun scan(requestId: String, url: String, feedUrl: String? = null, cookies: File?,
                     initialized: Boolean = false, knownUrls: Set<String> = emptySet(), forceFull: Boolean = false,
                     onProgress: (String) -> Unit = {}): FullScan =
        withContext(Dispatchers.IO) {
            require(Urls.isHttp(url)) { "Expected an HTTP(S) source URL" }
            scanWorker.withLock {
                initialize()
                val limits = SubscriptionScanLimits()
                SubscriptionScanCoordinator(limits).scan(url, feedUrl, initialized, knownUrls, forceFull,
                    enumerate = { scope, timeoutMillis ->
                        val arguments = SubscriptionScanArguments.build(url, pluginRoot.path, scope, limits)
                        // execute joins native cancellation before returning. A timed-out
                        // probe therefore releases its request ID before the full fallback.
                        val response = execute(requestId, request(url, arguments, cookies), timeoutMillis,
                            outputLimit = limits.maxMetadataBytes)
                        ScanMetadata(response.out, response.err)
                    },
                    readFeed = { address, timeoutMillis ->
                        httpBytes(address, limits.maxFeedBytes, timeoutMillis.toInt()).toString(Charsets.UTF_8)
                    }, onProgress = onProgress)
            }
        }

    suspend fun download(requestId: String, task: DownloadTask, settings: Settings, stage: File,
                         transferCounters: EngineTransferCounters = EngineTransferCounters(),
                         onTrace: (EngineTrace) -> Unit = {},
                         onProgress: (EngineProgress) -> Unit = {}): EngineDownloadResult = withContext(Dispatchers.IO) {
        require(Urls.isHttp(task.url)) { "Expected an HTTP(S) URL" }
        initialize()
        validateStage(stage)
        stage.mkdirs()
        val levels = YtDlpArguments.qualityLevels(settings)
        var invocation = 0
        var last = EngineProgress(title = task.title, transferredBytes = transferCounters.totalBytes)
        for ((qualityIndex, quality) in levels.withIndex()) {
            for (attempt in 1..4) {
                currentCoroutineContext().ensureActive()
                invocation++
                val previousShown = last.percent
                // New native invocations expose their real reset. Title/phase events below
                // instead copy the complete snapshot and cannot accidentally reset a sample.
                last = EngineProgress(title = last.title, transferredBytes = transferCounters.totalBytes,
                    attempt = attempt, quality = quality, invocation = invocation)
                val progressPlan = DownloadProgressPlan()
                val retryBoundary = DownloadRetryBoundary()
                val finalPaths = linkedSetOf<String>()
                val eventsGate = Any()
                fun trace(event: String, message: String = "", level: EngineTraceLevel = EngineTraceLevel.INFORMATION,
                          category: String = "download", component: String = last.component) {
                    report(onTrace, EngineTrace(category, event, message, level, attempt, invocation, quality, component))
                }
                report(onProgress, last)
                trace("attempt_start", "previous_shown_pct=$previousShown transferred=${last.transferredBytes}")
                try {
                    execute(requestId, request(task.url, YtDlpArguments.download(settings, stage, quality), cookie(settings.cookiesFile)),
                        6 * 60 * 60_000L, outputLimit = 4 * 1024 * 1024) { text ->
                        for (line in text.lineSequence()) synchronized(eventsGate) {
                            val marker = ProgressMarkers.parse(line)
                            val wasProcessing = retryBoundary.processingStarted
                            retryBoundary.observe(line, marker)
                            val level = when {
                                line.trimStart().startsWith("ERROR:", ignoreCase = true) -> EngineTraceLevel.ERROR
                                line.trimStart().startsWith("WARNING:", ignoreCase = true) -> EngineTraceLevel.WARNING
                                else -> EngineTraceLevel.VERBOSE
                            }
                            // Detailed keeps actual child markers as well as typed progress.
                            // The observer sanitises all text before retention or export.
                            trace("output", line, level, "yt-dlp", (marker as? MarkerEvent.Progress)?.component ?: last.component)
                            if (!wasProcessing && retryBoundary.processingStarted) {
                                last = last.copy(stage = EngineStage.PROCESSING, detail = "Обработка", speed = "", eta = "")
                                report(onProgress, last)
                                trace("processing_started")
                            }
                            when (marker) {
                                is MarkerEvent.Title -> {
                                    last = last.copy(title = marker.value.takeIf(String::isNotBlank) ?: last.title)
                                    report(onProgress, last)
                                    trace("title", marker.value)
                                }
                                is MarkerEvent.Media -> {
                                    finalPaths += marker.path
                                    trace("media_reported")
                                }
                                is MarkerEvent.Plan -> {
                                    last = progressPlan.begin(marker, last)
                                    report(onProgress, last)
                                    trace("download_plan", "components=${marker.formatIds.size} media_index=${marker.mediaIndex} media_count=${marker.mediaCount}")
                                }
                                is MarkerEvent.Progress -> {
                                    val previous = last
                                    if (marker.downloadedBytesKnown) {
                                        val observation = transferCounters.observe(marker.component, marker.value.downloadedBytes)
                                        if (observation.newlyLimited)
                                            trace("transfer_tracking_limited", "The bounded stream ledger evicted an older baseline; byte totals for revisited older streams may be estimates.", EngineTraceLevel.WARNING)
                                        if (observation.reset)
                                            trace("counter_reset", "downloaded=${marker.value.downloadedBytes}", EngineTraceLevel.PROGRESS,
                                                component = marker.component)
                                    }
                                    val selectedComponent = progressPlan.accepts(marker)
                                    last = progressPlan.observe(marker, last).copy(transferredBytes = transferCounters.totalBytes)
                                    if (!marker.downloadedBytesKnown && previous.component == marker.component)
                                        last = last.copy(downloadedBytes = previous.downloadedBytes)
                                    report(onProgress, last)
                                    trace(if (selectedComponent) "progress" else "auxiliary_progress",
                                        "component_pct=${marker.rawPercent} shown_pct=${last.percent} downloaded=${last.downloadedBytes} total=${last.totalBytes} transferred=${last.transferredBytes}",
                                        EngineTraceLevel.PROGRESS, component = marker.component)
                                }
                                null -> Unit
                            }
                        }
                    }
                    // From here even a verify/manifest failure is final processing failure.
                    // Restarting the entire native request cannot repair it.
                    retryBoundary.nativeFinished()
                    currentCoroutineContext().ensureActive()
                    last = last.copy(stage = EngineStage.VERIFYING, detail = "Проверка файла", speed = "", eta = "")
                    report(onProgress, last)
                    trace("verifying")
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
                    trace("verified", "files=${files.size} transferred=${transferCounters.totalBytes}")
                    return@withContext EngineDownloadResult(primary, files, transferCounters.totalBytes)
                } catch (cancel: CancellationException) { throw cancel }
                catch (error: Exception) {
                    val diagnostic = error.message.orEmpty()
                    when (retryBoundary.decide(diagnostic, attempt, qualityIndex < levels.lastIndex)) {
                        DownloadRetryDecision.LOWER_QUALITY -> {
                            trace("quality_fallback", diagnostic, EngineTraceLevel.WARNING)
                            last = last.copy(stage = EngineStage.RETRYING, detail = "Пробуем другое качество", speed = "", eta = "")
                            report(onProgress, last)
                            break
                        }
                        DownloadRetryDecision.RETRY -> {
                            val delayMillis = 5_000L shl (attempt - 1)
                            trace("retry_scheduled", "next_attempt=${attempt + 1} delay_ms=$delayMillis\n$diagnostic", EngineTraceLevel.WARNING)
                            last = last.copy(stage = EngineStage.RETRYING,
                                detail = "Повтор ${attempt + 1}/4 через ${delayMillis / 1000} с", speed = "", eta = "")
                            report(onProgress, last)
                            delay(delayMillis)
                        }
                        DownloadRetryDecision.FAIL -> {
                            val processing = retryBoundary.processingStarted || diagnostic.lineSequence().any(ProgressMarkers::isPostprocessing)
                            trace(if (processing) "processing_failed" else "download_failed", diagnostic, EngineTraceLevel.ERROR)
                            throw error
                        }
                    }
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
        } finally {
            withContext(NonCancellable) { admission.withLock { updating = false } }
        }
        version()
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

internal suspend fun httpBytes(address: String, maxBytes: Int, timeoutMs: Int,
                              onTransfer: (status: Int, bytes: Int) -> Unit = { _, _ -> }): ByteArray = withContext(Dispatchers.IO) {
    require(Urls.isHttp(address)) { "Expected an HTTP(S) address" }
    require(maxBytes > 0 && timeoutMs > 0)
    val connection = URL(address).openConnection() as HttpURLConnection
    connection.connectTimeout = timeoutMs
    connection.readTimeout = timeoutMs
    connection.setRequestProperty("User-Agent", "Mozilla/5.0 VideoDownloader-Android")
    // A per-read timeout alone lets a server keep a worker forever by sending
    // one byte before every timeout. Bound headers and the complete body too.
    val timedOut = AtomicBoolean(false)
    var status = 0
    var transferred = 0
    val deadline = httpDeadlines.schedule({ timedOut.set(true); connection.disconnect() }, timeoutMs.toLong(), TimeUnit.MILLISECONDS)
    try {
        connection.connect()
        status = connection.responseCode
        if (status !in 200..299) throw IOException("HTTP Error $status: ${connection.responseMessage}")
        if (connection.contentLengthLong > maxBytes) throw IOException("HTTP response exceeds its size limit")
        connection.inputStream.use { input ->
            val output = ByteArrayOutputStream()
            val buffer = ByteArray(16 * 1024)
            while (true) {
                currentCoroutineContext().ensureActive()
                val size = input.read(buffer)
                if (size < 0) break
                transferred += size
                if (output.size().toLong() + size > maxBytes) throw IOException("HTTP response exceeds its size limit")
                output.write(buffer, 0, size)
            }
            if (timedOut.get()) throw SocketTimeoutException("HTTP response timed out")
            output.toByteArray()
        }
    } catch (error: IOException) {
        if (timedOut.get()) throw SocketTimeoutException("HTTP response timed out").apply { initCause(error) }
        throw error
    } finally {
        runCatching { onTransfer(status, transferred) }
        deadline.cancel(false); connection.disconnect()
    }
}
