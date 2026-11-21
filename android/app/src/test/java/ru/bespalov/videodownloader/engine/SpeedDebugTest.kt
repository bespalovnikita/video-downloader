package ru.bespalov.videodownloader.engine

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
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
}
