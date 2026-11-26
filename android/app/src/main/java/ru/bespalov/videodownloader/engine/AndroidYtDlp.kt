package ru.bespalov.videodownloader.engine

import android.content.Context
import android.os.SystemClock
import com.yausername.ffmpeg.FFmpeg
import com.yausername.youtubedl_android.YoutubeDL
import com.yausername.youtubedl_android.YoutubeDLRequest
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.TimeoutCancellationException
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
import ru.bespalov.video.core.PornHubVideo
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
import java.nio.file.Files
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.UUID

/** Android-specific process bridge. Publication and successful history belong to the service. */
class AndroidYtDlp internal constructor(context: Context, private val runtimeUpdater: suspend (File) -> String) {
    constructor(context: Context) : this(context, { target -> RuntimeUpdater(target).update() })
    private val app = context.applicationContext
    private val initialization = Mutex()
    private val metadataWorker = Mutex()
    private val scanWorker = Mutex()
    private var initialized = false
    private val admission = Mutex()
    private var updating = false
    private val operations = ConcurrentHashMap<String, Operation>()
    private val processes = AndroidRuntimeProcess(app)
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
                for (name in listOf("vd_pornhub.py", "vd_pornhub_profiles.py")) {
                    val relative = "videodownloader/yt_dlp_plugins/extractor/$name"
                    val target = File(pluginRoot, relative)
                    target.parentFile!!.mkdirs()
                    app.assets.open(relative).use { input -> target.outputStream().use(input::copyTo) }
                }
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

