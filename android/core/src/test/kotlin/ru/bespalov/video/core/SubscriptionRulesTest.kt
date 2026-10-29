package ru.bespalov.video.core

import org.junit.Assert.*
import org.junit.Test

class SubscriptionRulesTest {
    private val source = Subscription(id = "source", url = "https://example.com/channel")
    private val a = DiscoveredMedia("https://example.com/Video?token=A%2FB", "Видео A")
    private val b = DiscoveredMedia("https://example.com/video?token=A%2FB", "Видео B")

    @Test fun baselineAndNewDetectionRequireSuccessfulFullResult() {
        val initial = AppState(subscriptions = listOf(source))
        val baseline = SubscriptionRules.applyScan(initial, source.id, FullScan("Канал", listOf(a)), 10)
        assertEquals(EntryState.BASELINE, baseline.items.single().state)
        assertTrue(baseline.queue.isEmpty())
        val updated = SubscriptionRules.applyScan(baseline, source.id, FullScan("Канал", listOf(a, b)), 20)
        assertEquals(listOf(EntryState.BASELINE, EntryState.NEW), updated.items.map { it.state })
        assertEquals(listOf(a.url, b.url), updated.items.map { it.url })
        val failed = SubscriptionRules.recordFailure(updated, source.id, "HTTP 403", 30)
        assertEquals(updated.items, failed.items)
        assertEquals(20L, failed.subscriptions.single().lastSuccessUtc)
        assertEquals(30L, failed.subscriptions.single().lastAttemptUtc)
        val disappeared = SubscriptionRules.applyScan(failed, source.id, FullScan("Канал", emptyList()), 40)
        assertEquals(updated.items, disappeared.items)
    }

    @Test fun importQueuesExistingButDoesNotDownloadOrClaimSuccess() {
        val scanned = SubscriptionRules.applyScan(AppState(subscriptions = listOf(source.copy(downloadExisting = true))),
            source.id, FullScan("Канал", listOf(a, b)), 10)
        assertTrue(scanned.items.all { it.state == EntryState.NEW && it.initialQueuePending })
        val keys = scanned.items.map { it.subscriptionId to it.url }.toSet()
        val queued = SubscriptionRules.queueEntries(scanned, keys)
        assertEquals(2, queued.queue.size)
        assertTrue(queued.queue.all { it.status == DownloadStatus.QUEUED && it.deferredNextStart && it.subscriptionName == "Канал" })
        assertTrue(queued.items.all { it.state == EntryState.QUEUED && !it.initialQueuePending })
        assertTrue(queued.history.isEmpty())
        assertEquals(queued.queue, SubscriptionRules.queueEntries(queued, keys).queue)
    }

    @Test fun globalHistoryExcludesFirstBaselineAndFirstImportFromDownloading() {
        for (downloadAll in listOf(false, true)) {
            val state = AppState(subscriptions = listOf(source.copy(downloadExisting = downloadAll)),
                history = listOf(SuccessRecord(a.url, 5)))
            val scanned = SubscriptionRules.applyScan(state, source.id, FullScan("Канал", listOf(a)), 10)
            assertEquals(EntryState.PROCESSED, scanned.items.single().state)
            assertFalse(scanned.items.single().successAttributed)
            assertEquals("", scanned.items.single().duplicateSubscriptionId)
            assertTrue(SubscriptionRules.queueEntries(scanned, setOf(source.id to a.url)).queue.isEmpty())
        }
    }

    @Test fun onlyPublishedSuccessMarksEntriesAndCrossSourceNoticeIsOnce() {
        val other = source.copy(id = "other", url = "https://example.com/other")
        var state = AppState(subscriptions = listOf(source.copy(downloadExisting = true), other.copy(downloadExisting = true)))
        state = SubscriptionRules.applyScan(state, source.id, FullScan("Канал", listOf(a)), 10)
        state = SubscriptionRules.applyScan(state, other.id, FullScan("Другой", listOf(a)), 11)
        state = SubscriptionRules.queueEntries(state, setOf(source.id to a.url))
        val task = state.queue.single()
        val failed = state.copy(queue = listOf(task.copy(status = DownloadStatus.FAILED)))
        assertTrue(failed.history.isEmpty())
        assertEquals(EntryState.NEW, failed.items.single { it.subscriptionId == other.id }.state)
        val done = SubscriptionRules.recordSuccess(state, task.id, listOf("content://downloads/owned/1"), now = 20)
        assertEquals(DownloadStatus.COMPLETED, done.queue.single().status)
        assertEquals(EntryState.PROCESSED, done.items.single { it.subscriptionId == source.id }.state)
        val duplicate = done.items.single { it.subscriptionId == other.id }
        assertEquals(EntryState.ALREADY_PROCESSED, duplicate.state)
        assertFalse(duplicate.duplicateNotified)
        assertEquals(source.id, duplicate.duplicateSubscriptionId)
        val acknowledged = SubscriptionRules.acknowledgeDuplicates(done, setOf(other.id to a.url))
        val checkedAgain = SubscriptionRules.applyScan(acknowledged, other.id, FullScan("Другой", listOf(a)), 30)
        assertTrue(checkedAgain.items.single { it.subscriptionId == other.id }.duplicateNotified)
        assertEquals(listOf(20L), checkedAgain.history.map { it.completedAtUtc })
        val removed = SubscriptionRules.removeSource(checkedAgain, source.id)
        assertEquals(checkedAgain.queue, removed.queue)
        assertEquals(checkedAgain.history, removed.history)
    }

    @Test fun cancelledTaskCannotWinFinalSuccessGate() {
        for (status in listOf(DownloadStatus.CANCELLING, DownloadStatus.CLEANUP_REQUIRED, DownloadStatus.PAUSED)) {
            val task = DownloadTask(id = "task", url = a.url, status = status)
            assertThrows(IllegalStateException::class.java) {
                SubscriptionRules.recordSuccess(AppState(queue = listOf(task)), task.id, listOf("content://owned/1"))
            }
        }
        assertThrows(IllegalArgumentException::class.java) {
            SubscriptionRules.recordSuccess(AppState(queue = listOf(DownloadTask(id = "task", url = a.url))), "task", emptyList())
        }
    }

    @Test fun queueingAnExistingOrdinaryUrlKeepsItsPinnedSource() {
        val state = SubscriptionRules.applyScan(AppState(subscriptions = listOf(source.copy(downloadExisting = true)),
            queue = listOf(DownloadTask(id = "ordinary", url = a.url))), source.id, FullScan("Канал", listOf(a)))
        val queued = SubscriptionRules.queueEntries(state, setOf(source.id to a.url))
        assertEquals(1, queued.queue.size)
        assertEquals("", queued.queue.single().subscriptionId)
        assertEquals("", queued.queue.single().subscriptionName)
        assertEquals(a.title, queued.queue.single().title)
    }
}
