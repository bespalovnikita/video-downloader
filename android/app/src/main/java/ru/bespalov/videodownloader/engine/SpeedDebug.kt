package ru.bespalov.videodownloader.engine

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonPrimitive
import ru.bespalov.video.core.JournalSanitizer
import ru.bespalov.video.core.Settings
import ru.bespalov.video.core.Urls
import java.io.File
import java.nio.file.Files
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import kotlin.random.Random

enum class SpeedDebugDownloader { STANDARD, MATCHED_NATIVE, ARIA2C }

data class SpeedDebugProfile(
    val parallel: Int,
    val fragments: Int,
    val httpChunkSize: String? = null,
    val downloader: SpeedDebugDownloader = SpeedDebugDownloader.STANDARD,
) {
    val label: String
        get() = when (downloader) {
            SpeedDebugDownloader.STANDARD -> "Видео: $parallel · фрагменты: $fragments" +
                (httpChunkSize?.let { " · --http-chunk-size $it" } ?: "")
            SpeedDebugDownloader.MATCHED_NATIVE -> "yt-dlp · $parallel видео · прямой HTTPS"
            SpeedDebugDownloader.ARIA2C -> "aria2c · $parallel видео · до 4 соединений на файл"
        }
}

data class SpeedDebugResult(
    val profile: SpeedDebugProfile,
    val bytes: Long,
    val millis: Long,
    val started: Int,
    val measured: Int,
    val failures: Int,
    val error: String,
    val eligible: Int? = null,
) {
    val mebibytesPerSecond: Double get() = bytes * 1000.0 / (1024 * 1024 * millis)
}

internal object SpeedDebug {
    const val SOURCE_COUNT = 4
    const val BUDGET_MILLIS = 60_000L
    private const val COMPARISON_SLOT_MILLIS = 30_000L
    const val FORMAT_MARKER = "__VD_ARIA2_FORMAT__:"
    private val formatId = Regex("[A-Za-z0-9._-]{1,200}")
    private val readout = Regex("""^\[#([0-9a-fA-F]{6})\s+([0-9]+)B?/[0-9]+B?\([0-9]+%\)(?:\s|]).*""")
    private val completed = Regex("""^[0-9a-fA-F]{6,16}\|OK\s*\|.*""")

    val defaultUrls = """
        https://rt.pornhub.com/view_video.php?viewkey=6afda993ab932
        https://rt.pornhub.com/view_video.php?viewkey=662a6761bd68e
        https://rt.pornhub.com/view_video.php?viewkey=6911ce334ae78
        https://rt.pornhub.com/view_video.php?viewkey=6793cd8049d00
    """.trimIndent()

    fun sample(input: String, random: Random = Random.Default): List<String> {
        require(input.length <= 65_536) { "Список ссылок слишком длинный." }
        val urls = input.lineSequence().map(String::trim).filter(String::isNotEmpty).distinct().toList()
        require(urls.all(Urls::isHttp)) { "Укажите по одной ссылке HTTP(S) на строку." }
        require(urls.size >= SOURCE_COUNT) { "Укажите хотя бы четыре разных ссылки." }
        return urls.shuffled(random).take(SOURCE_COUNT)
    }

    fun profiles(settings: Settings): List<SpeedDebugProfile> {
        val parallel = settings.threads.coerceIn(1, SOURCE_COUNT)
        val fragments = settings.fragments.coerceIn(1, 8)
        val chunkSize = HTTP_CHUNK_SIZE.takeIf { settings.httpChunked }
        return listOf(
            SpeedDebugProfile(parallel, fragments, chunkSize),
            SpeedDebugProfile(parallel, if (fragments == 8) 2 else 8, chunkSize),
            SpeedDebugProfile(if (parallel == SOURCE_COUNT) 2 else SOURCE_COUNT, 8, chunkSize),
            SpeedDebugProfile(SOURCE_COUNT, 8, HTTP_CHUNK_SIZE.takeUnless { settings.httpChunked }),
        )
    }

    const val HTTP_CHUNK_SIZE = "4M"

    fun comparisonProfiles(settings: Settings = Settings()) = listOf(
        SpeedDebugProfile(SOURCE_COUNT, 1, HTTP_CHUNK_SIZE.takeIf { settings.httpChunked },
            downloader = SpeedDebugDownloader.MATCHED_NATIVE),
        SpeedDebugProfile(SOURCE_COUNT, 1, downloader = SpeedDebugDownloader.ARIA2C),
    )

