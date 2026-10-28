package ru.bespalov.videodownloader

import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTextInput
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.uiautomator.Configurator
import androidx.test.uiautomator.UiDevice
import com.yausername.youtubedl_android.YoutubeDL
import com.yausername.youtubedl_android.YoutubeDLRequest
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import ru.bespalov.video.core.DownloadStatus
import ru.bespalov.video.core.DownloadTask
import ru.bespalov.video.core.EntryState
import ru.bespalov.video.core.Settings
import ru.bespalov.video.core.RssParser
import ru.bespalov.video.core.Subscription
import ru.bespalov.video.core.SubscriptionEntry
import ru.bespalov.videodownloader.download.DownloadService
import ru.bespalov.videodownloader.download.MediaPublisher
import ru.bespalov.videodownloader.engine.NativeMedia
import ru.bespalov.videodownloader.engine.PrivatePaths
import java.io.File
import java.security.MessageDigest
import java.nio.ByteBuffer
import java.nio.file.Files
import java.util.UUID

/** These tests execute the packaged Android native binaries, not JVM substitutes. */
@RunWith(AndroidJUnit4::class)
class AndroidPortInstrumentedTest {
    @get:Rule val compose = createEmptyComposeRule()

    private val context: Context get() = ApplicationProvider.getApplicationContext()
    private val graph: AppGraph get() = (context.applicationContext as DownloaderApplication).graph

    private fun initialize() = runBlocking(Dispatchers.IO) {
        withTimeout(90_000) { graph.ready.await() }
        assertTrue("Application initialization failed: ${graph.startupError.value}", graph.runtimeReady.value)
        assertFalse("An isolated test install must have a writable repository: ${graph.repository.error}", graph.repository.readOnly)
    }

    private fun sample(): ByteArray = InstrumentationRegistry.getInstrumentation().context.assets
        .open("offline-sample.mp4").use { it.readBytes() }

    private fun slowSample(): ByteArray {
        // A standards-compliant free box adds transfer time without changing the media.
        val padding = ByteBuffer.allocate(512 * 1024).apply {
            putInt(capacity())
            put("free".toByteArray(Charsets.US_ASCII))
        }.array()
        return sample() + padding
    }

    @Test fun sharedTextAndActualAddStayQueuedWithoutStart() {
        initialize()
        LoopbackMediaServer(sample()).use { origin ->
            val token = UUID.randomUUID().toString()
            val urls = listOf(
                "${origin.baseUrl}/share-$token.mp4?Token=AbC",
                "${origin.baseUrl}/share-$token.mp4?Token=abc",
                "${origin.baseUrl}/manual-$token.mp4",
            )
            val historyBefore = graph.repository.state.value.history
            val intent = Intent(context, MainActivity::class.java)
                .setAction(Intent.ACTION_SEND)
                .setType("text/plain")
                .putExtra(Intent.EXTRA_TEXT, urls.take(2).joinToString("\n"))
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            try {
                ActivityScenario.launch<MainActivity>(intent).use {
                    compose.waitUntil(20_000) { urls.take(2).all { url -> graph.repository.state.value.queue.any { row -> row.url == url } } }
                    compose.onNodeWithTag("queueTab").assertIsDisplayed()
                    compose.onNodeWithTag("urlInput").performTextInput(urls.last())
                    compose.onNodeWithTag("addUrls").performClick()
                    compose.waitUntil(20_000) { graph.repository.state.value.queue.any { row -> row.url == urls.last() } }
                    val rows = graph.repository.state.value.queue.filter { it.url in urls }
                    assertEquals("Share and Add must preserve case-sensitive URL identity", 3, rows.size)
                    assertTrue("Adding links must not start downloading", rows.all { it.status == DownloadStatus.QUEUED && it.outputUris.isEmpty() })
                    assertEquals("Metadata requests must not create successful downloads", historyBefore, graph.repository.state.value.history)
                    compose.onNodeWithTag("startDownloads").assertIsDisplayed()
                    compose.onNodeWithTag("subscriptionsTab").assertIsDisplayed()
                    compose.onNodeWithTag("settingsTab").assertIsDisplayed()
                }
            } finally {
                runBlocking(Dispatchers.IO) {
                    graph.repository.update { state -> state.copy(queue = state.queue.filterNot { it.url in urls }) }
                }
            }
        }
    }

