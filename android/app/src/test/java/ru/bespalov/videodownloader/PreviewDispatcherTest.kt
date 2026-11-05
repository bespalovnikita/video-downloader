package ru.bespalov.videodownloader

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test
import ru.bespalov.video.core.*
import ru.bespalov.videodownloader.engine.EngineMetadata
import ru.bespalov.videodownloader.engine.PreviewCachePolicy
import java.io.IOException
import java.nio.file.Files

@OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
class PreviewDispatcherTest {
    private class Repository(initial: AppState = AppState()) : StateRepository {
        val current = MutableStateFlow(initial)
        override val state = current.asStateFlow()
        override val readOnly = false
        override val error = ""
        override suspend fun update(transform: (AppState) -> AppState): AppState = transform(current.value).also { current.value = it }
    }

    @Test fun titleIsPublishedBeforeImageAndOneWorkerDeduplicatesActiveRequests() = runTest {
        val folder = Files.createTempDirectory("preview-dispatcher").toFile()
        val image = folder.resolve("cover.jpg").apply { writeBytes(byteArrayOf(1)) }
        val url = "https://example.com/video"
        val repository = Repository(AppState(queue = listOf(DownloadTask(url = url))))
        val scope = CoroutineScope(SupervisorJob() + StandardTestDispatcher(testScheduler))
        var calls = 0
        var active = 0
        var maximum = 0
        val worker = PreviewDispatcher(PreviewLoader { _, address, _, _, update ->
            calls++; active++; maximum = maxOf(maximum, active)
            update(EngineMetadata(address, title = "Actual title"))
            delay(100)
            active--
            EngineMetadata(address, title = "Actual title", images = listOf(image), savedAtMillis = 1_000)
        }, repository, scope, {}, { 1_000 })
        try {
            worker.request(url)
            runCurrent()
            assertEquals("Actual title", repository.state.value.queue.single().title)
            assertTrue(worker.metadata.value[url]!!.images.isEmpty())
            repeat(20) { worker.request(url, priority = true) }
            worker.request("https://example.com/other")
            advanceUntilIdle()
            assertEquals(2, calls)
            assertEquals(1, maximum)
            assertEquals(listOf(image), worker.metadata.value[url]!!.images)
            assertTrue(worker.activeUrls.value.isEmpty())
            worker.request(url)
            advanceUntilIdle()
            assertEquals("A ready cache must not load or fetch another image", 2, calls)
        } finally { scope.cancel(); folder.deleteRecursively() }
    }

    @Test fun failedEmptyMetadataRetriesOnlyOnNextRequestAfterOneMinuteThenCachesRealAbsence() = runTest {
        val scope = CoroutineScope(SupervisorJob() + StandardTestDispatcher(testScheduler))
        var now = 1_000L
        var calls = 0
        val records = mutableListOf<JournalRecord>()
        val url = "https://example.com/video?v=Public&token=private"
        val worker = PreviewDispatcher(PreviewLoader { _, address, _, _, _ ->
            calls++
            if (calls == 1) throw IOException("HTTP Error 503: unavailable")
            EngineMetadata(address, title = "No image candidates", savedAtMillis = now)
        }, Repository(), scope, records::add, { now })
        try {
            worker.request(url, "Known title")
            advanceUntilIdle()
            assertEquals(1, calls)
            assertEquals("Known title", worker.metadata.value[url]!!.title)
            assertTrue(worker.errors.value[url]!!.contains("503"))
            now += PreviewCachePolicy.FAILURE_RETRY_MILLIS - 1
            worker.request(url)
            advanceUntilIdle()
            assertEquals(1, calls)
            now++
            advanceTimeBy(120_000)
            runCurrent()
            assertEquals("There is no automatic retry timer", 1, calls)
            worker.request(url)
            advanceUntilIdle()
            assertEquals(2, calls)
            assertTrue(worker.errors.value.isEmpty())
            now += PreviewCachePolicy.FAILURE_RETRY_MILLIS
            worker.request(url)
            advanceUntilIdle()
            assertEquals("An actual result without candidates has the ready lifetime", 2, calls)
            val journal = DiagnosticJournal()
            records.forEach(journal::write)
            assertTrue(journal.snapshotText().contains("stage=metadata"))
            assertTrue(journal.snapshotText().contains("v=Public"))
            assertFalse(journal.snapshotText().contains("private"))
        } finally { scope.cancel() }
    }

