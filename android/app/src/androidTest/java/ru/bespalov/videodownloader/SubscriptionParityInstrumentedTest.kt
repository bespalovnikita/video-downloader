package ru.bespalov.videodownloader

import android.app.Activity
import android.app.Instrumentation
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Environment
import androidx.compose.ui.test.assertHasClickAction
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.assertIsSelected
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.test.onAllNodesWithTag
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.lifecycle.ViewModelProvider
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.uiautomator.UiDevice
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import ru.bespalov.video.core.DownloadStatus
import ru.bespalov.video.core.DownloadTask
import ru.bespalov.video.core.EntryState
import ru.bespalov.video.core.Subscription
import ru.bespalov.video.core.SubscriptionEntry
import ru.bespalov.videodownloader.download.DownloadService
import java.io.File
import java.util.UUID
import java.util.concurrent.atomic.AtomicReference

/** Real Activity, repository, clipboard and packaged extractor; never starts a download. */
@RunWith(AndroidJUnit4::class)
class SubscriptionParityInstrumentedTest {
    @get:Rule val compose = createEmptyComposeRule()
    private val context: Context get() = ApplicationProvider.getApplicationContext()
    private val graph: AppGraph get() = (context.applicationContext as DownloaderApplication).graph

    private fun initialize() = runBlocking(Dispatchers.IO) {
        withTimeout(90_000) { graph.ready.await() }
        assertTrue(graph.startupError.value, graph.runtimeReady.value)
        assertFalse(graph.repository.error, graph.repository.readOnly)
        assertFalse("A previous test must finish its foreground session", DownloadService.isRunning.value)
    }

    private fun sample() = InstrumentationRegistry.getInstrumentation().context.assets.open("offline-sample.mp4").use { it.readBytes() }
    private fun update(change: (ru.bespalov.video.core.AppState) -> ru.bespalov.video.core.AppState) =
        runBlocking(Dispatchers.IO) { graph.repository.update(change) }
    private fun cleanup(sourceIds: Set<String>, urls: Set<String>) = update { state -> state.copy(
        subscriptions = state.subscriptions.filterNot { it.id in sourceIds },
        items = state.items.filterNot { it.subscriptionId in sourceIds },
        queue = state.queue.filterNot { it.url in urls },
    ) }
    private fun model(activity: ActivityScenario<MainActivity>): DownloaderViewModel {
        lateinit var result: DownloaderViewModel
        activity.onActivity { result = ViewModelProvider(it)[DownloaderViewModel::class.java] }
        return result
    }

    @Test fun baselineCanBeQueuedAndImmediatelyGetsRealPosterWithoutStart() {
        initialize()
        LoopbackSubscriptionServer(sample()).use { origin ->
            val source = Subscription(url = origin.sourceUrl("baseline") + "?Token=AbC&keep=1", title = "Baseline source", shortName = "baseline", initialized = true)
            val first = SubscriptionEntry(source.id, origin.videoUrl("baseline1"), title = "First baseline", state = EntryState.BASELINE, discoveredAtUtc = 1_700_000_000_000)
            val second = SubscriptionEntry(source.id, origin.videoUrl("baseline2"), title = "Second baseline", state = EntryState.BASELINE)
            val urls = setOf(first.url, second.url)
            val history = graph.repository.state.value.history
            try {
                update { it.copy(subscriptions = it.subscriptions + source, items = it.items + listOf(first, second)) }
                ActivityScenario.launch(MainActivity::class.java).use { activity ->
                    compose.onNodeWithTag("subscriptionsTab").performClick()
                    compose.onNodeWithTag("subscriptionSource:${source.id}").performClick()
                    compose.onNodeWithTag("subscriptionFilter:1").assertIsSelected()
                    compose.onNodeWithTag("selectedSubscriptionUrl").assertIsDisplayed()
                    compose.onNodeWithText(source.url).assertIsDisplayed()
                    compose.onNodeWithTag("copySubscriptionUrl").performClick()
                    var copied = ""
                    compose.waitUntil(5_000) {
                        activity.onActivity { copied = (it.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager).primaryClip?.getItemAt(0)?.text?.toString().orEmpty() }
                        copied == source.url
                    }
                    assertFalse("Opening baseline must not request image network", origin.requests("/covers/baseline1.png") > 0)
                    compose.onNodeWithTag("selectSubscriptionEntry:${first.url}").assertIsEnabled().performClick()
                    compose.onNodeWithText("В очередь (1)").performClick()
                    compose.waitUntil(20_000) { graph.repository.state.value.queue.any { it.url == first.url } }
                    compose.waitUntil(60_000) { graph.previews.metadata.value[first.url]?.images?.isNotEmpty() == true }
                    assertTrue("The actual HTTP image must be fetched before selection or Start", origin.requests("/covers/baseline1.png") > 0)
                    val accepted = graph.repository.state.value
                    assertEquals(EntryState.QUEUED, accepted.items.single { it.subscriptionId == source.id && it.url == first.url }.state)
                    assertEquals(EntryState.BASELINE, accepted.items.single { it.subscriptionId == source.id && it.url == second.url }.state)
                    assertTrue(accepted.queue.single { it.url == first.url }.let { it.status == DownloadStatus.QUEUED && it.deferredNextStart && it.outputUris.isEmpty() })
                    compose.onNodeWithTag("subscriptionFilter:3").performClick()
                    compose.onNodeWithTag("subscriptionEntry:${first.url}").performClick()
                    compose.onNodeWithTag("subscriptionDetails").assertIsDisplayed()
                    compose.onNodeWithText("Подписка: Baseline source").assertIsDisplayed()
                    compose.onNodeWithText("Состояние: В очереди").assertIsDisplayed()
                    compose.onNodeWithText("Обнаружено:", substring = true).assertIsDisplayed()
                    compose.onNodeWithTag("previewGallery").performScrollTo().assertIsDisplayed()
                    compose.onNodeWithContentDescription("Превью 1 из 1").assertIsDisplayed()
                    compose.onNodeWithText("Закрыть").performClick()
                    activity.recreate()
                    compose.waitUntil(10_000) { graph.repository.state.value.queue.any { it.url == first.url } }
                    assertEquals(history, graph.repository.state.value.history)
                    assertFalse("Accepting baseline must wait for Start", DownloadService.isRunning.value)
                }
            } finally { cleanup(setOf(source.id), urls) }
        }
    }

