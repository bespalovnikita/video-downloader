package ru.bespalov.videodownloader

import android.content.Context
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsOn
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.core.content.FileProvider
import androidx.lifecycle.ViewModelProvider
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import ru.bespalov.video.core.AppState
import ru.bespalov.video.core.EntryState
import ru.bespalov.video.core.Subscription
import ru.bespalov.video.core.SubscriptionEntry
import ru.bespalov.videodownloader.download.DownloadService
import ru.bespalov.videodownloader.engine.PreviewCachePolicy
import ru.bespalov.videodownloader.engine.PreviewDiagnostic
import ru.bespalov.videodownloader.engine.PreviewGallery
import java.io.File
import java.security.MessageDigest
import java.util.UUID

/** Real metadata extraction and image requests, independent of row selection and Start. */
@RunWith(AndroidJUnit4::class)
class DiscoveryPreviewInstrumentedTest {
    @get:Rule val compose = createEmptyComposeRule()
    private val context: Context get() = ApplicationProvider.getApplicationContext()
    private val graph: AppGraph get() = (context.applicationContext as DownloaderApplication).graph

    private fun await(timeout: Long = 90_000, predicate: () -> Boolean) = runBlocking(Dispatchers.IO) {
        withTimeout(timeout) { while (!predicate()) delay(50) }
    }
    private fun initialize() = runBlocking(Dispatchers.IO) {
        withTimeout(90_000) { graph.ready.await() }
        assertTrue(graph.startupError.value, graph.runtimeReady.value)
        assertFalse(graph.repository.error, graph.repository.readOnly)
        withTimeout(20_000) { while (DownloadService.isRunning.value || DownloadService.hasOwnedTasks) delay(50) }
    }
    private fun sample() = InstrumentationRegistry.getInstrumentation().context.assets.open("offline-sample.mp4").use { it.readBytes() }
    private fun update(change: (AppState) -> AppState) = runBlocking(Dispatchers.IO) { graph.repository.update(change) }
    private fun model(activity: ActivityScenario<MainActivity>): DownloaderViewModel {
        lateinit var result: DownloaderViewModel
        activity.onActivity { result = ViewModelProvider(it)[DownloaderViewModel::class.java] }
        return result
    }
    private fun cleanup(sourceIds: Set<String>, urls: Set<String>) {
        update { state -> state.copy(subscriptions = state.subscriptions.filterNot { it.id in sourceIds },
            items = state.items.filterNot { it.subscriptionId in sourceIds }, queue = state.queue.filterNot { it.url in urls }) }
    }

