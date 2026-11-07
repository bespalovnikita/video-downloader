package ru.bespalov.videodownloader

import android.app.Activity
import android.app.Instrumentation
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Bundle
import android.os.Process
import android.provider.DocumentsContract
import android.os.SystemClock
import androidx.compose.ui.test.hasScrollAction
import androidx.compose.ui.test.hasTestTag
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollToNode
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.runner.intent.IntentCallback
import androidx.test.runner.intent.IntentMonitorRegistry
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import ru.bespalov.video.core.DownloadStatus
import ru.bespalov.video.core.DownloadTask
import ru.bespalov.video.core.JsonStateRepository
import ru.bespalov.video.core.Settings
import ru.bespalov.video.core.SubscriptionRules
import ru.bespalov.videodownloader.download.DownloadService
import ru.bespalov.videodownloader.download.MediaPublisher
import ru.bespalov.videodownloader.ui.OutputViewing
import java.io.File
import java.util.UUID
import java.util.concurrent.atomic.AtomicReference

/** Native service publication, actual MediaStore/SAF resolvers, reopened state and Compose open actions. */
@RunWith(AndroidJUnit4::class)
class PrimaryOutputInstrumentedTest {
    @get:Rule val compose = createEmptyComposeRule()
    private val context: Context get() = ApplicationProvider.getApplicationContext()
    private val graph: AppGraph get() = (context.applicationContext as DownloaderApplication).graph
    private val instrumentation get() = InstrumentationRegistry.getInstrumentation()
    private fun await(predicate: () -> Boolean) = runBlocking(Dispatchers.IO) {
        withTimeout(90_000) { while (!predicate()) delay(50) }
    }
    private fun initialize() = runBlocking(Dispatchers.IO) {
        withTimeout(90_000) { graph.ready.await() }
        assertTrue(graph.startupError.value, graph.runtimeReady.value)
        assertFalse(graph.repository.error, graph.repository.readOnly)
        withTimeout(30_000) { while (DownloadService.isRunning.value || DownloadService.hasOwnedTasks) delay(50) }
    }
    private fun sample() = instrumentation.context.assets.open("offline-sample.mp4").use { it.readBytes() }
    private fun openTask(id: String) {
        val tag = "openCompletedFile:$id"
        compose.onNode(hasScrollAction()).performScrollToNode(hasTestTag(tag))
        compose.onNodeWithTag(tag).performClick()
    }

    private fun observeViews(block: (AtomicReference<Intent?>) -> Unit) {
        val captured = AtomicReference<Intent?>()
        val callback = IntentCallback { intent -> if (intent.action == Intent.ACTION_VIEW) captured.set(Intent(intent)) }
        val monitor = instrumentation.addMonitor(IntentFilter(Intent.ACTION_VIEW), Instrumentation.ActivityResult(Activity.RESULT_OK, null), true)
        IntentMonitorRegistry.getInstance().addIntentCallback(callback)
        try { block(captured) }
        finally {
            IntentMonitorRegistry.getInstance().removeIntentCallback(callback)
            instrumentation.removeMonitor(monitor)
        }
    }