    @Test fun oneBatchOfferExcludesFailedAndBaselineAndSurvivesNoAndClose() {
        initialize()
        LoopbackSubscriptionServer(sample()).use { origin ->
            val a = Subscription(url = origin.sourceUrl("a"), title = "A", shortName = "a", initialized = true)
            val b = Subscription(url = origin.sourceUrl("b"), title = "B", shortName = "b", initialized = true)
            val failed = Subscription(url = origin.sourceUrl("forbidden"), title = "Failed", shortName = "failed", initialized = true)
            val old = SubscriptionEntry(a.id, origin.videoUrl("old-a"), title = "Earlier pending")
            val baseline = SubscriptionEntry(a.id, origin.videoUrl("old-baseline"), state = EntryState.BASELINE)
            val failedPending = SubscriptionEntry(failed.id, origin.videoUrl("old-failed"))
            val acceptedUrls = setOf("a1", "a2", "b1", "b2", "old-a").map(origin::videoUrl).toSet()
            val allUrls = acceptedUrls + baseline.url + failedPending.url
            val sources = listOf(a, b, failed)
            val sourceIds = sources.map { it.id }
            val history = graph.repository.state.value.history
            try {
                update { it.copy(subscriptions = it.subscriptions + sources, items = it.items + listOf(old, baseline, failedPending)) }
                ActivityScenario.launch(MainActivity::class.java).use { activity ->
                    val viewModel = model(activity)
                    fun checkBatch(start: Boolean = true) {
                        if (start) activity.onActivity { viewModel.checkSubscriptions(sourceIds) }
                        compose.waitUntil(120_000) { viewModel.ui.value.subscriptionQueuePrompt != null && viewModel.ui.value.operation == null }
                        val prompt = requireNotNull(viewModel.ui.value.subscriptionQueuePrompt)
                        assertEquals(acceptedUrls, prompt.keys.map { it.url }.toSet())
                        assertEquals(2, prompt.sourceCount)
                        assertEquals(1, compose.onAllNodesWithTag("subscriptionQueuePrompt").fetchSemanticsNodes().size)
                        assertTrue(graph.repository.state.value.subscriptions.single { it.id == failed.id }.lastError.isNotBlank())
                        assertTrue(graph.repository.state.value.queue.none { it.url in allUrls })
                    }
                    checkBatch()
                    compose.onNodeWithTag("deferSubscriptionQueue").performClick()
                    compose.waitUntil(5_000) { viewModel.ui.value.subscriptionQueuePrompt == null }
                    assertTrue(graph.repository.state.value.items.filter { it.url in acceptedUrls }.all { it.state == EntryState.NEW })
                    checkBatch()
                    UiDevice.getInstance(InstrumentationRegistry.getInstrumentation()).pressBack()
                    compose.waitUntil(5_000) { viewModel.ui.value.subscriptionQueuePrompt == null }
                    assertTrue(graph.repository.state.value.items.filter { it.url in acceptedUrls }.all { it.state == EntryState.NEW })
                    checkBatch()
                    // Hold admission while an old acceptance is suspended. Starting a
                    // newer check must invalidate it before it can mutate the queue.
                    runBlocking(Dispatchers.IO) { graph.dataOperation.lock() }
                    try {
                        activity.onActivity {
                            viewModel.resolveSubscriptionQueuePrompt(true)
                            viewModel.checkSubscriptions(sourceIds)
                        }
                    } finally { graph.dataOperation.unlock() }
                    checkBatch(start = false)
                    compose.onNodeWithTag("acceptSubscriptionQueue").performClick()
                    compose.waitUntil(20_000) { acceptedUrls.all { url -> graph.repository.state.value.queue.any { it.url == url } } }
                    val after = graph.repository.state.value
                    assertTrue(after.queue.filter { it.url in acceptedUrls }.all { it.status == DownloadStatus.QUEUED && it.deferredNextStart })
                    assertEquals(EntryState.BASELINE, after.items.single { it.url == baseline.url }.state)
                    assertEquals(EntryState.NEW, after.items.single { it.url == failedPending.url }.state)
                    assertTrue(after.queue.none { it.url == baseline.url || it.url == failedPending.url })
                    assertEquals(history, after.history)
                    assertFalse("The batch offer must never start the foreground downloader", DownloadService.isRunning.value)
                }
            } finally { cleanup(sourceIds.toSet(), allUrls) }
        }
    }

