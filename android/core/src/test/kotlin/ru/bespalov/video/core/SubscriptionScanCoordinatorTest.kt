package ru.bespalov.video.core

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Job
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.*
import org.junit.Assert.*
import org.junit.Test
import ru.bespalov.video.core.ScanFixtures.changed
import java.io.IOException

@OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
class SubscriptionScanCoordinatorTest {
    private val source = Subscription(id = "source", url = ScanFixtures.YOUTUBE, shortName = "Channel", initialized = true)
    private val noFeed: suspend (String, Long) -> String = { _, _ -> error("Unexpected feed request") }

    @Test fun aVerifiedKnownWindowIsEnoughAndDoesNotEnumerateTheArchive() = runTest {
        val calls = mutableListOf<Pair<ScanScope, Long>>()
        val stages = mutableListOf<String>()
        val known = (1..100).map(ScanFixtures::watch).toSet()
        val scan = SubscriptionScanCoordinator().scan(source.url, initialized = true, knownUrls = known,
            enumerate = { scope, timeout -> calls += scope to timeout; ScanMetadata(ScanFixtures.recent((1..20).toList()).toString()) },
            readFeed = noFeed, onProgress = stages::add)
        assertEquals(listOf(ScanScope.RecentWindow to 30_000L), calls)
        assertEquals(ScanScope.RecentWindow, scan.scope)
        assertEquals((1..20).map(ScanFixtures::watch), scan.items.map { it.url })
        assertEquals("RecentWindow", stages.first())
        assertEquals("Ready", stages.last())
        assertFalse(stages.contains("Enumerating"))
    }

    @Test fun firstForcedEmptyKnownAndUnorderedSourcesAlwaysUseTheFullAdapter() = runTest {
        data class Case(val url: String, val initialized: Boolean = true, val known: Set<String> = setOf(ScanFixtures.watch(1)), val force: Boolean = false)
        val cases = listOf(Case(source.url, initialized = false), Case(source.url, force = true), Case(source.url, known = emptySet()),
            Case("https://www.youtube.com/@fixture"), Case("https://www.youtube.com/playlist?list=PL1"), Case("https://example.com/collection"))
        for (case in cases) {
            val calls = mutableListOf<ScanScope>()
            val scan = SubscriptionScanCoordinator().scan(case.url, initialized = case.initialized, knownUrls = case.known, forceFull = case.force,
                enumerate = { scope, _ -> calls += scope; ScanMetadata(ScanFixtures.full(listOf(1, 2)).toString()) }, readFeed = noFeed)
            assertEquals(case.toString(), listOf(ScanScope.Full), calls)
            assertEquals(ScanScope.Full, scan.scope)
        }
        val calls = mutableListOf<ScanScope>()
        SubscriptionScanCoordinator(SubscriptionScanLimits(maxItems = 19)).scan(source.url, initialized = true, knownUrls = setOf(ScanFixtures.watch(1)),
            enumerate = { scope, _ -> calls += scope; ScanMetadata(ScanFixtures.full(listOf(1)).toString()) }, readFeed = noFeed)
        assertEquals(listOf(ScanScope.Full), calls)
    }

    @Test fun oneHundredNewAboveOneHundredKnownForceAFullScanAndAllNewCanBeQueued() = runTest {
        val oldIndices = (1..100).toList()
        val newIndices = (101..200).toList()
        val old = oldIndices.map { index -> SubscriptionEntry(source.id, ScanFixtures.watch(index), "Saved $index",
            if (index % 2 == 0) EntryState.PROCESSED else EntryState.SKIPPED, discoveredAtUtc = index.toLong()) }
        val before = AppState(subscriptions = listOf(source), items = old)
        val calls = mutableListOf<ScanScope>()
        val scan = SubscriptionScanCoordinator().scan(source.url, initialized = true, knownUrls = old.map { it.url }.toSet(),
            enumerate = { scope, _ -> calls += scope; ScanMetadata(when (scope) {
                ScanScope.RecentWindow -> ScanFixtures.recent(newIndices.take(20)).toString()
                ScanScope.Full -> ScanFixtures.full(newIndices + oldIndices).toString()
            }) }, readFeed = noFeed)
        assertEquals(listOf(ScanScope.RecentWindow, ScanScope.Full), calls)
        assertEquals(ScanScope.Full, scan.scope)
        assertEquals(200, scan.items.size)
        val after = SubscriptionRules.applyScan(before, source.id, scan, 999)
        assertEquals(newIndices.map(ScanFixtures::watch) + oldIndices.map(ScanFixtures::watch), after.items.map { it.url })
        assertEquals(old, after.items.takeLast(100))
        assertEquals(100, after.items.count { it.state == EntryState.NEW })
        val accepted = SubscriptionRules.queueEntries(after, after.items.filter { it.state == EntryState.NEW }.map { it.subscriptionId to it.url }.toSet())
        assertEquals(100, accepted.queue.size)
        assertTrue(accepted.queue.all { it.deferredNextStart && it.status == DownloadStatus.QUEUED })
        assertEquals(2, calls.size)
        assertTrue(accepted.history.isEmpty())
    }

