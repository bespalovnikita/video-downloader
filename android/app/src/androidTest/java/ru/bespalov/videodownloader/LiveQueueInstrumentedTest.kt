package ru.bespalov.videodownloader

import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.ViewModelProvider
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import ru.bespalov.video.core.*
import ru.bespalov.videodownloader.download.DownloadService
import ru.bespalov.videodownloader.download.MediaPublisher
import ru.bespalov.videodownloader.ui.EntryKey
import java.io.File
import java.nio.ByteBuffer
import java.security.MessageDigest
import java.util.UUID

@RunWith(AndroidJUnit4::class)
class LiveQueueInstrumentedTest {
    @get:Rule val compose = createEmptyComposeRule()
    private val context: Context get() = ApplicationProvider.getApplicationContext()
    private val graph: AppGraph get() = (context.applicationContext as DownloaderApplication).graph
    private fun await(timeout: Long = 90_000, condition: () -> Boolean) = runBlocking(Dispatchers.IO) {
        withTimeout(timeout) { while (!condition()) delay(50) }
    }
    private fun initialize() = runBlocking(Dispatchers.IO) {
        withTimeout(90_000) { graph.ready.await() }
        assertTrue(graph.startupError.value, graph.runtimeReady.value)
        assertFalse(graph.repository.error, graph.repository.readOnly)
        withTimeout(30_000) { while (DownloadService.isRunning.value || DownloadService.hasOwnedTasks) delay(50) }
    }
    private fun sample() = InstrumentationRegistry.getInstrumentation().context.assets.open("offline-sample.mp4").use { it.readBytes() }
    private fun padded() = sample() + ByteBuffer.allocate(2 * 1024 * 1024).apply {
        putInt(capacity()); put("free".toByteArray(Charsets.US_ASCII))
    }.array()
    private fun page(media: String) = "<!doctype html><html><head><title>$media</title></head><body><video controls><source src='$media.mp4' type='video/mp4'></video></body></html>".toByteArray()
    private fun model(activity: ActivityScenario<MainActivity>): DownloaderViewModel {
        lateinit var result: DownloaderViewModel
        activity.onActivity { result = ViewModelProvider(it)[DownloaderViewModel::class.java] }
        return result
    }
    private fun update(change: (AppState) -> AppState) = runBlocking(Dispatchers.IO) { graph.repository.update(change) }
    private fun settings(old: Settings, token: String) = old.copy(profile = "Universal", threads = 1, fragments = 1,
        quality = "Best", codec = "Auto", container = "Auto", filenameTemplate = "$token-%(id)s.%(ext)s",
        outputTreeUri = "", cookiesFile = "", rateLimit = "Unlimited", audioOnly = false, archive = false,
        sponsor = false, writeSubs = false, autoSubs = false, embedSubs = false, embedThumbnail = false,
        embedMetadata = false, embedChapters = false, logProfile = "Detailed")