    @Test fun packagedPythonYtDlpFfmpegAndFfprobeActuallyExecute() = runBlocking(Dispatchers.IO) {
        initialize()
        val request = YoutubeDLRequest(emptyList<String>()).apply { addOption("--version") }
        val response = YoutubeDL.execute(request, "instrumentation-version-${UUID.randomUUID()}")
        assertEquals("The packaged yt-dlp process must exit successfully", 0, response.exitCode)
        assertEquals("A fresh install must execute the pinned extractor, not the wrapper's old default", BuildConfig.YTDLP_VERSION, response.out.trim())
        assertTrue("yt-dlp must report a real upstream version: ${response.out}", Regex("\\d{4}\\.\\d{2}\\.\\d{2}").containsMatchIn(response.out))
        val ffmpeg = NativeMedia.run(context, "ffmpeg", listOf("-version")).toString(Charsets.UTF_8)
        val ffprobe = NativeMedia.run(context, "ffprobe", listOf("-version")).toString(Charsets.UTF_8)
        assertTrue("The FFmpeg native executable must run", ffmpeg.contains("ffmpeg version", ignoreCase = true))
        assertTrue("The FFprobe native executable must run", ffprobe.contains("ffprobe version", ignoreCase = true))
    }

    @Test fun androidXmlProviderAcceptsRssAndRejectsExternalEntities() {
        val parsed = RssParser.parse("""<rss version="2.0"><channel><item><title>Название</title><link>https://example.org/watch?v=AbC</link></item></channel></rss>""")
        assertEquals("Название", parsed.single().title)
        assertEquals("https://example.org/watch?v=AbC", parsed.single().url)
        val unsafe = """<!DOCTYPE rss [<!ENTITY xxe SYSTEM "file:///data/local/tmp/never-read">]><rss><channel><item><title>&xxe;</title><link>https://example.org/video</link></item></channel></rss>"""
        assertTrue("The Android XML provider must never resolve external entities", runCatching { RssParser.parse(unsafe) }.isFailure)
    }

    @Test fun privatePathsAcceptAndroidDataAliasAndRejectInternalLinksAndTraversal() {
        val root = File(context.filesDir, "staging/path-test-${UUID.randomUUID()}")
        val directory = File(root, "target").apply { mkdirs() }
        val media = File(directory, "media.mp4").apply { writeText("test-owned path sentinel") }
        val link = File(root, "link")
        try {
            assertEquals("The actual Android context.filesDir spelling must be accepted", media,
                PrivatePaths.child(root, media))
            assertEquals("A native process may report the canonical Android data-directory spelling", media.canonicalFile,
                PrivatePaths.child(root, media.canonicalFile))
            Files.createSymbolicLink(link.toPath(), directory.toPath())
            assertTrue("A symlink below the owned root is forbidden even if its target stays inside it",
                runCatching { PrivatePaths.child(root, File(link, "media.mp4")) }.isFailure)
            assertTrue("A traversal below the owned root is forbidden even if it resolves inside it",
                runCatching { PrivatePaths.child(root, File(root, "target/../target/media.mp4")) }.isFailure)
            assertTrue("The trusted root cannot be used to access a sibling directory",
                runCatching { PrivatePaths.child(root, File(context.filesDir, "unowned-media.mp4")) }.isFailure)
            assertEquals("Rejection must preserve the known test target", "test-owned path sentinel", media.readText())
        } finally {
            Files.deleteIfExists(link.toPath())
            root.deleteRecursively()
        }
    }