    @Test fun malformedProbeAndCaseDistinctOrAliasedAnchorsFallBackRatherThanLoseContent() = runTest {
        val badProbe = listOf("not json", ScanFixtures.recent((1..20).toList()).changed("incomplete", JsonPrimitive(true)).toString(),
            ScanFixtures.recent((101..120).toList()).toString())
        for (probe in badProbe) {
            val calls = mutableListOf<ScanScope>()
            val scan = SubscriptionScanCoordinator().scan(source.url, initialized = true, knownUrls = setOf(ScanFixtures.watch(1)),
                enumerate = { scope, _ -> calls += scope; ScanMetadata(if (scope == ScanScope.RecentWindow) probe else ScanFixtures.full(listOf(1, 2)).toString()) },
                readFeed = noFeed)
            assertEquals(listOf(ScanScope.RecentWindow, ScanScope.Full), calls)
            assertEquals(ScanScope.Full, scan.scope)
        }
        val savedAlias = ScanFixtures.watch(1).replace("www.youtube.com", "m.youtube.com")
        val calls = mutableListOf<ScanScope>()
        val scan = SubscriptionScanCoordinator().scan(source.url, initialized = true, knownUrls = setOf(savedAlias),
            enumerate = { scope, _ -> calls += scope; ScanMetadata(if (scope == ScanScope.RecentWindow) ScanFixtures.recent(listOf(1)).toString() else ScanFixtures.full(listOf(1)).toString()) },
            readFeed = noFeed)
        assertEquals(listOf(ScanScope.RecentWindow, ScanScope.Full), calls)
        val state = AppState(subscriptions = listOf(source), items = listOf(SubscriptionEntry(source.id, savedAlias, state = EntryState.SKIPPED)))
        val updated = SubscriptionRules.applyScan(state, source.id, scan)
        assertEquals(listOf(ScanFixtures.watch(1), savedAlias), updated.items.map { it.url })
        assertEquals(EntryState.SKIPPED, updated.items.last().state)
    }

    @Test fun probeTimeoutLeavesOnlyTheRemainingBudgetForTheFullFallback() = runTest {
        val limits = SubscriptionScanLimits(scanTimeoutMillis = 100, recentTimeoutMillis = 10)
        val calls = mutableListOf<ScanScope>()
        val start = testScheduler.currentTime
        val scan = SubscriptionScanCoordinator(limits).scan(source.url, initialized = true, knownUrls = setOf(ScanFixtures.watch(1)),
            enumerate = { scope, _ -> calls += scope; delay(if (scope == ScanScope.RecentWindow) 20 else 5); ScanMetadata(ScanFixtures.full(listOf(1)).toString()) },
            readFeed = noFeed)
        assertEquals(ScanScope.Full, scan.scope)
        assertEquals(15L, testScheduler.currentTime - start)
        assertEquals(listOf(ScanScope.RecentWindow, ScanScope.Full), calls)

        val deadlineStart = testScheduler.currentTime
        val failed = runCatching {
            SubscriptionScanCoordinator(limits).scan(source.url, initialized = true, knownUrls = setOf(ScanFixtures.watch(1)),
                enumerate = { _, _ -> delay(150); ScanMetadata(ScanFixtures.full(listOf(1)).toString()) }, readFeed = noFeed)
        }.exceptionOrNull()
        assertTrue("An own total deadline must become a source failure, not user cancellation", failed is ScanTimeoutException)
        assertEquals(100L, testScheduler.currentTime - deadlineStart)
    }

    @Test fun nativeUserAndCallerDeadlineCancellationNeverBeginFallback() = runTest {
        val calls = mutableListOf<ScanScope>()
        val native = runCatching {
            SubscriptionScanCoordinator().scan(source.url, initialized = true, knownUrls = setOf(ScanFixtures.watch(1)),
                enumerate = { scope, _ -> calls += scope; throw CancellationException("Native request cancelled") }, readFeed = noFeed)
        }.exceptionOrNull()
        assertTrue(native is CancellationException)
        assertEquals(listOf(ScanScope.RecentWindow), calls)

        calls.clear()
        val started = CompletableDeferred<Unit>()
        val user = launch {
            SubscriptionScanCoordinator().scan(source.url, initialized = true, knownUrls = setOf(ScanFixtures.watch(1)),
                enumerate = { scope, _ -> calls += scope; started.complete(Unit); awaitCancellation() }, readFeed = noFeed)
        }
        started.await(); user.cancelAndJoin()
        assertEquals(listOf(ScanScope.RecentWindow), calls)

        calls.clear()
        val caller = runCatching {
            withTimeout(5) {
                SubscriptionScanCoordinator(SubscriptionScanLimits(scanTimeoutMillis = 100, recentTimeoutMillis = 30)).scan(source.url,
                    initialized = true, knownUrls = setOf(ScanFixtures.watch(1)),
                    enumerate = { scope, _ -> calls += scope; delay(20); ScanMetadata(ScanFixtures.recent(listOf(1)).toString()) }, readFeed = noFeed)
            }
        }.exceptionOrNull()
        assertTrue("Caller cancellation must not turn into a recorded source timeout", caller is TimeoutCancellationException)
        assertEquals(listOf(ScanScope.RecentWindow), calls)
    }