    @Test fun serviceKeepsPrimaryThroughSidecarFirstMediaStorePublicationReopenAndQueueHistoryUi() {
        initialize()
        val token = UUID.randomUUID().toString()
        val run = UUID.randomUUID().toString()
        val settings = Settings(filenameTemplate = "primary-$token.%(ext)s", embedMetadata = false, embedChapters = false)
        LoopbackMediaServer(sample()).use { origin ->
            val task = DownloadTask(url = "${origin.baseUrl}/primary-$token.mp4", status = DownloadStatus.INTERRUPTED,
                runId = run, settingsSnapshot = settings)
            val stage = File(context.filesDir, "staging/$run/${task.id}").apply { mkdirs() }
            // A retained sidecar precedes the newly downloaded media in this real staging directory.
            File(stage, "primary-$token.en.vtt").writeText("WEBVTT\n\n00:00.000 --> 00:01.000\nPrimary output test\n")
            val resumable = task.copy(stageDirectory = stage.absolutePath)
            var published = emptyList<String>()
            try {
                runBlocking(Dispatchers.IO) { graph.repository.update { it.copy(queue = it.queue + resumable) } }
                ActivityScenario.launch(MainActivity::class.java).use { activity ->
                    activity.onActivity { DownloadService.start(it, listOf(task.id)) }
                    await { graph.repository.state.value.queue.single { it.id == task.id }.status in setOf(DownloadStatus.COMPLETED, DownloadStatus.FAILED, DownloadStatus.CLEANUP_REQUIRED) }
                    val completed = graph.repository.state.value.queue.single { it.id == task.id }
                    published = completed.outputUris
                    assertEquals(completed.rawError, DownloadStatus.COMPLETED, completed.status)
                    assertEquals(2, published.size)
                    assertNotEquals("This regression must exercise sidecar-first ordering", published.first(), completed.primaryOutputUri)
                    assertEquals("video/mp4", context.contentResolver.getType(Uri.parse(completed.primaryOutputUri)))
                    await { !DownloadService.isRunning.value && !DownloadService.hasOwnedTasks }
                    val reopened = JsonStateRepository(File(context.filesDir, "state.json"))
                    assertFalse(reopened.error, reopened.readOnly)
                    val restored = reopened.state.value.queue.single { it.id == task.id }
                    val history = reopened.state.value.history.single { it.url == task.url }
                    assertEquals(completed.primaryOutputUri, restored.primaryOutputUri)
                    assertEquals(completed.primaryOutputUri, history.primaryOutputUri)
                    runBlocking(Dispatchers.IO) { graph.repository.update { state -> state.copy(
                        queue = state.queue.map { if (it.id == task.id) restored else it },
                        history = state.history.map { if (it.url == task.url) history else it }) } }
                    observeViews { captured ->
                        compose.onNodeWithTag("downloadsTab").performClick()
                        openTask(task.id)
                        await { captured.get() != null }
                        assertEquals(restored.primaryOutputUri, captured.get()!!.data.toString())
                        assertEquals("video/mp4", captured.get()!!.type)
                        captured.set(null)
                        compose.onNodeWithText("История (${graph.repository.state.value.history.size})").performClick()
                        compose.onNodeWithTag("openHistoryFile:${history.completedAtUtc}").performClick()
                        await { captured.get() != null }
                        assertEquals(restored.primaryOutputUri, captured.get()!!.data.toString())
                    }
                    // Old history without the additive primary field uses provider MIME, never URI order.
                    runBlocking(Dispatchers.IO) { assertEquals(restored.primaryOutputUri, OutputViewing.primary(context, "", published)) }
                }
            } finally { cleanup(task, published, stage) }
        }
    }

    @Test fun safOpaqueUrisKeepExplicitPrimaryAndLegacyMultipleMediaRequiresAChoice() {
        initialize()
        val token = UUID.randomUUID().toString()
        val tree = requireNotNull(context.contentResolver.call(OutputDocumentsProvider.CONTROL, "fixture", token,
            Bundle().apply { putString("package", context.packageName) })).getString("tree")!!
        val stage = File(context.cacheDir, "primary-saf-$token").apply { mkdirs() }
        val subtitle = File(stage, "lesson.en.vtt").apply { writeText("WEBVTT\n") }
        val video = File(stage, "lesson.mp4").apply { writeBytes(sample()) }
        val second = File(stage, "second.mp4").apply { writeBytes(sample()) }
        val task = DownloadTask(url = "https://example.invalid/primary-saf-$token")
        var published = emptyList<String>()
        try {
            val batch = runBlocking(Dispatchers.IO) {
                MediaPublisher(context).publish(stage, listOf(subtitle, video, second), second, Settings(outputTreeUri = tree), "", "", { false }) { published = it }
            }
            assertEquals(published.last(), batch.primaryUri)
            assertEquals("SAF access must come from the explicit tree grant", PackageManager.PERMISSION_GRANTED,
                context.checkUriPermission(Uri.parse(tree), Process.myPid(), Process.myUid(),
                    Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION))
            val ungrantedDocument = DocumentsContract.buildDocumentUri(OutputDocumentsProvider.AUTHORITY,
                DocumentsContract.getDocumentId(Uri.parse(batch.primaryUri)))
            assertEquals("A tree grant must not grant the standalone document URI", PackageManager.PERMISSION_DENIED,
                context.checkUriPermission(ungrantedDocument, Process.myPid(), Process.myUid(), Intent.FLAG_GRANT_READ_URI_PERMISSION))
            assertThrows(SecurityException::class.java) { context.contentResolver.openInputStream(ungrantedDocument)?.close() }
            assertTrue(published.all { Uri.parse(it).lastPathSegment!!.matches(Regex("[a-f0-9-]{36}")) })
            runBlocking(Dispatchers.IO) {
                graph.repository.update { SubscriptionRules.recordSuccess(it.copy(queue = it.queue + task), task.id, batch.uris, primaryOutputUri = batch.primaryUri) }
            }
            val reopened = JsonStateRepository(File(context.filesDir, "state.json"))
            assertFalse(reopened.error, reopened.readOnly)
            val restored = reopened.state.value.queue.single { it.id == task.id }
            assertEquals(batch.primaryUri, restored.primaryOutputUri)
            assertEquals(batch.primaryUri, reopened.state.value.history.single { it.url == task.url }.primaryOutputUri)
            runBlocking(Dispatchers.IO) {
                assertEquals(batch.primaryUri, OutputViewing.primary(context, restored.primaryOutputUri, published))
                assertNull(OutputViewing.primary(context, "content://foreign/video.mp4", published))
                assertEquals(listOf("lesson.en.vtt", "lesson.mp4", "second.mp4"), OutputViewing.files(context, published).map { it.name })
            }
            ActivityScenario.launch(MainActivity::class.java).use {
                observeViews { captured ->
                    compose.onNodeWithTag("downloadsTab").performClick()
                    openTask(task.id)
                    await { captured.get() != null }
                    assertEquals(batch.primaryUri, captured.get()!!.data.toString())
                    captured.set(null)
                    runBlocking(Dispatchers.IO) { graph.repository.update { state -> state.copy(queue = state.queue.map { row ->
                        if (row.id == task.id) row.copy(primaryOutputUri = "") else row }) } }
                    openTask(task.id)
                    compose.waitUntil(10_000) { compose.onAllNodes(hasTestTag("publishedFile:0")).fetchSemanticsNodes().isNotEmpty() }
                    assertNull("Legacy records with two media must not silently open a sidecar or arbitrary video", captured.get())
                    compose.onNodeWithTag("publishedFile:1").performClick()
                    await { captured.get() != null }
                    assertEquals(published[1], captured.get()!!.data.toString())
                    captured.set(null)
                    compose.onNodeWithTag("chooseCompletedFiles:${task.id}").performClick()
                    compose.waitUntil(10_000) { compose.onAllNodes(hasTestTag("publishedFile:0")).fetchSemanticsNodes().isNotEmpty() }
                    compose.onNodeWithTag("publishedFile:0").performClick()
                    await { captured.get() != null }
                    assertEquals("A sidecar remains available through explicit selection", published[0], captured.get()!!.data.toString())
                }
            }
        } finally {
            try { cleanup(task, published, stage) }
            finally { context.contentResolver.call(OutputDocumentsProvider.CONTROL, "forget", token, null) }
        }
    }

