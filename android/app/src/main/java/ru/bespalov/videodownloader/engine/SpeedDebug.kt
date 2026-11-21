package ru.bespalov.videodownloader.engine

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import ru.bespalov.video.core.JournalSanitizer
import ru.bespalov.video.core.Settings
import ru.bespalov.video.core.Urls
import java.io.File
import java.nio.file.Files
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import kotlin.random.Random

data class SpeedDebugProfile(val parallel: Int, val fragments: Int, val httpChunkSize: String? = null) {
    val label: String
        get() = "Видео: $parallel · фрагменты: $fragments" +
            (httpChunkSize?.let { " · --http-chunk-size $it" } ?: "")
}

data class SpeedDebugResult(
    val profile: SpeedDebugProfile,
    val bytes: Long,
    val millis: Long,
    val started: Int,
    val measured: Int,
    val failures: Int,
    val error: String,
) {
    val mebibytesPerSecond: Double get() = bytes * 1000.0 / (1024 * 1024 * millis)
}

internal object SpeedDebug {
    const val SOURCE_COUNT = 4
    const val BUDGET_MILLIS = 60_000L

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
        return listOf(
            SpeedDebugProfile(parallel, fragments),
            SpeedDebugProfile(parallel, if (fragments == 8) 2 else 8),
            SpeedDebugProfile(if (parallel == SOURCE_COUNT) 2 else SOURCE_COUNT, 8),
            SpeedDebugProfile(SOURCE_COUNT, 8, "4M"),
        )
    }

    suspend fun measure(
        urls: List<String>,
        profiles: List<SpeedDebugProfile>,
        root: File,
        now: () -> Long,
        onProfile: (Int, SpeedDebugProfile) -> Unit,
        onResult: (SpeedDebugResult) -> Unit,
        download: suspend (String, File, SpeedDebugProfile, EngineTransferCounters) -> Unit,
    ) {
        val deadline = now() + BUDGET_MILLIS
        profiles.forEachIndexed { index, profile ->
            currentCoroutineContext().ensureActive()
            val slot = (deadline - now()) / (profiles.size - index)
            if (slot <= 0) return
            onProfile(index, profile)
            val stage = Files.createDirectory(root.toPath().resolve("profile-$index")).toFile()
            val started = AtomicInteger()
            val failures = AtomicInteger()
            val error = AtomicReference("")
            val counters = urls.map { EngineTransferCounters() }
            val profileDeadline = minOf(deadline, now() + slot)
            val waves = urls.indices.chunked(profile.parallel)
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