    fun winner(results: List<SpeedDebugResult>, settings: Settings, comparing: Boolean): SpeedDebugProfile? {
        val expected = if (comparing) comparisonProfiles(settings) else profiles(settings)
        if (results.map { it.profile } != expected) return null
        if (!comparing) return results.filter { it.measured == SOURCE_COUNT && it.failures == 0 && it.bytes > 0 }
            .maxByOrNull { it.mebibytesPerSecond }?.profile

        val (native, aria2c) = results
        val eligible = native.eligible ?: return null
        if (eligible < 1 || aria2c.eligible != eligible ||
            results.any { it.measured != eligible || it.failures != 0 || it.bytes <= 0 }) return null
        return if (aria2c.mebibytesPerSecond > native.mebibytesPerSecond) aria2c.profile else native.profile
    }

    fun applyWinner(settings: Settings, winner: SpeedDebugProfile): Settings = when (winner.downloader) {
        SpeedDebugDownloader.STANDARD -> settings.copy(threads = winner.parallel, fragments = winner.fragments,
            httpChunked = winner.httpChunkSize != null)
        SpeedDebugDownloader.MATCHED_NATIVE -> settings.copy(preferAria2c = false)
        SpeedDebugDownloader.ARIA2C -> settings.copy(preferAria2c = true)
    }

    fun pairedFormat(line: String): String? {
        if (!line.startsWith(FORMAT_MARKER) || line.length > 1024) return null
        return runCatching {
            (Json.parseToJsonElement(line.removePrefix(FORMAT_MARKER)) as? JsonPrimitive)?.takeIf { it.isString }?.content
        }
            .getOrNull()?.takeIf(formatId::matches)
    }

    fun aria2cBytes(line: String): Long? = readout.matchEntire(line.trim())?.groupValues?.get(2)?.toLongOrNull()
    fun aria2cCompleted(line: String): Boolean = completed.matches(line.trim())

    suspend fun measure(
        urls: List<String>,
        profiles: List<SpeedDebugProfile>,
        root: File,
        now: () -> Long,
        onProfile: (Int, SpeedDebugProfile) -> Unit,
        onResult: (SpeedDebugResult) -> Unit,
        accept: (String, SpeedDebugProfile) -> Boolean = { _, _ -> true },
        download: suspend (String, File, SpeedDebugProfile, EngineTransferCounters) -> Unit,
    ) {
        val deadline = now() + BUDGET_MILLIS
        profiles.forEachIndexed { index, profile ->
            currentCoroutineContext().ensureActive()
            val available = (deadline - now()) / (profiles.size - index)
            val slot = if (profile.downloader == SpeedDebugDownloader.STANDARD) available
                else minOf(available, COMPARISON_SLOT_MILLIS)
            if (slot <= 0) return
            onProfile(index, profile)
            val stage = Files.createDirectory(root.toPath().resolve("profile-$index")).toFile()
            val started = AtomicInteger()
            val failures = AtomicInteger()
            val error = AtomicReference("")
            val counters = urls.map { EngineTransferCounters() }
            val profileDeadline = minOf(deadline, now() + slot)
            val waves = urls.indices.filter { accept(urls[it], profile) }.chunked(profile.parallel)
            var elapsed = 0L
            try {
                waves.forEachIndexed { wave, sources ->
                    val begin = now()
                    val window = (profileDeadline - begin) / (waves.size - wave)
                    if (window > 0) {
                        withTimeoutOrNull(window) {
                            coroutineScope {
                                for (source in sources) launch {
                                    started.incrementAndGet()
                                    try {
                                        val target = Files.createDirectory(stage.toPath().resolve("source-$source")).toFile()
                                        download(urls[source], target, profile, counters[source])
                                    } catch (cancel: CancellationException) {
                                        throw cancel
                                    } catch (failure: Exception) {
                                        failures.incrementAndGet()
                                        error.compareAndSet("", JournalSanitizer.redact(failure.message.orEmpty())
                                            .lineSequence().firstOrNull { it.isNotBlank() }.orEmpty().take(240))
                                    }
                                }
                            }
                        }
                        elapsed += (now() - begin).coerceIn(1L, window)
                    }
                }
                currentCoroutineContext().ensureActive()
                val counts = counters.map { it.totalBytes }
                onResult(SpeedDebugResult(profile, counts.sum(), elapsed.coerceAtLeast(1L), started.get(),
                    counts.count { it > 0 }, failures.get(), error.get()))
            } finally {
                removeStage(stage)
            }
        }
    }

    fun removeStage(stage: File) {
        // The private directory is created by this run. Walk without following links.
        Files.walk(stage.toPath()).use { paths ->
            paths.sorted(Comparator.reverseOrder()).forEach { Files.deleteIfExists(it) }
        }
    }
}
