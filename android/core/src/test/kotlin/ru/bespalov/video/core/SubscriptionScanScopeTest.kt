package ru.bespalov.video.core

import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import java.nio.file.Files

class SubscriptionScanScopeTest {
    private val source = Subscription(id = "source", url = ScanFixtures.YOUTUBE, title = "Existing channel", shortName = "Channel", initialized = true)
    private val other = Subscription(id = "other", url = "https://example.com/other", shortName = "Other", initialized = true)

    @Test fun recentPrefixPreservesAllEarlierStatesIdentityDiscoveryTimeAndOtherSourceOrder() {
        val states = EntryState.entries
        val saved = states.mapIndexed { index, state -> SubscriptionEntry(source.id, ScanFixtures.watch(index + 1), "Saved $index", state,
            discoveredAtUtc = index.toLong(), successAttributed = state == EntryState.PROCESSED,
            duplicateSubscriptionId = if (state == EntryState.ALREADY_PROCESSED) other.id else "",
            duplicateNotified = state == EntryState.ALREADY_PROCESSED) }
        val beforeOther = SubscriptionEntry(other.id, "https://example.com/before")
        val afterOther = SubscriptionEntry(other.id, "https://example.com/after", state = EntryState.SKIPPED)
        val initial = AppState(subscriptions = listOf(source, other), items = listOf(beforeOther) + saved + afterOther)
        val scan = FullScan("", listOf(DiscoveredMedia(ScanFixtures.watch(90), "New"), DiscoveredMedia(saved[1].url, "Changed title")),
            suggestedName = "Do not replace the folder", scope = ScanScope.RecentWindow)
        val result = SubscriptionRules.applyScan(initial, source.id, scan, 500)
        assertEquals(listOf(beforeOther.url, ScanFixtures.watch(90), saved[1].url) + saved.filterIndexed { index, _ -> index != 1 }.map { it.url } + afterOther.url,
            result.items.map { it.url })
        saved.forEach { assertEquals(it, result.items.single { current -> current.subscriptionId == it.subscriptionId && current.url == it.url }) }
        assertEquals(EntryState.NEW, result.items.single { it.url == ScanFixtures.watch(90) }.state)
        assertEquals(500L, result.items.single { it.url == ScanFixtures.watch(90) }.discoveredAtUtc)
        assertEquals(listOf(beforeOther, afterOther), result.items.filter { it.subscriptionId == other.id })
        assertEquals(source.title, result.subscriptions.first().title)
        assertEquals(source.shortName, result.subscriptions.first().shortName)
        assertEquals(500L, result.subscriptions.first().lastSuccessUtc)
        assertTrue(result.queue.isEmpty())
        assertTrue(result.history.isEmpty())
    }

    @Test fun recentCannotInitializeOrApplyAnUnanchoredOversizedOrInvalidSnapshot() {
        val old = SubscriptionEntry(source.id, ScanFixtures.watch(1), state = EntryState.BASELINE)
        val initial = AppState(subscriptions = listOf(source), items = listOf(old))
        val anchored = FullScan("Window", listOf(DiscoveredMedia(old.url)), scope = ScanScope.RecentWindow)
        assertThrows(IllegalArgumentException::class.java) {
            SubscriptionRules.applyScan(initial.copy(subscriptions = listOf(source.copy(initialized = false))), source.id, anchored)
        }
        assertThrows(IllegalArgumentException::class.java) {
            SubscriptionRules.applyScan(initial, source.id, anchored.copy(items = emptyList()))
        }
        assertThrows(IllegalArgumentException::class.java) {
            SubscriptionRules.applyScan(initial, source.id, anchored.copy(items = listOf(DiscoveredMedia(ScanFixtures.watch(2)))))
        }
        assertThrows(IllegalArgumentException::class.java) {
            SubscriptionRules.applyScan(initial, source.id, anchored.copy(items = (1..21).map { DiscoveredMedia(ScanFixtures.watch(it)) }))
        }
        assertThrows(IllegalArgumentException::class.java) {
            SubscriptionRules.applyScan(initial, source.id, anchored.copy(items = listOf(DiscoveredMedia(old.url), DiscoveredMedia(old.url))))
        }
        assertThrows(IllegalArgumentException::class.java) {
            SubscriptionRules.applyScan(initial, source.id, anchored.copy(items = listOf(DiscoveredMedia(old.url), DiscoveredMedia("file:///secret"))))
        }
        assertEquals(listOf(old), initial.items)
        assertNull(initial.subscriptions.single().lastSuccessUtc)
    }

    @Test fun aVersionOneSnapshotAcceptsRecentWithoutMigrationOrLosingHistoricalDecisions() = runBlocking {
        val directory = Files.createTempDirectory("vd-recent-compatibility").toFile()
        try {
            val file = directory.resolve("state.json")
            file.writeText("""{"version":1,"subscriptions":[{"id":"source","url":"${source.url}","shortName":"Channel","initialized":true}],"items":[
                {"subscriptionId":"source","url":"${ScanFixtures.watch(1)}","title":"Old","state":"SKIPPED","discoveredAtUtc":10},
                {"subscriptionId":"source","url":"${ScanFixtures.watch(2)}","title":"Historical","state":"BASELINE","discoveredAtUtc":20}]}""")
            val repository = JsonStateRepository(file)
            assertFalse(repository.error, repository.readOnly)
            val scan = FullScan("Window", listOf(DiscoveredMedia(ScanFixtures.watch(90), "New"), DiscoveredMedia(ScanFixtures.watch(1))), scope = ScanScope.RecentWindow)
            val updated = repository.update { SubscriptionRules.applyScan(it, source.id, scan, 100) }
            assertEquals(1, updated.version)
            assertEquals(listOf(ScanFixtures.watch(90), ScanFixtures.watch(1), ScanFixtures.watch(2)), updated.items.map { it.url })
            assertEquals(listOf(EntryState.NEW, EntryState.SKIPPED, EntryState.BASELINE), updated.items.map { it.state })
            assertEquals(10L, updated.items[1].discoveredAtUtc)
            val restored = JsonStateRepository(file)
            assertFalse(restored.error, restored.readOnly)
            assertEquals(updated, restored.state.value)
            assertTrue(restored.state.value.queue.isEmpty())
            assertTrue(restored.state.value.history.isEmpty())
        } finally { directory.deleteRecursively() }
    }
}