    suspend fun findPornHubProfiles(watchUrl: String, cookies: File?): List<String> = withContext(Dispatchers.IO) {
        require(PornHubVideo.watchUrl(watchUrl) == watchUrl) { "Укажите ссылку на ролик Pornhub." }
        initialize()
        val arguments = listOf("--ignore-config", "--dump-single-json", "--skip-download", "--abort-on-error",
            "--no-color", "--no-progress", "--encoding", "utf-8", "--socket-timeout", "15",
            "--no-plugin-dirs", "--plugin-dirs", pluginRoot.path, "--use-extractors", "videodownloader:pornhub:profiles")
        try {
            val response = execute("profiles-${UUID.randomUUID()}", request(watchUrl, arguments, cookies), 30_000,
                outputLimit = 128 * 1024)
            PornHubVideo.parseProfiles(response.out, watchUrl)
        } catch (_: TimeoutCancellationException) {
            currentCoroutineContext().ensureActive()
            throw IOException("Pornhub не ответил за 30 секунд.")
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
        var aria2c = if (settings.preferAria2c) processes.aria2cExecutable() else null
        var invocation = 0
        var last = EngineProgress(title = task.title, transferredBytes = transferCounters.totalBytes)
        for ((qualityIndex, quality) in levels.withIndex()) {
            var attempt = 1
            while (attempt <= 4) {
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
                    Aria2Stage.prepare(stage, aria2c != null)
                    val arguments = YtDlpArguments.download(settings, stage, quality, aria2c, processes.aria2cCertificate)
                    executeDownload(requestId, task.url, arguments, cookie(settings.cookiesFile), aria2c != null) { text ->
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
                    if (aria2c != null) Aria2Stage.complete(stage)
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
                        .filterNot { it.name.endsWith(".part") || it.name.endsWith(".part.aria2") || it.name.contains(".part-Frag") ||
                            it.name.endsWith(".ytdl") || it.name.endsWith(".temp") }
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
                    if (aria2c != null && !retryBoundary.processingStarted &&
                        diagnostic.contains("aria2c exited with code ", ignoreCase = true)) {
                        aria2c = null
                        trace("aria2c_fallback", diagnostic, EngineTraceLevel.WARNING)
                        last = last.copy(stage = EngineStage.RETRYING, detail = "Пробуем встроенный загрузчик", speed = "", eta = "")
                        report(onProgress, last)
                        continue
                    }
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
                            attempt++
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

    internal fun hasAria2c(): Boolean = processes.aria2cExecutable() != null

    private suspend fun executeDownload(
        requestId: String, url: String, arguments: List<String>, cookies: File?, useAria2c: Boolean,
        line: (String) -> Unit,
    ) {
        // The external downloader writes its cookie jar before the transfer.
        // Give each invocation a private copy even if no user jar is connected.
        val temporary = if (useAria2c) File.createTempFile("aria2-cookies-", ".txt", app.cacheDir) else null
        try {
            temporary?.let { target ->
                if (cookies?.isFile == true) cookies.copyTo(target, overwrite = true)
                else target.writeText("# Netscape HTTP Cookie File\n")
            }
            execute(requestId, request(url, arguments, temporary ?: cookies), 6 * 60 * 60_000L,
                outputLimit = 4 * 1024 * 1024, line = line)
        } finally {
            temporary?.delete()
        }
    }

    internal suspend fun measureSpeed(
        urls: List<String>,
        settings: Settings,
        compareAria2c: Boolean,
        onProfile: (Int, SpeedDebugProfile) -> Unit,
        onResult: (SpeedDebugResult) -> Unit,
    ) = withContext(Dispatchers.IO) {
        initialize()
        val aria2c = if (compareAria2c) {
            val executable = checkNotNull(processes.aria2cExecutable()) { "В этой сборке Android нет aria2c." }
            val version = try {
                execute("aria2c-version-${UUID.randomUUID()}", 3_000L, 32 * 1024, null) { session ->
                    processes.executableBuilder(session, executable, listOf("--version"))
                }
            } catch (cancel: CancellationException) {
                currentCoroutineContext().ensureActive()
                throw EngineException("Не удалось проверить версию aria2c.", cancel)
            } catch (error: EngineException) {
                throw EngineException("Не удалось запустить aria2c: ${error.message}", error)
            }
            check(version.out.lineSequence().any { it.startsWith("aria2 version ") }) { "Версия aria2c не подтверждена." }
            executable
        } else null
        val profiles = if (compareAria2c) SpeedDebug.comparisonProfiles(settings) else SpeedDebug.profiles(settings)
        val matchedFormats = ConcurrentHashMap<String, String>()
        val root = Files.createTempDirectory(app.cacheDir.toPath(), "speed-debug-").toFile()
        try {
            SpeedDebug.measure(urls, profiles, root, SystemClock::elapsedRealtime, onProfile,
                onResult = { result -> onResult(if (compareAria2c) result.copy(eligible = matchedFormats.size) else result) },
                accept = { url, profile -> profile.downloader != SpeedDebugDownloader.ARIA2C || matchedFormats.containsKey(url) }) {
                url, stage, profile, counter ->
                val sampleCookies = cookie(settings.cookiesFile)?.takeIf(File::isFile)?.let { source ->
                    Files.copy(source.toPath(), stage.toPath().resolve("cookies.txt")).toFile()
                } ?: if (compareAria2c) File(stage, "cookies.txt").apply { writeText("# Netscape HTTP Cookie File\n") } else null
                val useAria2c = profile.downloader == SpeedDebugDownloader.ARIA2C
                val expected = if (useAria2c) {
                    matchedFormats[url] ?: throw EngineException("У встроенного загрузчика нет подходящего формата HTTPS для этой ссылки.")
                } else null
                val arguments = if (compareAria2c) {
                    YtDlpArguments.speedDebugComparison(stage, expected, aria2c.takeIf { useAria2c }, processes.aria2cCertificate,
                        profile.httpChunkSize)
                } else YtDlpArguments.speedDebug(settings, profile, stage)
                var actual: String? = null
                var aria2cConfirmed = false
                var highWaterMark = 0L
                var ariaError: String? = null
                try {
                    execute("speed-debug-${UUID.randomUUID()}", request(url, arguments,
                        sampleCookies), SpeedDebug.BUDGET_MILLIS, 4 * 1024 * 1024) { line ->
                        if (compareAria2c) SpeedDebug.pairedFormat(line)?.let {
                            actual = it
                            if (!useAria2c) matchedFormats[url] = it
                        }
                        if (useAria2c && ariaError == null && "[ERROR]" in line) {
                            ariaError = line.trim()
                        }
                        if (useAria2c && actual == expected) {
                            SpeedDebug.aria2cBytes(line)?.let {
                                aria2cConfirmed = true
                                highWaterMark = maxOf(highWaterMark, it)
                            }
                            if (SpeedDebug.aria2cCompleted(line)) aria2cConfirmed = true
                        }
                        val event = ProgressMarkers.parse(line) as? MarkerEvent.Progress
                        if (event?.downloadedBytesKnown == true) {
                            if (!useAria2c && (!compareAria2c || actual != null)) {
                                counter.observe(event.component, event.value.downloadedBytes)
                            } else if (useAria2c && aria2cConfirmed && actual == expected) {
                                highWaterMark = maxOf(highWaterMark, event.value.downloadedBytes)
                            }
                        }
                    }
                    if (useAria2c && (!aria2cConfirmed || actual != expected)) {
                        throw EngineException("Получение того же формата через aria2c не подтверждено.")
                    }
                    if (compareAria2c && !useAria2c && actual == null) {
                        throw EngineException("Не удалось зафиксировать формат HTTPS.")
                    }
                } catch (error: EngineException) {
                    val unsupported = compareAria2c && !useAria2c && actual == null &&
                        DownloadRetryBoundary().decide(error.message.orEmpty(), 1, true) == DownloadRetryDecision.LOWER_QUALITY
                    if (!unsupported) throw error
                } finally {
                    // Errors emitted by aria2c itself can precede a successful yt-dlp exit or a trial timeout.
                    ariaError?.let { throw EngineException(it) }
                    if (aria2cConfirmed && actual == expected) counter.observe("aria2c", highWaterMark)
                }
            }
        } finally {
            SpeedDebug.removeStage(root)
        }
    }

    /** Marks cancellation; completion is still awaited before the caller may clean its stage. */
    fun cancel(requestId: String): Boolean {
        val operation = operations[requestId] ?: return false
        operation.cancel()
        return true
    }

    suspend fun update(): String = withContext(Dispatchers.IO) {
        initialize()
        admission.withLock {
            check(!updating && operations.isEmpty()) { "Дождитесь окончания получения метаданных и других операций yt-dlp перед обновлением." }
            updating = true
        }
        try {
            runtimeUpdater(runtimeArchive())
        } finally {
            withContext(NonCancellable) { admission.withLock { updating = false } }
        }
        version()
    }

    private suspend fun execute(requestId: String, request: YoutubeDLRequest, timeout: Long,
                                outputLimit: Int = 32 * 1024 * 1024, line: ((String) -> Unit)? = null): NativeProcessResult =
        execute(requestId, timeout, outputLimit, line) { session -> processes.builder(request, session) }

    private suspend fun execute(requestId: String, timeout: Long, outputLimit: Int, line: ((String) -> Unit)?,
                                builder: (ProcessSession) -> ProcessBuilder): NativeProcessResult {
        currentCoroutineContext().ensureActive()
        val operation = Operation(processes.session())
        admission.withLock {
            check(!updating) { "Сейчас обновляется yt-dlp. Повторите действие после обновления." }
            check(operations.putIfAbsent(requestId, operation) == null) { "A request with this ID is already running" }
        }
        val completed = CompletableDeferred<Result<NativeProcessResult>>()
        executor.execute {
            completed.complete(runCatching {
                if (operation.cancelled.get()) throw CancellationException("Request cancelled")
                val response = BoundedProcessRunner.run(builder(operation.session), operation.session,
                    outputLimit, onLine = line)
                if (response.exitCode != 0) throw EngineException(response.err.trim().ifBlank { "yt-dlp exited with code ${response.exitCode}" })
                response
            })
        }
        try {
            val response = withTimeout(timeout) {
                while (!completed.isCompleted) {
                    delay(50)
                }
                completed.await().getOrThrow()
            }
            if (operation.cancelled.get()) throw CancellationException("Request cancelled")
            return response
        } catch (cancel: CancellationException) {
            operation.cancel()
            // Do not return cancellation while yt-dlp still owns files. The
            // runner joins its two byte-bounded readers and its native owner;
            // the authenticated launch group includes ordinary native children.
            withContext(NonCancellable) {
                while (!completed.isCompleted) { operation.cancel(); delay(50) }
                completed.await()
            }
            throw cancel
        } catch (error: Exception) {
            if (operation.cancelled.get()) throw CancellationException("Request cancelled", error)
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
    private class Operation(val session: ProcessSession) {
        val cancelled = AtomicBoolean()
        fun cancel() { cancelled.set(true); session.cancel() }
    }
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