    @Test fun realForegroundDownloadPublishesMediaBeforeHistoryAndPreservesPreviousCopy() {
        initialize()
        LoopbackMediaServer(sample()).use { origin ->
            val token = "android-test-${UUID.randomUUID()}"
            val url = "${origin.baseUrl}/$token.mp4"
            val source = Subscription(url = "${origin.baseUrl}/channel-$token", title = "Offline test source", shortName = token, initialized = true)
            val first = DownloadTask(url = url, subscriptionId = source.id, subscriptionName = token)
            val ids = setOf(first.id)
            val settingsBefore = graph.repository.state.value.settings
            val historyBefore = graph.repository.state.value.history
            var createdUris = emptyList<String>()
            try {
                runBlocking(Dispatchers.IO) {
                    graph.repository.update { state -> state.copy(
                        settings = testSettings(settingsBefore, token),
                        queue = state.queue + first,
                        subscriptions = state.subscriptions + source,
                        items = state.items + SubscriptionEntry(source.id, url, title = token, state = EntryState.QUEUED),
                    ) }
                }
                ActivityScenario.launch(MainActivity::class.java).use { activity ->
                    activity.onActivity { DownloadService.start(it, listOf(first.id)) }
                    val completed = awaitTerminal(first.id)
                    createdUris = completed.outputUris
                    assertEquals("A verified media download must complete: ${completed.rawError}", DownloadStatus.COMPLETED, completed.status)
                    assertTrue("Success requires a published owned media URI", completed.outputUris.isNotEmpty())
                    val originalHash = hash(completed.outputUris.first())
                    val afterFirst = graph.repository.state.value
                    assertTrue("The verified media must enter history", afterFirst.history.any { it.url == url && it.outputUris == completed.outputUris })
                    assertEquals("Only actual success processes a subscription item", EntryState.PROCESSED,
                        afterFirst.items.single { it.subscriptionId == source.id && it.url == url }.state)
                    assertTrue("Existing history must remain intact", afterFirst.history.containsAll(historyBefore))
                    awaitServiceIdle()

                    // The UI approves a repeat of the existing queue row. Wait for the
                    // asynchronous Start command so the old COMPLETED state cannot pass.
                    activity.onActivity { DownloadService.start(it, listOf(first.id)) }
                    runBlocking(Dispatchers.IO) {
                        withTimeout(20_000) {
                            while (graph.repository.state.value.queue.single { it.id == first.id }.outputUris == completed.outputUris) delay(50)
                        }
                    }
                    val repeated = awaitTerminal(first.id)
                    createdUris = createdUris + repeated.outputUris
                    assertEquals("The approved second copy must also complete: ${repeated.rawError}", DownloadStatus.COMPLETED, repeated.status)
                    assertTrue("A repeat must create new files rather than overwrite the first copy", repeated.outputUris.none { it in completed.outputUris })
                    assertEquals("The original published media must remain byte-for-byte unchanged", originalHash, hash(completed.outputUris.first()))
                    assertTrue("The second published copy must be readable", hash(repeated.outputUris.first()).isNotEmpty())
                    awaitServiceIdle()
                }
            } finally {
                cleanup(ids, setOf(source.id), setOf(url), createdUris, settingsBefore)
            }
        }
    }

    @Test fun failedNativeRequestCannotPublishOrProcessSubscription() {
        initialize()
        LoopbackMediaServer(sample()).use { origin ->
            val token = "android-failed-${UUID.randomUUID()}"
            val url = "${origin.baseUrl}/forbidden-$token.mp4"
            val source = Subscription(url = "${origin.baseUrl}/channel-$token", title = token, shortName = token, initialized = true)
            val task = DownloadTask(url = url, subscriptionId = source.id, subscriptionName = token)
            val originalSettings = graph.repository.state.value.settings
            val sentinel = File(context.filesDir, "$token-sentinel.txt").apply { writeText("preserve unrelated data") }
            try {
                runBlocking(Dispatchers.IO) {
                    graph.repository.update { state -> state.copy(
                        settings = testSettings(originalSettings, token),
                        queue = state.queue + task,
                        subscriptions = state.subscriptions + source,
                        items = state.items + SubscriptionEntry(source.id, url, state = EntryState.QUEUED),
                    ) }
                }
                ActivityScenario.launch(MainActivity::class.java).use { activity ->
                    activity.onActivity { DownloadService.start(it, listOf(task.id)) }
                    val terminal = awaitTerminal(task.id)
                    assertEquals("A 403 response must be a failed download", DownloadStatus.FAILED, terminal.status)
                    assertTrue("Failed extraction must not publish files", terminal.outputUris.isEmpty())
                    val snapshot = graph.repository.state.value
                    assertFalse("Failure must not enter successful history", snapshot.history.any { it.url == url })
                    assertEquals("Failure must not process the subscription item", EntryState.QUEUED,
                        snapshot.items.single { it.subscriptionId == source.id && it.url == url }.state)
                    assertEquals("Cleanup must not touch unrelated app files", "preserve unrelated data", sentinel.readText())
                    awaitServiceIdle()
                }
            } finally {
                cleanup(setOf(task.id), setOf(source.id), setOf(url), emptyList(), originalSettings)
                sentinel.delete()
            }
        }
    }

