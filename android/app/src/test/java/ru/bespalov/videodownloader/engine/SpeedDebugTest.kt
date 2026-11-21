package ru.bespalov.videodownloader.engine

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import ru.bespalov.video.core.AppState
import ru.bespalov.video.core.DownloadTask
import ru.bespalov.video.core.JsonStateRepository
import ru.bespalov.video.core.Settings
import java.io.File
import kotlin.random.Random

@OptIn(ExperimentalCoroutinesApi::class)
class SpeedDebugTest {
    @get:Rule val temp = TemporaryFolder()

    private val urls = (1..6).map { "https://example.test/video/$it" }

    @Test fun timedWavesReuseSampleAndCleanUpWithinTheBudget() = runTest {
        val sample = SpeedDebug.sample((urls + urls[0]).joinToString("\n"), Random(42))
        assertEquals(4, sample.size)
        assertEquals(4, sample.distinct().size)
        val profiles = SpeedDebug.profiles(Settings())
        val results = mutableListOf<SpeedDebugResult>()
        val attempted = mutableMapOf<SpeedDebugProfile, MutableList<String>>()
        val root = temp.newFolder()

        SpeedDebug.measure(sample, profiles, root, { testScheduler.currentTime }, { _, _ -> }, results::add) {
            url, stage, profile, counter ->
            attempted.getOrPut(profile) { mutableListOf() } += url
            val partial = File(stage, "sample.part").apply { writeText("unfinished") }
            counter.observe("video", 1024)
            try { awaitCancellation() } finally { assertTrue(partial.exists()) }
        }

        assertEquals(SpeedDebug.BUDGET_MILLIS, testScheduler.currentTime)
        assertEquals(profiles, results.map { it.profile })
        results.forEach { result ->
            assertEquals(sample, attempted[result.profile])
            assertEquals(4 * 1024L, result.bytes)
            assertEquals(4, result.measured)
            assertEquals(0, result.failures)
        }
        assertEquals(emptyList<String>(), root.list().orEmpty().toList())
    }

    @Test fun cancellingCleansPartFilesWithoutReportingTheIncompleteProfile() = runTest {
        val root = temp.newFolder()
        val results = mutableListOf<SpeedDebugResult>()
        val job = launch {
            SpeedDebug.measure(urls.take(4), SpeedDebug.profiles(Settings()), root,
                { testScheduler.currentTime }, { _, _ -> }, results::add) { _, stage, _, _ ->
                File(stage, "sample.part").writeText("unfinished")
                awaitCancellation()
            }
        }
        runCurrent()
        assertFalse(root.list().isNullOrEmpty())
        job.cancelAndJoin()
        assertTrue(results.isEmpty())
        assertTrue(root.list().isNullOrEmpty())
    }

    @Test fun speedArgumentsKeepFormatAndExcludeOptionalProcessing() {
        val settings = Settings(quality = "720", codec = "H264", rateLimit = "1M", sponsor = true, writeSubs = true)
        val arguments = YtDlpArguments.speedDebug(settings, SpeedDebugProfile(4, 8, "4M"), temp.newFolder())
        fun option(name: String) = arguments[arguments.indexOf(name) + 1]

        assertEquals("8", option("-N"))
        assertEquals("4M", option("--http-chunk-size"))
        assertTrue(option("-f").contains("[height<=720][vcodec^=avc1]"))
        assertTrue(arguments.containsAll(listOf("--ignore-config", "--no-continue", "--no-playlist")))
        assertFalse(arguments.any { it in setOf("--limit-rate", "--sponsorblock-remove", "--write-subs", "--embed-metadata") })
    }

    @Test fun comparisonDoesNotGiveAria2cUnusedNativeTime() = runTest {
        val results = mutableListOf<SpeedDebugResult>()
        SpeedDebug.measure(urls.take(4), SpeedDebug.comparisonProfiles(), temp.newFolder(), { testScheduler.currentTime },
            { _, _ -> }, results::add, accept = { url, profile ->
                profile.downloader != SpeedDebugDownloader.ARIA2C || url != urls[0]
            }) { _, _, profile, counter ->
            counter.observe("https", 2048)
            if (profile.downloader == SpeedDebugDownloader.ARIA2C) awaitCancellation() else delay(100)
        }
        assertEquals(listOf(100L, 30_000L), results.map { it.millis })
        assertEquals(listOf(4, 3), results.map { it.started })
        assertEquals(listOf(4, 3), results.map { it.measured })
    }

    @Test fun pairedComparisonSelectsOneIdenticalHttpsFormatAndReadsExactAria2Counters() {
        val stage = temp.newFolder()
        val native = YtDlpArguments.speedDebugComparison(stage, null, null, File(stage, "cert.pem"))
        val external = YtDlpArguments.speedDebugComparison(stage, "mp4-720", File(stage, "libaria2c.so"), File(stage, "cert.pem"))
        fun List<String>.option(name: String) = this[indexOf(name) + 1]

        assertEquals("native", native.option("--downloader"))
        assertEquals("best[protocol=https]", native.option("-f"))
        assertEquals("best[protocol=https][format_id=mp4-720]", external.option("-f"))
        assertEquals("http:${File(stage, "libaria2c.so").absolutePath}", external.option("--downloader"))
        assertTrue(external.option("--downloader-args").contains("-x4 -s4 --min-split-size=1M"))
        assertTrue(external.option("--downloader-args").contains("--human-readable=false --enable-color=false"))
        assertTrue(external.option("--downloader-args").contains("--summary-interval=1"))
        assertEquals("mp4-720", SpeedDebug.pairedFormat("__VD_ARIA2_FORMAT__:\"mp4-720\""))
        assertEquals(null, SpeedDebug.pairedFormat("__VD_ARIA2_FORMAT__:\"mp4][height>0\""))
        assertEquals(4_456_448L, SpeedDebug.aria2cBytes("[#2c2f01 4456448B/18874368B(23%) CN:8 DL:2176000B ETA:6s]"))
        assertEquals(null, SpeedDebug.aria2cBytes("[#2c2f01 4MiB/18MiB(23%) CN:8 DL:2MiB ETA:6s]"))
        assertTrue(SpeedDebug.aria2cCompleted("94878f|OK  |   2.5MiB/s|100|/tmp/./fixture.mp4.part"))
    }

