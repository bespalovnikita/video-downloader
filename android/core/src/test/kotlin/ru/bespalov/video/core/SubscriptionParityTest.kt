package ru.bespalov.video.core

import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import java.nio.file.Files

class SubscriptionParityTest {
    private val source = Subscription(id = "source", url = "https://example.com/channel", shortName = "Канал", initialized = true)
    private val other = Subscription(id = "other", url = "https://example.com/other", shortName = "Другой", initialized = true)
    private val a = "https://example.com/Video?Token=A%2FB"
    private val b = "https://example.com/video?Token=A%2FB"
    private val c = "https://example.com/new"

    @Test fun fullScanUsesCurrentOrderAndRetainsHistoricalDecisionsAndOtherSources() {
        val before = SubscriptionEntry(other.id, "https://example.com/before", state = EntryState.SKIPPED, discoveredAtUtc = 1)
        val after = SubscriptionEntry(other.id, "https://example.com/after", discoveredAtUtc = 2)
        val oldA = SubscriptionEntry(source.id, a, "Заголовок A", EntryState.SKIPPED, 3)
        val oldB = SubscriptionEntry(source.id, b, b, EntryState.BASELINE, 4)
        val absent = SubscriptionEntry(source.id, "https://example.com/absent", "Исчезнувшее видео", EntryState.ALREADY_PROCESSED,
            discoveredAtUtc = 5, successAttributed = true, duplicateSubscriptionId = other.id, duplicateNotified = true)
        val queue = listOf(DownloadTask(id = "ordinary", url = a))
        val initial = AppState(subscriptions = listOf(source, other), items = listOf(before, oldA, after, absent, oldB), queue = queue)
        val result = SubscriptionRules.applyScan(initial, source.id, FullScan("Канал", listOf(
            DiscoveredMedia(b, "Заголовок B"), DiscoveredMedia(c, "Новое видео"), DiscoveredMedia(a, "Другое название A"))), 50)

        assertEquals(listOf(b, c, a, absent.url), result.items.filter { it.subscriptionId == source.id }.map { it.url })
        assertEquals(listOf(before, after), result.items.filter { it.subscriptionId == other.id })
        assertEquals(listOf(before.url, b, c, a, absent.url, after.url), result.items.map { it.url })
        assertEquals(oldA, result.items.single { it.url == a })
        assertEquals(oldB.copy(title = "Заголовок B"), result.items.single { it.url == b })
        assertEquals(absent, result.items.single { it.url == absent.url })
        assertEquals(SubscriptionEntry(source.id, c, "Новое видео", discoveredAtUtc = 50), result.items.single { it.url == c })
        assertEquals(queue, result.queue)
        assertTrue(result.history.isEmpty())

        val empty = SubscriptionRules.applyScan(result, source.id, FullScan("Канал", emptyList()), 60)
        assertEquals(result.items, empty.items)
    }

    @Test fun oldVersionOneSnapshotReordersAndQueuesBaselineWithoutResetOrAutomaticStart() = runBlocking {
        val directory = Files.createTempDirectory("vd-subscription-parity").toFile()
        try {
            val file = directory.resolve("state.json")
            file.writeText("""{
                "version": 1,
                "subscriptions": [{"id":"source","url":"https://example.com/channel","shortName":"Канал","initialized":true}],
                "items": [
                    {"subscriptionId":"source","url":"$a","title":"A","state":"BASELINE","discoveredAtUtc":10},
                    {"subscriptionId":"source","url":"$b","title":"B","state":"SKIPPED","discoveredAtUtc":11}
                ]
            }""")
            val repository = JsonStateRepository(file)
            assertFalse(repository.error, repository.readOnly)
            repository.update { SubscriptionRules.applyScan(it, source.id, FullScan("Канал", listOf(
                DiscoveredMedia(b, "B"), DiscoveredMedia(a, "A"))), 100) }
            val queued = repository.update { SubscriptionRules.queueEntries(it, setOf(source.id to a)) }
            assertEquals(listOf(b, a), queued.items.map { it.url })
            assertEquals(EntryState.SKIPPED, queued.items[0].state)
            assertEquals(10L, queued.items[1].discoveredAtUtc)
            assertEquals(EntryState.QUEUED, queued.items[1].state)
            assertTrue(queued.queue.single().deferredNextStart)
            assertEquals(DownloadStatus.QUEUED, queued.queue.single().status)
            assertTrue(queued.history.isEmpty())
            val restored = JsonStateRepository(file)
            assertFalse(restored.error, restored.readOnly)
            assertEquals(queued, restored.state.value)
        } finally { directory.deleteRecursively() }
    }