    @Test fun foregroundDownloadCompletesAfterPressingHome() {
        initialize()
        // An ISO BMFF free box keeps the fixture valid while making the real HTTP transfer
        // long enough to observe background progress. No external network is involved.
        LoopbackMediaServer(slowSample(), chunkBytes = 8192, chunkDelayMillis = 100).use { origin ->
            val token = "android-background-${UUID.randomUUID()}"
            val task = DownloadTask(url = "${origin.baseUrl}/$token.mp4")
            val originalSettings = graph.repository.state.value.settings
            var published = emptyList<String>()
            try {
                runBlocking(Dispatchers.IO) {
                    graph.repository.update { state -> state.copy(
                        settings = testSettings(originalSettings, token),
                        queue = state.queue + task,
                    ) }
                }
                ActivityScenario.launch(MainActivity::class.java).use { activity ->
                    activity.onActivity { DownloadService.start(it, listOf(task.id)) }
                    awaitTransferProgress(task.id)
                    assertEquals(DownloadStatus.RUNNING, graph.repository.state.value.queue.single { it.id == task.id }.status)
                    val device = UiDevice.getInstance(InstrumentationRegistry.getInstrumentation())
                    val configurator = Configurator.getInstance()
                    val idleTimeout = configurator.waitForIdleTimeout
                    try {
                        // Progress changes continuously. Do not wait ten seconds for the
                        // download screen to become idle before sending the actual Home key.
                        configurator.waitForIdleTimeout = 500
                        assertTrue("Home key must move the application to the background", device.pressHome())
                        device.waitForIdle(500)
                        assertTrue("The foreground download service must survive Home", DownloadService.isRunning.value)
                        assertTrue("The launcher must be visible while downloading", device.currentPackageName != context.packageName)
                    } finally {
                        configurator.waitForIdleTimeout = idleTimeout
                    }
                    val completed = awaitTerminal(task.id)
                    published = completed.outputUris
                    assertEquals("Background transfer must publish successfully: ${completed.rawError}", DownloadStatus.COMPLETED, completed.status)
                    assertTrue("The published background result must be readable", hash(published.first()).isNotEmpty())
                    assertTrue("Success must be saved while the activity is backgrounded", graph.repository.state.value.history.any { it.url == task.url && it.outputUris == published })
                    awaitServiceIdle()
                }
            } finally {
                cleanup(setOf(task.id), emptySet(), setOf(task.url), published, originalSettings)
            }
        }
    }

    @Test fun cancelledNativeTransferCannotPublishAndRemovalKeepsUnrelatedFiles() {
        initialize()
        LoopbackMediaServer(slowSample(), chunkBytes = 8192, chunkDelayMillis = 100).use { origin ->
            val token = "android-cancel-${UUID.randomUUID()}"
            val url = "${origin.baseUrl}/$token.mp4"
            val source = Subscription(url = "${origin.baseUrl}/channel-$token", title = token, shortName = token, initialized = true)
            val task = DownloadTask(url = url, subscriptionId = source.id, subscriptionName = token)
            val originalSettings = graph.repository.state.value.settings
            val sentinel = File(context.filesDir, "staging/$token-unrelated.txt").apply {
                parentFile!!.mkdirs()
                writeText("preserve unrelated staging data")
            }
            try {
                runBlocking(Dispatchers.IO) {
                    graph.repository.update { state -> state.copy(
                        settings = testSettings(originalSettings, token),
                        queue = state.queue + task,
                        subscriptions = state.subscriptions + source,
                        items = state.items + SubscriptionEntry(source.id, url, state = EntryState.QUEUED),
                    ) }
                }
                ActivityScenario.launch(MainActivity::class.java).use { activity ->
                    activity.onActivity { DownloadService.start(it, listOf(task.id)) }
                    awaitTransferProgress(task.id)
                    activity.onActivity { DownloadService.cancelTask(it, task.id) }
                    val cancelled = awaitTerminal(task.id)
                    awaitServiceIdle()
                    assertEquals("Explicit cancellation must interrupt the native transfer", DownloadStatus.INTERRUPTED, cancelled.status)
                    assertTrue("Cancellation cannot publish media", cancelled.outputUris.isEmpty())
                    val afterCancel = graph.repository.state.value
                    assertFalse("Cancellation cannot enter successful history", afterCancel.history.any { it.url == url })
                    assertEquals("Cancellation cannot process a subscription item", EntryState.QUEUED,
                        afterCancel.items.single { it.subscriptionId == source.id && it.url == url }.state)
                    assertTrue("The partial download must retain its private resume directory", cancelled.stageDirectory.isNotBlank())
                    val stage = File(cancelled.stageDirectory)
                    assertTrue("The cancelled transfer's staging directory must remain resumable", stage.isDirectory)

                    activity.onActivity { DownloadService.removeTask(it, task.id, deleteFiles = true) }
                    runBlocking(Dispatchers.IO) {
                        withTimeout(20_000) {
                            while (graph.repository.state.value.queue.any { it.id == task.id }) delay(50)
                        }
                    }
                    awaitServiceIdle()
                    assertFalse("Removal must clean the task's owned partial directory", stage.exists())
                    assertEquals("Removal must preserve unrelated staging files", "preserve unrelated staging data", sentinel.readText())
                    assertFalse("Removal cannot manufacture a successful history entry", graph.repository.state.value.history.any { it.url == url })
                }
            } finally {
                cleanup(setOf(task.id), setOf(source.id), setOf(url), emptyList(), originalSettings)
                sentinel.delete()
            }
        }
    }