    @Test fun fullCheckBackfillsEveryConfirmedStateWithoutWaitingForImagesOrStartingDownloads() {
        initialize()
        DiscoveryLoopbackServer(sample()).use { origin ->
            val states = linkedMapOf("slow-new" to EntryState.NEW, "baseline" to EntryState.BASELINE,
                "skipped-bad" to EntryState.SKIPPED, "processed" to EntryState.PROCESSED, "already" to EntryState.ALREADY_PROCESSED)
            val source = Subscription(url = origin.collection("all-states", states.keys.toList()), title = "All states",
                shortName = "discovery-${UUID.randomUUID()}", initialized = true)
            val entries = states.map { (name, state) -> SubscriptionEntry(source.id, origin.video(name), title = "Video $name", state = state) }
            val absent = SubscriptionEntry(source.id, origin.video("historical-absent"), title = "Absent historical result", state = EntryState.PROCESSED)
            val urls = entries.map { it.url }.toSet() + absent.url
            val history = graph.repository.state.value.history
            origin.blockImage("slow-new")
            origin.imageStatus("skipped-bad", 503)
            try {
                update { it.copy(subscriptions = it.subscriptions + source, items = it.items + entries + absent) }
                ActivityScenario.launch(MainActivity::class.java).use { activity ->
                    val viewModel = model(activity)
                    compose.onNodeWithTag("subscriptionsTab").performClick()
                    compose.onNodeWithTag("forceFullSubscriptions").performClick().assertIsOn()
                    compose.onNodeWithTag("checkSubscription:${source.id}").performScrollTo().performClick()
                    await { viewModel.ui.value.operation?.active == false && viewModel.ui.value.subscriptionQueuePrompt != null }
                    await(30_000) { origin.requests("/covers/slow-new.png") > 0 }
                    assertTrue("The image request must still be blocked when the completed result and question are visible",
                        graph.previews.metadata.value[origin.video("slow-new")]?.images.isNullOrEmpty())
                    assertEquals(listOf(origin.video("slow-new")), requireNotNull(viewModel.ui.value.subscriptionQueuePrompt).keys.map { it.url })
                    assertNotNull(graph.repository.state.value.subscriptions.single { it.id == source.id }.lastSuccessUtc)
                    assertTrue(graph.repository.state.value.queue.none { it.url in urls })
                    compose.onNodeWithTag("deferSubscriptionQueue").performClick()
                    compose.onNodeWithTag("subscriptionCheckResult").assertIsDisplayed()
                    compose.onNodeWithTag("operationAnimation").assertDoesNotExist()
                    val firstResult = requireNotNull(viewModel.ui.value.operation)
                    val elapsedText = compose.onNodeWithTag("operationElapsed").fetchSemanticsNode().config.toString()
                    runBlocking(Dispatchers.IO) { delay(800) }
                    assertEquals("A completed operation cannot keep advancing its final timestamp", firstResult.finishedAtElapsedMillis,
                        viewModel.ui.value.operation?.finishedAtElapsedMillis)
                    assertEquals(elapsedText, compose.onNodeWithTag("operationElapsed").fetchSemanticsNode().config.toString())
                    origin.releaseImage("slow-new")
                    val ready = states.keys.filter { it != "skipped-bad" }
                    await { ready.all { graph.previews.metadata.value[origin.video(it)]?.images?.isNotEmpty() == true } &&
                        graph.previews.errors.value[origin.video("skipped-bad")]?.isNotBlank() == true }
                    states.forEach { (name, state) ->
                        assertTrue("The $state material must reach the real image endpoint", origin.requests("/covers/$name.png") > 0)
                        assertEquals(state, graph.repository.state.value.items.single { it.subscriptionId == source.id && it.url == origin.video(name) }.state)
                    }
                    assertEquals("One shared preview worker must not fetch images concurrently", 1, origin.maximumConcurrentImages)
                    assertEquals("An absent historical entry is outside a confirmed Full result", 0, origin.requests("/covers/historical-absent.png"))
                    val diagnostic = graph.journal.snapshotText()
                    assertTrue(diagnostic.contains("[Warning/preview]"))
                    assertTrue(diagnostic.contains("stage=http"))
                    assertTrue(diagnostic.contains("http_status=503"))
                    assertTrue(diagnostic.contains("bytes=0"))
                    assertTrue(diagnostic.contains(origin.cover("skipped-bad")))
                    val coverCounts = ready.associateWith { origin.requests("/covers/$it.png") }
                    activity.onActivity { viewModel.checkSubscriptionsFully(listOf(source.id)) }
                    await { viewModel.ui.value.operation?.let { it.id != firstResult.id && !it.active } == true }
                    await(5_000) { ready.none { origin.video(it) in graph.previews.activeUrls.value } }
                    coverCounts.forEach { (name, count) -> assertEquals("A ready preview must not repeat HTTP after another check", count, origin.requests("/covers/$name.png")) }
                    assertTrue("The real collection must have been enumerated twice", origin.requests("/collections/all-states.rss") >= 2)
                    activity.onActivity { viewModel.resolveSubscriptionQueuePrompt(false) }
                    assertEquals(history, graph.repository.state.value.history)
                    assertFalse(DownloadService.isRunning.value)
                    assertFalse(DownloadService.hasOwnedTasks)
                    states.keys.forEach { assertEquals("Preview must not download media", 0, origin.requests("/media/$it.mp4")) }
                }
            } finally { origin.releaseImage("slow-new"); cleanup(setOf(source.id), urls) }
        }
    }

    @Test fun urlAndImportedFileImmediatelyFetchActualTitlesAndPicturesWithoutSelection() {
        initialize()
        DiscoveryLoopbackServer(sample()).use { origin ->
            val manual = origin.video("manual-ingress")
            val imported = origin.video("file-ingress")
            val urls = setOf(manual, imported)
            val history = graph.repository.state.value.history
            val list = File(File(context.filesDir, "exports").apply { mkdirs() }, "discovery-${UUID.randomUUID()}.txt").apply { writeText(imported) }
            try {
                ActivityScenario.launch(MainActivity::class.java).use { activity ->
                    val viewModel = model(activity)
                    val uri = FileProvider.getUriForFile(context, "${context.packageName}.files", list)
                    activity.onActivity { viewModel.addUrls(manual); viewModel.importList(uri) }
                    await { urls.all { url -> graph.repository.state.value.queue.any { it.url == url && it.title.isNotBlank() && it.title != url } &&
                        graph.previews.metadata.value[url]?.images?.isNotEmpty() == true } }
                    assertNull("Cold metadata cannot depend on opening a selected row", viewModel.ui.value.preview)
                    assertTrue(origin.requests("/covers/manual-ingress.png") > 0)
                    assertTrue(origin.requests("/covers/file-ingress.png") > 0)
                    assertEquals(0, origin.requests("/media/manual-ingress.mp4"))
                    assertEquals(0, origin.requests("/media/file-ingress.mp4"))
                    assertEquals(history, graph.repository.state.value.history)
                    assertFalse(DownloadService.isRunning.value)
                }
            } finally { cleanup(emptySet(), urls); list.delete() }
        }
    }