    @Test fun timedOutProviderQueriesKeepTheirSlotsUntilTheRealBinderCallsReturn() = runBlocking(Dispatchers.IO) {
        val token = UUID.randomUUID().toString()
        val tree = requireNotNull(context.contentResolver.call(OutputDocumentsProvider.CONTROL, "fixture", token,
            Bundle().apply { putString("package", context.packageName) })).getString("tree")!!
        val stage = File(context.cacheDir, "primary-timeout-$token").apply { mkdirs() }
        val media = File(stage, "timeout.mp4").apply { writeBytes(sample()) }
        var published = emptyList<String>()
        fun control(method: String) = context.contentResolver.call(OutputDocumentsProvider.CONTROL, method, token, null)
        try {
            val batch = MediaPublisher(context).publish(stage, listOf(media), media, Settings(outputTreeUri = tree), "", "", { false }) { published = it }
            control("block")
            val first = async { OutputViewing.files(context, batch.uris) }
            val second = async { OutputViewing.files(context, batch.uris) }
            withTimeout(5_000) { while (control("status")!!.getInt("waiting") != 2) delay(25) }
            assertEquals("application/octet-stream", first.await().single().mime)
            assertEquals("application/octet-stream", second.await().single().mime)
            assertEquals("Timeout must not release the two still-blocked provider slots", 2, control("status")!!.getInt("waiting"))
            val started = SystemClock.elapsedRealtime()
            assertEquals("application/octet-stream", OutputViewing.files(context, batch.uris).single().mime)
            assertTrue("A third request must fall back without queueing another Binder call", SystemClock.elapsedRealtime() - started < 1_000)
            assertEquals(2, control("status")!!.getInt("waiting"))
            control("release")
            withTimeout(5_000) { while (control("status")!!.getInt("waiting") != 0) delay(25) }
            withTimeout(5_000) { while (OutputViewing.files(context, batch.uris).single().mime != "video/mp4") delay(25) }
        } finally {
            control("release")
            MediaPublisher(context).deleteOwned(published)
            stage.deleteRecursively()
            control("forget")
        }
    }

    private fun cleanup(task: DownloadTask, published: List<String>, stage: File) = runBlocking(Dispatchers.IO) {
        withTimeout(30_000) { while (DownloadService.isRunning.value || DownloadService.hasOwnedTasks) delay(50) }
        val current = graph.repository.state.value.queue.firstOrNull { it.id == task.id }
        assertTrue(MediaPublisher(context).deleteOwned((published + current?.outputUris.orEmpty()).distinct()).isEmpty())
        graph.repository.update { state -> state.copy(queue = state.queue.filterNot { it.id == task.id }, history = state.history.filterNot { it.url == task.url }) }
        stage.deleteRecursively()
    }
}
