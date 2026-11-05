package ru.bespalov.videodownloader

import android.content.Context
import android.net.Uri
import android.system.Os
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.assertIsOff
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
import androidx.test.uiautomator.UiDevice
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.encodeToString
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
import java.io.File
import java.util.UUID

/** Reopening a committed snapshot and a real private-directory denial preserve published media. */
@RunWith(AndroidJUnit4::class)
class PublicationCleanupInstrumentedTest {
    @get:Rule val compose = createEmptyComposeRule()
    private val context: Context get() = ApplicationProvider.getApplicationContext()
    private val graph: AppGraph get() = (context.applicationContext as DownloaderApplication).graph

    private fun await(predicate: () -> Boolean) = runBlocking(Dispatchers.IO) {
        withTimeout(30_000) { while (!predicate()) delay(50) }
    }

    @Test fun recoveredSuccessfulPublicationCanRetryPrivateCleanupWithoutDeletingMedia() {
        runBlocking(Dispatchers.IO) { withTimeout(90_000) { graph.ready.await() } }
        assertTrue(graph.startupError.value, graph.runtimeReady.value)
        assertFalse(graph.repository.error, graph.repository.readOnly)
        await { !DownloadService.isRunning.value && !DownloadService.hasOwnedTasks }
        val token = UUID.randomUUID().toString()
        val run = UUID.randomUUID().toString()
        val task = DownloadTask(url = "http://127.0.0.1:9/cleanup-$token.mp4", title = "Private cleanup $token", runId = run)
        val stage = File(context.filesDir, "staging/$run/${task.id}").apply { mkdirs() }
        val media = File(stage, "cleanup-$token.mp4")
        val reopenedDirectory = File(context.cacheDir, "cleanup-reopen-$token").apply { mkdirs() }
        val sample = InstrumentationRegistry.getInstrumentation().context.assets.open("offline-sample.mp4").use { it.readBytes() }
        media.writeBytes(sample)
        val published = mutableListOf<String>()
        try {
            runBlocking(Dispatchers.IO) {
                graph.repository.update { it.copy(queue = it.queue + task.copy(status = DownloadStatus.RUNNING,
                    stageDirectory = stage.absolutePath)) }
                val batch = MediaPublisher(context).publish(stage, listOf(media), media, Settings(), "", "", { false }) { uris ->
                    published.clear()
                    published.addAll(uris)
                    graph.repository.update { state -> state.copy(queue = state.queue.map {
                        if (it.id == task.id) it.copy(outputUris = uris) else it
                    }) }
                }
                graph.repository.update { state ->
                    val success = SubscriptionRules.recordSuccess(state, task.id, batch.uris, task.title)
                    success.copy(queue = success.queue.map {
                        if (it.id == task.id) it.copy(stageDirectory = stage.absolutePath, runId = run) else it
                    })
                }
                // This is the durable state at process death after the success transaction
                // and before private cleanup, reopened through the production repository.
                val checkpoint = File(reopenedDirectory, "state.json")
                checkpoint.writeText(JsonStateRepository.JSON.encodeToString(graph.repository.state.value))
                val reopened = JsonStateRepository(checkpoint)
                assertFalse(reopened.error, reopened.readOnly)
                val recovered = reopened.state.value.queue.single { it.id == task.id }
                assertEquals(DownloadStatus.CLEANUP_REQUIRED, recovered.status)
                graph.repository.update { state -> state.copy(queue = state.queue.map { if (it.id == task.id) recovered else it }) }
            }
            val history = graph.repository.state.value.history.filter { it.url == task.url }
            assertEquals(1, history.size)
            assertEquals(published, history.single().outputUris)
            // Removing a child requires write permission on its directory. The app
            // still owns this real stage, but cannot delete it until access is restored.
            Os.chmod(stage.absolutePath, 320) // 0500
            val device = UiDevice.getInstance(InstrumentationRegistry.getInstrumentation())
            device.wakeUp()
            device.executeShellCommand("wm dismiss-keyguard")
            ActivityScenario.launch(MainActivity::class.java).use {
                openCleanup(task.id)
                compose.onNodeWithTag("deleteTaskFiles:${task.id}").assertIsOff().assertIsEnabled()
                compose.onNodeWithTag("confirmTaskRemoval:${task.id}").performClick()
                await {
                    !DownloadService.isRunning.value && !DownloadService.hasOwnedTasks &&
                        graph.repository.state.value.queue.any { row -> row.id == task.id && row.pendingRemoval }
                }
                val retained = graph.repository.state.value.queue.single { row -> row.id == task.id }
                assertEquals(DownloadStatus.CLEANUP_REQUIRED, retained.status)
                assertEquals("A failed private cleanup must preserve the successful output manifest", published, retained.outputUris)
                assertEquals(history, graph.repository.state.value.history.filter { row -> row.url == task.url })
                assertPublished(sample, published)

                Os.chmod(stage.absolutePath, 448) // 0700
                openCleanup(task.id)
                compose.onNodeWithTag("deleteTaskFiles:${task.id}").assertIsOff().assertIsEnabled()
                compose.onNodeWithTag("confirmTaskRemoval:${task.id}").performClick()
                await {
                    !DownloadService.isRunning.value && !DownloadService.hasOwnedTasks &&
                        graph.repository.state.value.queue.none { row -> row.id == task.id }
                }
                assertFalse("The restored private directory must be cleaned", stage.exists())
                assertPublished(sample, published)
                assertEquals("Cleaning staging must not change successful history", history,
                    graph.repository.state.value.history.filter { row -> row.url == task.url })
            }
        } finally {
            if (stage.exists()) Os.chmod(stage.absolutePath, 448)
            await { !DownloadService.isRunning.value && !DownloadService.hasOwnedTasks }
            runBlocking(Dispatchers.IO) {
                assertTrue(MediaPublisher(context).deleteOwned(published).isEmpty())
                graph.repository.update { state -> state.copy(queue = state.queue.filterNot { it.id == task.id },
                    history = state.history.filterNot { it.url == task.url }) }
            }
            stage.parentFile?.deleteRecursively()
            reopenedDirectory.deleteRecursively()
        }
    }

    private fun openCleanup(id: String) {
        compose.onNodeWithTag("queueList").performScrollToNode(hasTestTag("queueRow:$id"))
        compose.onNodeWithTag("taskActions:$id").assertIsDisplayed().performClick()
        compose.onNodeWithText("Очистить временные файлы").performClick()
    }

    private fun assertPublished(expected: ByteArray, uris: List<String>) {
        assertEquals(1, uris.size)
        val actual = context.contentResolver.openInputStream(Uri.parse(uris.single()))!!.use { it.readBytes() }
        assertArrayEquals("The successful published file must remain readable and unchanged", expected, actual)
    }
}