    @Test fun standardWinnerPersistsThroughRepositoryReopenAndKeepsPinnedJobSettings() = runTest {
        val settings = Settings(quality = "720", rateLimit = "5M")
        val task = DownloadTask(url = urls[0], settingsSnapshot = settings)
        val stateFile = File(temp.newFolder(), "state.json")
        val original = AppState(settings = settings, queue = listOf(task))
        val initial = JsonStateRepository.JSON.encodeToString(AppState.serializer(), original)
        assertFalse("\"httpChunked\"" in initial || "\"preferAria2c\"" in initial)
        stateFile.writeText(initial)
        val repository = JsonStateRepository(stateFile)
        assertFalse(repository.readOnly)
        val results = SpeedDebug.profiles(settings).mapIndexed { index, profile ->
            SpeedDebugResult(profile, 1000L * (index + 1), 1000, 4, 4, 0, "")
        }
        assertEquals(null, SpeedDebug.winner(results.dropLast(1), settings, false))
        assertEquals(null, SpeedDebug.winner(results.map { it.copy(measured = 3) }, settings, false))
        assertEquals(null, SpeedDebug.winner(results.map { it.copy(failures = 1) }, settings, false))
        val winner = checkNotNull(SpeedDebug.winner(results, settings, false))
        repository.update { it.copy(settings = SpeedDebug.applyWinner(it.settings, winner)) }
        val saved = JsonStateRepository(stateFile).state.value
        assertEquals(settings.copy(threads = 4, fragments = 8, httpChunked = true), saved.settings)
        assertEquals(task, saved.queue.single())
        val nextProfiles = SpeedDebug.profiles(saved.settings)
        assertEquals("4M", nextProfiles.first().httpChunkSize)
        assertEquals(null, nextProfiles.last().httpChunkSize)
    }

    @Test fun pairedWinnerAppliesOnlyDownloaderPreferenceAndTiesChooseNative() {
        val settings = Settings(threads = 3, fragments = 5, httpChunked = true)
        val (native, aria) = SpeedDebug.comparisonProfiles(settings)
        val first = SpeedDebugResult(native, 1000, 1000, 4, 3, 0, "", 3)
        val second = SpeedDebugResult(aria, 1000, 1000, 3, 3, 0, "", 3)
        assertEquals(native, SpeedDebug.winner(listOf(first, second), settings, true))
        assertEquals(null, SpeedDebug.winner(listOf(first, second.copy(measured = 2)), settings, true))
        val winner = checkNotNull(SpeedDebug.winner(listOf(first, second.copy(bytes = 2000)), settings, true))
        assertEquals(settings.copy(preferAria2c = true), SpeedDebug.applyWinner(settings, winner))
        val stage = temp.newFolder()
        val args = YtDlpArguments.download(SpeedDebug.applyWinner(settings, winner), stage, "best")
        assertTrue(args.containsAll(listOf("--http-chunk-size", "4M")))
        assertFalse(args.contains("--downloader"))
        val ariaPath = File(stage, "libaria2c.so")
        val caFile = File(stage, "cert.pem")
        val available = YtDlpArguments.download(settings.copy(preferAria2c = true), stage, "best", ariaPath, caFile)
        assertTrue(available.containsAll(listOf("--downloader", "http:${ariaPath.absolutePath}")))
        assertTrue(available[available.indexOf("--downloader-args") + 1].contains("--show-console-readout=false"))
        val pairedExternal = YtDlpArguments.speedDebugComparison(stage, "mp4", ariaPath, caFile, aria.httpChunkSize)
        assertFalse(pairedExternal.contains("--http-chunk-size"))
    }

    @Test fun aria2ResumeOrFallbackClearsOnlyOwnedIncompleteArtifacts() {
        val stage = temp.newFolder()
        val nativePart = File(stage, "native.mp4.part").apply { writeText("native") }
        Aria2Stage.prepare(stage, false)
        assertTrue(nativePart.isFile)
        Aria2Stage.prepare(stage, true)
        val ready = File(stage, "complete.mp4").apply { writeText("finished") }
        val sparse = File(stage, "external.mp4.part").apply { writeText("sparse") }
        val control = File(stage, "external.mp4.part.aria2").apply { writeText("control") }
        val orphan = File(stage, "early.mp4.part").apply { writeText("sparse before control") }
        val fragmentState = File(stage, "native.mp4.ytdl").apply { writeText("native fragment state") }
        val fragment = File(stage, "native.mp4.part-Frag4").apply { writeText("fragment") }
        Aria2Stage.prepare(stage, false)
        assertFalse(listOf(sparse, control, orphan, fragmentState, fragment).any(File::exists))
        assertTrue(ready.isFile)
    }
}