    @Test fun baselineChangesToQueuedOnlyWhenTheQueueActuallyAcceptsIt() {
        val entry = SubscriptionEntry(source.id, a, "Исходное видео", EntryState.BASELINE, 10)
        val initial = AppState(subscriptions = listOf(source), items = listOf(entry))
        val queued = SubscriptionRules.queueEntries(initial, setOf(source.id to a))
        assertEquals(EntryState.QUEUED, queued.items.single().state)
        assertEquals(a, queued.queue.single().url)
        assertEquals(source.id, queued.queue.single().subscriptionId)
        assertTrue(queued.queue.single().deferredNextStart)
        assertTrue(queued.history.isEmpty())

        for (status in listOf(DownloadStatus.COMPLETED, DownloadStatus.CANCELLING, DownloadStatus.CLEANUP_REQUIRED)) {
            val blocked = initial.copy(queue = listOf(DownloadTask(id = "existing", url = a, status = status)))
            val result = SubscriptionRules.queueEntries(blocked, setOf(source.id to a))
            assertEquals("Blocked by $status", blocked, result)
        }
        val pendingRemoval = initial.copy(queue = listOf(DownloadTask(id = "existing", url = a, pendingRemoval = true)))
        assertEquals(pendingRemoval, SubscriptionRules.queueEntries(pendingRemoval, setOf(source.id to a)))
        val historical = initial.copy(history = listOf(SuccessRecord(a, 20)))
        val processed = SubscriptionRules.queueEntries(historical, setOf(source.id to a))
        assertEquals(EntryState.PROCESSED, processed.items.single().state)
        assertTrue(processed.queue.isEmpty())
    }

    @Test fun aggregateOfferIncludesEarlierNewEntriesOnlyFromSuccessfulAvailableSources() {
        val paused = source.copy(id = "paused", url = "https://example.com/paused", shortName = "Пауза", paused = true)
        val entries = listOf(
            SubscriptionEntry(source.id, a, discoveredAtUtc = 1),
            SubscriptionEntry(source.id, b, discoveredAtUtc = 2),
            SubscriptionEntry(source.id, c, state = EntryState.BASELINE),
            SubscriptionEntry(source.id, "https://example.com/initial", initialQueuePending = true),
            SubscriptionEntry(source.id, "https://example.com/skipped", state = EntryState.SKIPPED),
            SubscriptionEntry(source.id, "https://example.com/queued", state = EntryState.QUEUED),
            SubscriptionEntry(other.id, "https://example.com/failed-source"),
            SubscriptionEntry(paused.id, "https://example.com/paused-video"),
        )
        val initial = AppState(subscriptions = listOf(source, other, paused), items = entries)
        assertEquals(linkedSetOf(source.id to a, source.id to b),
            SubscriptionRules.pendingNewKeys(initial, setOf(source.id, paused.id)))
        assertTrue(SubscriptionRules.pendingNewKeys(initial, emptySet()).isEmpty())
        assertTrue(initial.queue.isEmpty())
        assertEquals(entries, initial.items)

        val withSuccess = initial.copy(history = listOf(SuccessRecord(a, 3)))
        assertEquals(setOf(source.id to b), SubscriptionRules.pendingNewKeys(withSuccess, setOf(source.id)))
    }

    @Test fun acceptingAnOfferRechecksCurrentStateHistorySourceAndOnlyTheOfferedKeys() {
        val initial = AppState(subscriptions = listOf(source, other), items = listOf(
            SubscriptionEntry(source.id, a), SubscriptionEntry(source.id, b), SubscriptionEntry(source.id, c),
            SubscriptionEntry(other.id, "https://example.com/removed"),
        ))
        val offered = SubscriptionRules.pendingNewKeys(initial, setOf(source.id, other.id))
        val changed = SubscriptionRules.removeSource(initial.copy(
            items = initial.items.map { if (it.url == b) it.copy(state = EntryState.SKIPPED) else it } +
                SubscriptionEntry(source.id, "https://example.com/after-offer"),
            history = listOf(SuccessRecord(a, 30, other.id))), other.id)
        val queued = SubscriptionRules.queueNewEntries(changed, offered)

        assertEquals(listOf(c), queued.queue.map { it.url })
        assertTrue(queued.queue.single().deferredNextStart)
        assertEquals(EntryState.ALREADY_PROCESSED, queued.items.single { it.url == a }.state)
        assertEquals(EntryState.SKIPPED, queued.items.single { it.url == b }.state)
        assertEquals(EntryState.QUEUED, queued.items.single { it.url == c }.state)
        assertEquals(EntryState.NEW, queued.items.single { it.url == "https://example.com/after-offer" }.state)
        assertEquals(changed.history, queued.history)

        val paused = initial.copy(subscriptions = listOf(source.copy(paused = true), other))
        assertTrue(SubscriptionRules.queueNewEntries(paused, offered).queue.none { it.subscriptionId == source.id })
    }
}