    @Test fun aLaterFailurePreservesUsableImageButNeverKeepsADeletedFile() = runTest {
        val folder = Files.createTempDirectory("preview-retained").toFile()
        val image = folder.resolve("cover.jpg").apply { writeBytes(byteArrayOf(1)) }
        val scope = CoroutineScope(SupervisorJob() + StandardTestDispatcher(testScheduler))
        var now = 1_000L
        var calls = 0
        val url = "https://example.com/video"
        val worker = PreviewDispatcher(PreviewLoader { _, address, _, _, _ ->
            calls++
            when (calls) {
                1 -> EngineMetadata(address, title = "Old title", images = listOf(image), savedAtMillis = now)
                2 -> throw IOException("Image request failed")
                else -> EngineMetadata(address, savedAtMillis = now)
            }
        }, Repository(), scope, {}, { now })
        try {
            worker.request(url); advanceUntilIdle()
            now += PreviewCachePolicy.READY_TTL_MILLIS
            worker.request(url); advanceUntilIdle()
            assertEquals(listOf(image), worker.metadata.value[url]!!.images)
            assertEquals("Old title", worker.metadata.value[url]!!.title)
            assertTrue(worker.errors.value[url]!!.isNotBlank())
            worker.request(url); advanceUntilIdle()
            assertEquals(2, calls)
            assertTrue(image.delete())
            worker.request(url); advanceUntilIdle()
            assertEquals(3, calls)
            assertTrue(worker.metadata.value[url]!!.images.isEmpty())
            assertEquals("Old title", worker.metadata.value[url]!!.title)
        } finally { scope.cancel(); folder.deleteRecursively() }
    }

    @Test fun readdedQueueAndRestoredSubscriptionRowsReceiveTitlesEvenAfterCacheEviction() = runTest {
        val scope = CoroutineScope(SupervisorJob() + StandardTestDispatcher(testScheduler))
        val repository = Repository()
        val url = "https://example.com/again"
        val worker = PreviewDispatcher(PreviewLoader { _, address, _, _, _ ->
            EngineMetadata(address, title = "Site title", savedAtMillis = 1_000)
        }, repository, scope, {}, { 1_000 })
        try {
            worker.request(url); advanceUntilIdle()
            repeat(129) { worker.request("https://example.com/$it") }
            advanceUntilIdle()
            assertNull(worker.metadata.value[url])
            assertEquals(128, worker.metadata.value.size)
            repository.current.value = AppState(queue = listOf(DownloadTask(url = url)),
                subscriptions = listOf(Subscription(id = "restored", url = "https://example.com/source")),
                items = listOf(SubscriptionEntry("restored", url)))
            worker.request(url); advanceUntilIdle()
            assertEquals("Site title", repository.state.value.queue.single().title)
            assertEquals("Site title", repository.state.value.items.single().title)
        } finally { scope.cancel() }
    }

    @Test fun cacheLifetimesHandleBoundariesClockRollbackAndExistingImages() {
        val empty = EngineMetadata("https://example.com/video", savedAtMillis = 1_000)
        val failure = empty.copy(previewError = "HTTP 503")
        assertTrue(PreviewCachePolicy.isFresh(empty, 1_000 + PreviewCachePolicy.READY_TTL_MILLIS - 1))
        assertFalse(PreviewCachePolicy.isFresh(empty, 1_000 + PreviewCachePolicy.READY_TTL_MILLIS))
        assertTrue(PreviewCachePolicy.isFresh(failure, 60_999))
        assertFalse(PreviewCachePolicy.isFresh(failure, 61_000))
        assertFalse(PreviewCachePolicy.isFresh(empty, 999))
        assertTrue(PreviewCachePolicy.isFresh(failure.copy(images = listOf(java.io.File("existing-cover"))), 61_000))
    }
}