    @Test fun failedFullFallbackPreservesThePreviousSnapshotAndSuccessTime() = runTest {
        val before = AppState(subscriptions = listOf(source.copy(lastSuccessUtc = 42)),
            items = listOf(SubscriptionEntry(source.id, ScanFixtures.watch(1), state = EntryState.SKIPPED, discoveredAtUtc = 7)))
        val failure = runCatching {
            SubscriptionScanCoordinator().scan(source.url, initialized = true, knownUrls = before.items.map { it.url }.toSet(),
                enumerate = { scope, _ -> if (scope == ScanScope.RecentWindow) ScanMetadata(ScanFixtures.recent(listOf(101)).toString())
                    else ScanMetadata(ScanFixtures.full(listOf(1)).toString(), "ERROR: incomplete collection") }, readFeed = noFeed)
        }.exceptionOrNull()
        assertTrue(failure is IOException)
        val failed = SubscriptionRules.recordFailure(before, source.id, failure!!.message.orEmpty(), 100)
        assertEquals(before.items, failed.items)
        assertEquals(before.history, failed.history)
        assertEquals(42L, failed.subscriptions.single().lastSuccessUtc)
        assertEquals(100L, failed.subscriptions.single().lastAttemptUtc)
        assertTrue(failed.subscriptions.single().initialized)
    }

    @Test fun rssOnlyEnrichesConfirmedExactUrlsAndItsFailureDoesNotInvalidateDiscovery() = runTest {
        val recent = ScanFixtures.recent(listOf(1, 2))
        val rows = (recent.getValue("entries") as JsonArray).map { (it as JsonObject).changed("title", JsonPrimitive("")) }
        val json = recent.changed("entries", JsonArray(rows)).toString()
        val feedUrl = "https://example.com/feed"
        val xml = "<rss><channel><item><title>From RSS</title><link>${ScanFixtures.watch(1)}</link></item>" +
            "<item><title>Wrong host casing</title><link>${ScanFixtures.watch(2).replace("www.youtube.com", "WWW.youtube.com")}</link></item>" +
            "<item><title>Feed only</title><link>${ScanFixtures.watch(90)}</link></item></channel></rss>"
        val scan = SubscriptionScanCoordinator().scan(source.url, feedUrl, true, setOf(ScanFixtures.watch(1)),
            enumerate = { _, _ -> ScanMetadata(json) }, readFeed = { address, _ -> assertEquals(feedUrl, address); xml })
        assertEquals(ScanScope.RecentWindow, scan.scope)
        assertEquals(listOf(ScanFixtures.watch(1), ScanFixtures.watch(2)), scan.items.map { it.url })
        assertEquals(listOf("From RSS", ScanFixtures.watch(2)), scan.items.map { it.title })
        assertTrue(scan.warning.contains("2"))
        val warning = SubscriptionScanCoordinator().scan(source.url, feedUrl, true, setOf(ScanFixtures.watch(1)),
            enumerate = { _, _ -> ScanMetadata(json) }, readFeed = { _, _ -> throw IOException("HTTP Error 403") })
        assertEquals(scan.items.map { it.url }, warning.items.map { it.url })
        assertEquals(ScanScope.RecentWindow, warning.scope)
        assertTrue(warning.warning.contains("Лента недоступна"))
    }

    @Test fun boundedPayloadsAndObserversCannotProduceAFalseSuccessfulScan() = runTest {
        val oversized = runCatching {
            SubscriptionScanCoordinator(SubscriptionScanLimits(maxMetadataBytes = 10)).scan("https://example.com/source",
                enumerate = { _, _ -> ScanMetadata(ScanFixtures.full(listOf(1)).toString()) }, readFeed = noFeed)
        }.exceptionOrNull()
        assertTrue(oversized is IllegalArgumentException)
        val observer = SubscriptionScanCoordinator().scan("https://example.com/source",
            enumerate = { _, _ -> ScanMetadata(ScanFixtures.full(listOf(1)).toString()) }, readFeed = noFeed,
            onProgress = { error("UI observer failed") })
        assertEquals(ScanScope.Full, observer.scope)
        val afterReady = async {
            val job = currentCoroutineContext()[Job]!!
            SubscriptionScanCoordinator().scan("https://example.com/source",
                enumerate = { _, _ -> ScanMetadata(ScanFixtures.full(listOf(1)).toString()) }, readFeed = noFeed,
                onProgress = { stage -> if (stage == "Ready") job.cancel(CancellationException("User cancelled at Ready")) })
        }
        val failure = runCatching { afterReady.await() }.exceptionOrNull()
        assertTrue(failure is CancellationException)
    }
}
