package ru.bespalov.videodownloader

import android.content.ClipboardManager
import android.content.Context
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import ru.bespalov.video.core.DownloadStatus
import ru.bespalov.video.core.DownloadTask
import ru.bespalov.video.core.PortableQueue
import ru.bespalov.videodownloader.download.DownloadService
import java.util.UUID

/** Exercises real clipboard UI actions and persistence without starting a download. */
@RunWith(AndroidJUnit4::class)
class PortableQueueInstrumentedTest {
    @get:Rule val compose = createEmptyComposeRule()

    @Test fun copiedQueueCanBePastedWithTitlesOrderAndNoAutostart() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val graph = (context.applicationContext as DownloaderApplication).graph
        runBlocking(Dispatchers.IO) { withTimeout(90_000) { graph.ready.await() } }
        assertTrue(graph.startupError.value, graph.runtimeReady.value)
        val sample = InstrumentationRegistry.getInstrumentation().context.assets.open("offline-sample.mp4").use { it.readBytes() }
        LoopbackMediaServer(sample).use { origin ->
            val token = UUID.randomUUID().toString()
            val rows = listOf(
                DownloadTask(url = "${origin.baseUrl}/portable-$token.mp4?Token=AbC", title = "Первое видео 🎬"),
                DownloadTask(url = "${origin.baseUrl}/portable-$token.mp4?Token=abc", title = "Второе видео"),
            )
            val urls = rows.map { it.url }.toSet()
            val history = graph.repository.state.value.history
            try {
                runBlocking(Dispatchers.IO) { graph.repository.update { it.copy(queue = it.queue + rows) } }
                ActivityScenario.launch(MainActivity::class.java).use { activity ->
                    compose.onNodeWithTag("queueTransfer").performClick()
                    compose.onNodeWithTag("copyQueue").performClick()
                    var transfer = ""
                    compose.waitUntil(15_000) {
                        activity.onActivity {
                            transfer = (it.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager)
                                .primaryClip?.getItemAt(0)?.text?.toString().orEmpty()
                        }
                        transfer.startsWith(PortableQueue.PREFIX)
                    }
                    val exported = PortableQueue.decode(transfer).filter { it.url in urls }
                    assertEquals(rows.map { PortableQueue.Entry(it.url, it.title) }, exported)
                    compose.onNodeWithText("Закрыть").performClick()
                    runBlocking(Dispatchers.IO) { graph.repository.update { state -> state.copy(queue = state.queue.filterNot { it.url in urls }) } }
                    compose.onNodeWithTag("queueTransfer").performClick()
                    compose.onNodeWithTag("pasteQueue").performClick()
                    compose.waitUntil(15_000) { urls.all { url -> graph.repository.state.value.queue.any { it.url == url } } }
                    val imported = graph.repository.state.value.queue.filter { it.url in urls }
                    assertEquals(rows.map { it.url }, imported.map { it.url })
                    assertEquals(rows.map { it.title }, imported.map { it.title })
                    assertTrue(imported.all { it.status == DownloadStatus.QUEUED && it.deferredNextStart && it.outputUris.isEmpty() && it.stageDirectory.isBlank() })
                    assertTrue(imported.all { it.subscriptionId.isEmpty() && it.subscriptionName.isEmpty() && it.settingsSnapshot == null })
                    assertTrue(imported.none { next -> rows.any { old -> next.id == old.id } })
                    assertEquals(history, graph.repository.state.value.history)
                    assertFalse("Pasting a portable queue must wait for Start", DownloadService.isRunning.value)
                }
            } finally {
                runBlocking(Dispatchers.IO) { graph.repository.update { state -> state.copy(queue = state.queue.filterNot { it.url in urls }) } }
            }
        }
    }
}