    @Test fun galleryRetriesFailedEmptyImagesByNextRequestAndPreservesGoodFilesAndLegacyTitles() = runBlocking(Dispatchers.IO) {
        initialize()
        DiscoveryLoopbackServer(sample()).use { origin ->
            var now = System.currentTimeMillis()
            val gallery = PreviewGallery(context) { now }
            val failedUrl = origin.video("retry-empty")
            val diagnostics = mutableListOf<PreviewDiagnostic>()
            origin.imageStatus("retry-empty", 503)
            val payload = JSONObject().put("title", "Retained title").put("thumbnail", origin.cover("retry-empty"))
            val failed = gallery.fetch(failedUrl, payload, {}, diagnostics::add)
            assertTrue(failed.images.isEmpty())
            assertTrue(failed.previewError.isNotBlank())
            assertEquals("Retained title", failed.title)
            assertEquals(503, diagnostics.single().httpStatus)
            assertEquals(0, diagnostics.single().bytes)
            assertEquals("http", diagnostics.single().stage)
            assertEquals(1, diagnostics.single().imageIndex)
            assertEquals(1, origin.requests("/covers/retry-empty.png"))
            origin.imageStatus("retry-empty", 200)
            now += PreviewCachePolicy.FAILURE_RETRY_MILLIS - 1
            assertNotNull(gallery.get(failedUrl))
            assertEquals("Reading a fresh failure must not create an HTTP retry", 1, origin.requests("/covers/retry-empty.png"))
            now++
            assertNull("A later request is permitted after one minute", gallery.get(failedUrl))
            val retried = gallery.fetch(failedUrl, payload, {}, diagnostics::add)
            assertEquals(2, origin.requests("/covers/retry-empty.png"))
            assertTrue(retried.images.single().isFile)
            assertTrue(retried.previewError.isBlank())

            val goodFile = retried.images.single()
            val oldTitle = retried.title
            origin.imageStatus("replacement-fails", 503)
            val retained = gallery.fetch(failedUrl, JSONObject().put("thumbnail", origin.cover("replacement-fails")), {}, diagnostics::add)
            assertEquals("An image failure must preserve a previous usable cover", listOf(goodFile), retained.images)
            assertEquals(oldTitle, retained.title)
            assertTrue(goodFile.isFile)
            assertNotNull("A usable image keeps the ready cache lifetime", gallery.get(failedUrl))
            migrateRecordToVersionOne(failedUrl)
            assertEquals("A valid legacy image remains usable", listOf(goodFile), gallery.get(failedUrl)?.images)

            val absentUrl = origin.video("no-candidates")
            val absent = gallery.fetch(absentUrl, JSONObject().put("title", "No pictures supplied"), {}, diagnostics::add)
            assertTrue(absent.images.isEmpty())
            assertTrue("Absence of site images is not a fetch error", absent.previewError.isBlank())
            val savedAt = now
            now = savedAt + PreviewCachePolicy.READY_TTL_MILLIS - 1
            assertNotNull("A genuine no-candidate result stays ready for 72 hours", gallery.get(absentUrl))
            now++
            assertNull(gallery.get(absentUrl))
            now = savedAt
            migrateRecordToVersionOne(absentUrl)
            assertNull("A legacy blank cache did not distinguish an error, so upgrade must recheck it", gallery.get(absentUrl))
            assertEquals("No pictures supplied", gallery.get(absentUrl, allowStale = true)?.title)
            assertEquals(0, origin.requests("/covers/no-candidates.png"))
            assertFalse(DownloadService.isRunning.value)
        }
    }

    private fun migrateRecordToVersionOne(url: String) {
        val hash = MessageDigest.getInstance("SHA-256").digest(url.toByteArray(Charsets.UTF_8)).joinToString("") { "%02x".format(it) }
        val file = File(context.cacheDir.canonicalFile, "preview-cache/$hash/metadata.json")
        val old = JSONObject(file.readText()).put("version", 1).apply { remove("previewError") }
        file.writeText(old.toString())
    }
}