    private fun testSettings(previous: Settings, filename: String): Settings = previous.copy(
        profile = "Universal", threads = 1, fragments = 1, quality = "Best", codec = "Auto", container = "Auto",
        filenameTemplate = "$filename.%(ext)s", outputTreeUri = "", cookiesFile = "", rateLimit = "Unlimited",
        audioOnly = false, archive = false, sponsor = false, writeSubs = false, autoSubs = false,
        embedSubs = false, embedThumbnail = false, embedMetadata = false, embedChapters = false,
    )

    private fun awaitTerminal(id: String): DownloadTask = runBlocking(Dispatchers.IO) {
        withTimeout(90_000) {
            while (true) {
                val row = graph.repository.state.value.queue.single { it.id == id }
                if (row.status in setOf(DownloadStatus.COMPLETED, DownloadStatus.FAILED, DownloadStatus.INTERRUPTED, DownloadStatus.CLEANUP_REQUIRED)) return@withTimeout row
                delay(100)
            }
            @Suppress("UNREACHABLE_CODE") error("Unreachable")
        }
    }

    private fun awaitTransferProgress(id: String) = runBlocking(Dispatchers.IO) {
        withTimeout(90_000) {
            while (true) {
                val task = graph.repository.state.value.queue.single { it.id == id }
                check(task.status !in setOf(DownloadStatus.FAILED, DownloadStatus.INTERRUPTED,
                    DownloadStatus.COMPLETED, DownloadStatus.CLEANUP_REQUIRED)) {
                    "Native transfer ended before the lifecycle test: ${task.status}: ${task.rawError}"
                }
                if (task.status == DownloadStatus.RUNNING &&
                    (DownloadService.progress.value[id]?.downloadedBytes ?: 0) > 0) break
                delay(50)
            }
        }
    }

    private fun awaitServiceIdle() = runBlocking(Dispatchers.IO) {
        withTimeout(20_000) { while (DownloadService.isRunning.value) delay(100) }
    }

    private fun hash(value: String): String = context.contentResolver.openInputStream(Uri.parse(value))!!.use { input ->
        val digest = MessageDigest.getInstance("SHA-256")
        val buffer = ByteArray(8192)
        while (true) { val count = input.read(buffer); if (count < 0) break; digest.update(buffer, 0, count) }
        digest.digest().joinToString("") { "%02x".format(it) }
    }

    private fun cleanup(ids: Set<String>, sourceIds: Set<String>, urls: Set<String>, uris: List<String>, settings: Settings) {
        if (DownloadService.isRunning.value) {
            DownloadService.stopAll(context)
            awaitServiceIdle()
        }
        runBlocking(Dispatchers.IO) {
            val snapshot = graph.repository.state.value
            val owned = uris + snapshot.queue.filter { it.id in ids }.flatMap { it.outputUris }
            assertTrue("Test-owned published files must be removable", MediaPublisher(context).deleteOwned(owned).isEmpty())
            graph.repository.update { state -> state.copy(
                settings = settings,
                queue = state.queue.filterNot { it.id in ids },
                subscriptions = state.subscriptions.filterNot { it.id in sourceIds },
                items = state.items.filterNot { it.subscriptionId in sourceIds },
                history = state.history.filterNot { it.url in urls },
            ) }
            val stageRoot = File(context.filesDir, "staging").canonicalFile.toPath()
            snapshot.queue.filter { it.id in ids && it.stageDirectory.isNotBlank() }.forEach { row ->
                val stage = File(row.stageDirectory)
                val path = stage.canonicalFile.toPath()
                // Test task IDs are random UUIDs created above; only their real run/task
                // directory may be cleaned, never the entire application's staging tree.
                if (path.startsWith(stageRoot) && path != stageRoot && path.fileName.toString() == row.id) stage.deleteRecursively()
            }
        }
    }
}