    @Test fun manualAddJoinsPausedSessionWhileImportsWaitAndCancellingOneJobPreservesOthers() {
        initialize()
        val token = UUID.randomUUID().toString()
        val names = listOf("$token-first", "$token-manual", "$token-imported", "$token-source")
        val bodies = names.flatMap { name -> listOf("/$name.html" to page(name), "/$name.mp4" to if (name == names.first()) padded() else sample()) }.toMap()
        SessionLoopbackServer(bodies, mapOf("/${names.first()}.mp4" to 100L)).use { origin ->
            val urls = names.map { "${origin.baseUrl}/$it.html" }.toSet()
            val first = DownloadTask(url = urls.first(), title = "First")
            val source = Subscription(url = "${origin.baseUrl}/collection", shortName = "sub-$token", initialized = true)
            val sourceItem = SubscriptionEntry(source.id, urls.last(), title = "From source")
            val old = graph.repository.state.value.settings
            val sentinel = File(File(context.filesDir, "exports").apply { mkdirs() }, "unrelated-$token.txt").apply { writeText("Keep this unrelated file") }
            try {
                update { it.copy(settings = settings(old, token), queue = it.queue + first,
                    subscriptions = it.subscriptions + source, items = it.items + sourceItem) }
                ActivityScenario.launch(MainActivity::class.java).use { activity ->
                    val viewModel = model(activity)
                    activity.onActivity { viewModel.start(listOf(first.id)) }
                    await { DownloadService.progress.value[first.id]?.downloadedBytes?.let { it > 0 } == true }
                    val session = requireNotNull(DownloadService.session.value).id
                    activity.onActivity { viewModel.pause() }
                    await(30_000) { DownloadService.session.value?.paused == true && graph.repository.state.value.queue.single { it.id == first.id }.status == DownloadStatus.PAUSED }
                    val pausedTime = requireNotNull(DownloadService.session.value).elapsedMillis
                    activity.onActivity { viewModel.addUrls(urls.elementAt(1)) }
                    await { DownloadService.session.value?.total == 2 && graph.repository.state.value.queue.any {
                        it.url == urls.elementAt(1) && it.status == DownloadStatus.PAUSED && !it.deferredNextStart
                    } }
                    assertEquals(session, DownloadService.session.value?.id)
                    assertTrue("Adding a URL cannot resume a paused session", DownloadService.session.value?.paused == true)
                    assertEquals(0, origin.getCount("/${names[1]}.mp4"))
                    activity.onActivity {
                        viewModel.importQueueText(PortableQueue.encode(listOf(PortableQueue.Entry(urls.elementAt(2), "Imported"))))
                        viewModel.queueEntries(listOf(EntryKey(source.id, sourceItem.url)))
                    }
                    await { graph.repository.state.value.queue.count { it.url in urls } == 4 }
                    activity.onActivity { viewModel.dismissMessage() }
                    val waiting = graph.repository.state.value.queue.filter { it.url == urls.elementAt(2) || it.url == sourceItem.url }
                    assertTrue(waiting.all { it.status == DownloadStatus.QUEUED && it.deferredNextStart })
                    assertEquals(2, DownloadService.session.value?.total)
                    activity.onActivity { viewModel.resume() }
                    await { graph.repository.state.value.queue.single { it.id == first.id }.status == DownloadStatus.RUNNING }
                    activity.recreate()
                    assertEquals(session, DownloadService.session.value?.id)
                    activity.onActivity { it.startActivity(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
                    await(10_000) { activity.state == Lifecycle.State.CREATED }
                    activity.onActivity { viewModel.remove(first.id, true) }
                    await(120_000) { DownloadService.session.value?.let { it.id == session && !it.active && it.completed == 1 && it.cancelled == 1 } == true &&
                        !DownloadService.isRunning.value && !DownloadService.hasOwnedTasks }
                    val result = requireNotNull(DownloadService.session.value)
                    assertEquals(2, result.total)
                    assertTrue(result.elapsedMillis >= pausedTime)
                    assertTrue(graph.repository.state.value.queue.none { it.id == first.id })
                    assertTrue(graph.repository.state.value.history.none { it.url == first.url })
                    assertPublished(urls.elementAt(1))
                    assertTrue(waiting.all { before -> graph.repository.state.value.queue.single { it.id == before.id }.deferredNextStart })
                    assertEquals(0, origin.getCount("/${names[2]}.mp4"))
                    assertEquals(0, origin.getCount("/${names[3]}.mp4"))
                    assertEquals("Keep this unrelated file", sentinel.readText())
                    runBlocking(Dispatchers.IO) { delay(700) }
                    assertEquals(result.elapsedMillis, DownloadService.session.value?.elapsedMillis)
                }
                // Opening the app and an explicit Start are required for the imported/source rows.
                ActivityScenario.launch(MainActivity::class.java).use { activity ->
                    val viewModel = model(activity)
                    val ids = graph.repository.state.value.queue.filter { it.url == urls.elementAt(2) || it.url == sourceItem.url }.map { it.id }
                    activity.onActivity { viewModel.start(ids) }
                    await(120_000) { ids.all { id -> graph.repository.state.value.queue.any { it.id == id && it.status == DownloadStatus.COMPLETED } } &&
                        !DownloadService.isRunning.value && !DownloadService.hasOwnedTasks }
                    assertPublished(urls.elementAt(2)); assertPublished(sourceItem.url)
                    assertEquals(EntryState.PROCESSED, graph.repository.state.value.items.single { it.subscriptionId == source.id }.state)
                }
            } finally { cleanup(urls, old, setOf(source.id)); sentinel.delete() }
        }
    }

    @Test fun historyApprovalJoinsTheSameSessionButAnAnswerAfterStopCannotRestartIt() {
        initialize()
        val token = UUID.randomUUID().toString()
        val names = listOf("$token-prior", "$token-running", "$token-stopping")
        val bodies = names.flatMap { name -> listOf("/$name.html" to page(name), "/$name.mp4" to if (name == names.first()) sample() else padded()) }.toMap()
        SessionLoopbackServer(bodies, names.drop(1).associate { "/$it.mp4" to 100L }).use { origin ->
            val urls = names.map { "${origin.baseUrl}/$it.html" }
            val old = graph.repository.state.value.settings
            try {
                val original = DownloadTask(url = urls[0])
                update { it.copy(settings = settings(old, token), queue = it.queue + original) }
                ActivityScenario.launch(MainActivity::class.java).use { activity ->
                    val viewModel = model(activity)
                    activity.onActivity { viewModel.start(listOf(original.id)) }
                    await(90_000) { graph.repository.state.value.queue.single { it.id == original.id }.status == DownloadStatus.COMPLETED &&
                        !DownloadService.isRunning.value && !DownloadService.hasOwnedTasks }
                    val originalUri = graph.repository.state.value.queue.single { it.id == original.id }.outputUris.single()
                    val originalHash = hash(originalUri)
                    activity.onActivity { viewModel.remove(original.id, false) }
                    await { graph.repository.state.value.queue.none { it.id == original.id } }
                    val running = DownloadTask(url = urls[1])
                    update { it.copy(queue = it.queue + running) }
                    activity.onActivity { viewModel.start(listOf(running.id)) }
                    await { DownloadService.progress.value[running.id]?.downloadedBytes?.let { it > 0 } == true }
                    val session = requireNotNull(DownloadService.session.value).id
                    activity.onActivity { viewModel.pause() }
                    await(30_000) { graph.repository.state.value.queue.single { it.id == running.id }.status == DownloadStatus.PAUSED }
                    activity.onActivity { viewModel.addUrls(urls[0]) }
                    await { viewModel.ui.value.duplicatePrompt?.url == urls[0] }
                    compose.onNodeWithText("Нет").performClick()
                    await { viewModel.ui.value.duplicatePrompt == null }
                    val rejected = graph.repository.state.value.queue.single { it.url == urls[0] }
                    assertTrue(rejected.deferredNextStart)
                    assertEquals(1, DownloadService.session.value?.total)
                    activity.onActivity { viewModel.remove(rejected.id, false) }
                    await { graph.repository.state.value.queue.none { it.id == rejected.id } }
                    activity.onActivity { viewModel.addUrls(urls[0]) }
                    await { viewModel.ui.value.duplicatePrompt?.url == urls[0] }
                    compose.onNodeWithText("Да").performClick()
                    await { graph.repository.state.value.queue.any { it.url == urls[0] && it.status == DownloadStatus.PAUSED && !it.deferredNextStart } }
                    assertEquals(session, DownloadService.session.value?.id)
                    assertTrue(DownloadService.session.value?.paused == true)
                    assertEquals(originalHash, hash(originalUri))
                    activity.onActivity { viewModel.resume() }
                    await(120_000) { graph.repository.state.value.history.count { it.url == urls[0] } == 2 && !DownloadService.isRunning.value && !DownloadService.hasOwnedTasks }
                    val repeated = graph.repository.state.value.queue.single { it.url == urls[0] }
                    assertEquals(DownloadStatus.COMPLETED, repeated.status)
                    assertNotEquals("An approved repeat must publish a new file", originalUri, repeated.outputUris.single())
                    assertEquals(originalHash, hash(originalUri))
                    activity.onActivity { viewModel.remove(repeated.id, false) }
                    await { graph.repository.state.value.queue.none { it.id == repeated.id } }
                    val stopping = DownloadTask(url = urls[2])
                    update { it.copy(queue = it.queue + stopping) }
                    activity.onActivity { viewModel.start(listOf(stopping.id)) }
                    await { DownloadService.progress.value[stopping.id]?.downloadedBytes?.let { it > 0 } == true }
                    val stoppingSession = requireNotNull(DownloadService.session.value).id
                    activity.onActivity { viewModel.addUrls(urls[0]) }
                    await { viewModel.ui.value.duplicatePrompt?.url == urls[0] }
                    // A live-input question must not hold the service's pause/stop gate.
                    activity.onActivity { viewModel.stop() }
                    await(30_000) { !DownloadService.isRunning.value && !DownloadService.hasOwnedTasks }
                    assertNotNull(viewModel.ui.value.duplicatePrompt)
                    compose.onNodeWithText("Да").performClick()
                    await { viewModel.ui.value.duplicatePrompt == null }
                    val deferred = graph.repository.state.value.queue.single { it.url == urls[0] }
                    assertTrue(deferred.deferredNextStart)
                    assertEquals(DownloadStatus.QUEUED, deferred.status)
                    assertEquals(stoppingSession, DownloadService.session.value?.id)
                    assertFalse(DownloadService.session.value?.active ?: true)
                    assertFalse(DownloadService.isRunning.value)
                    assertEquals(2, graph.repository.state.value.history.count { it.url == urls[0] })
                    assertTrue(graph.repository.state.value.history.none { it.url == urls[2] })
                    assertEquals(originalHash, hash(originalUri))
                }
            } finally { cleanup(urls.toSet(), old) }
        }
    }

    private fun assertPublished(url: String) {
        val task = graph.repository.state.value.queue.single { it.url == url }
        assertEquals("Download failed: ${task.rawError}", DownloadStatus.COMPLETED, task.status)
        assertEquals(100f, task.progress, 0f)
        assertTrue(task.stageDirectory.isEmpty())
        assertTrue(task.outputUris.isNotEmpty())
        assertTrue(hash(task.outputUris.first()).isNotEmpty())
        assertTrue(graph.repository.state.value.history.any { it.url == url && it.outputUris == task.outputUris })
    }
    private fun hash(uri: String): String = context.contentResolver.openInputStream(Uri.parse(uri))!!.use { input ->
        val hash = MessageDigest.getInstance("SHA-256")
        val bytes = ByteArray(8192)
        while (true) { val count = input.read(bytes); if (count < 0) break; hash.update(bytes, 0, count) }
        hash.digest().joinToString("") { "%02x".format(it) }
    }
    private fun cleanup(urls: Set<String>, settings: Settings, sources: Set<String> = emptySet()) {
        if (DownloadService.isRunning.value) DownloadService.stopAll(context)
        await(30_000) { !DownloadService.isRunning.value && !DownloadService.hasOwnedTasks }
        runBlocking(Dispatchers.IO) {
            val state = graph.repository.state.value
            val tasks = state.queue.filter { it.url in urls }
            val uris = tasks.flatMap { it.outputUris } + state.history.filter { it.url in urls }.flatMap { it.outputUris }
            assertTrue(MediaPublisher(context).deleteOwned(uris.distinct()).isEmpty())
            tasks.filter { it.stageDirectory.isNotBlank() }.forEach { row ->
                val root = File(context.filesDir, "staging").canonicalFile.toPath()
                val stage = File(row.stageDirectory)
                val path = stage.canonicalFile.toPath()
                if (path.startsWith(root) && path != root && path.fileName.toString() == row.id) stage.deleteRecursively()
            }
            graph.repository.update { it.copy(settings = settings, queue = it.queue.filterNot { row -> row.url in urls },
                history = it.history.filterNot { row -> row.url in urls }, subscriptions = it.subscriptions.filterNot { row -> row.id in sources },
                items = it.items.filterNot { row -> row.subscriptionId in sources }) }
        }
    }
}