    @Test fun cancelledBatchDoesNotOfferAlreadyFoundMaterials() {
        initialize()
        LoopbackSubscriptionServer(sample()).use { origin ->
            origin.blockSource("b")
            val sources = listOf("a", "b").map { Subscription(url = origin.sourceUrl(it), title = "Cancel $it", shortName = "cancel-$it", initialized = true) }
            val ids = sources.map { it.id }.toSet()
            val urls = setOf("a1", "a2", "b1", "b2").map(origin::videoUrl).toSet()
            val history = graph.repository.state.value.history
            try {
                update { it.copy(subscriptions = it.subscriptions + sources) }
                ActivityScenario.launch(MainActivity::class.java).use { activity ->
                    val viewModel = model(activity)
                    activity.onActivity { viewModel.checkSubscriptions(ids.toList()) }
                    compose.waitUntil(90_000) { origin.requests("/collections/b.rss") > 0 }
                    assertNotNull(graph.repository.state.value.subscriptions.single { it.id == sources.first().id }.lastSuccessUtc)
                    assertNull("No question may appear before the entire batch finishes", viewModel.ui.value.subscriptionQueuePrompt)
                    compose.onNodeWithText("Отменить проверку").performClick()
                    origin.releaseSource("b")
                    compose.waitUntil(30_000) { viewModel.ui.value.operation == null }
                    assertNull(viewModel.ui.value.subscriptionQueuePrompt)
                    assertTrue(graph.repository.state.value.queue.none { it.url in urls })
                    assertEquals(history, graph.repository.state.value.history)
                    assertFalse(DownloadService.isRunning.value)
                }
            } finally { origin.releaseSource("b"); cleanup(ids, urls) }
        }
    }

    @Test fun filenameHelpAndCompletedButtonWorkWithoutChangingSettingsOrSharingPrivatePaths() {
        initialize()
        val token = UUID.randomUUID().toString()
        val root = requireNotNull(context.getExternalFilesDir(Environment.DIRECTORY_MOVIES)).apply { mkdirs() }
        val file = File(root, "parity-$token.mp4").apply { writeBytes(sample()) }
        val task = DownloadTask(url = "https://example.invalid/parity-$token", title = "Ready result", status = DownloadStatus.COMPLETED, outputUris = listOf(Uri.fromFile(file).toString()))
        val settings = graph.repository.state.value.settings
        val captured = AtomicReference<Intent>()
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val monitor = object : Instrumentation.ActivityMonitor() {
            override fun onStartActivity(intent: Intent): Instrumentation.ActivityResult? {
                if (intent.action != Intent.ACTION_VIEW) return null
                captured.set(Intent(intent))
                return Instrumentation.ActivityResult(Activity.RESULT_OK, null)
            }
        }
        try {
            update { it.copy(queue = it.queue + task) }
            ActivityScenario.launch(MainActivity::class.java).use {
                instrumentation.addMonitor(monitor)
                compose.onNodeWithTag("openCompletedFile:${task.id}").performScrollTo().assertHasClickAction().performClick()
                compose.waitUntil(5_000) { captured.get() != null }
                val intent = captured.get()
                assertEquals("content", intent.data?.scheme)
                assertEquals("video/mp4", intent.type)
                assertTrue(intent.flags and Intent.FLAG_GRANT_READ_URI_PERMISSION != 0)
                val actual = context.contentResolver.openInputStream(requireNotNull(intent.data))!!.use { input -> input.readBytes() }
                assertArrayEquals(sample(), actual)
                compose.onNodeWithTag("settingsTab").performClick()
                compose.onNodeWithTag("filenameTemplateHelp").performScrollTo().performClick()
                compose.onNodeWithTag("filenameTemplateHelpDialog").assertIsDisplayed()
                compose.onNodeWithText("%(title)s — название видео", substring = true).assertIsDisplayed()
                compose.onNodeWithText("Шаблон — относительное имя", substring = true).performScrollTo().assertIsDisplayed()
                compose.onNodeWithText("Понятно").performClick()
                assertEquals("Reading help must not change a saved template", settings, graph.repository.state.value.settings)
                assertFalse(DownloadService.isRunning.value)
            }
        } finally {
            instrumentation.removeMonitor(monitor)
            cleanup(emptySet(), setOf(task.url))
            file.delete()
        }
    }
}
