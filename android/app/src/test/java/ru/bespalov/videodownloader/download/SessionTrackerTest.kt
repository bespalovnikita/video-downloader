package ru.bespalov.videodownloader.download

import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

class SessionTrackerTest {
    private fun member(id: String, sequence: Int = 1) = SessionMember(id, sequence, "Видео $id", "https://example.com/$id")
    private fun SessionTracker.current() = requireNotNull(snapshot())

    @Test fun clockIncludesPauseIsMonotonicAndFreezesOnlyAfterFinish() {
        var now = 1_000L
        val tracker = SessionTracker { now }
        assertNull(tracker.snapshot())
        tracker.begin("session", listOf(member("a")))
        now = 1_500
        assertEquals(500L, tracker.current().elapsedMillis)
        tracker.setPaused("session", true)
        tracker.mark("session", "a", SessionJobState.PAUSED)
        now = 4_500
        assertEquals(3_500L, tracker.current().elapsedMillis)
        assertTrue(tracker.current().paused)
        assertEquals(1, tracker.current().queued)
        now = 2_000
        assertEquals("A clock rollback must not shorten an observed session", 3_500L, tracker.current().elapsedMillis)
        tracker.setPaused("session", false)
        tracker.mark("session", "a", SessionJobState.RUNNING)
        now = 5_500
        assertEquals(4_500L, tracker.current().elapsedMillis)
        tracker.mark("session", "a", SessionJobState.COMPLETED)
        now = 6_100
        tracker.finish("session")
        val finished = tracker.current()
        assertEquals(5_100L, finished.elapsedMillis)
        assertFalse(finished.active)
        assertFalse(finished.paused)
        now = 99_000
        assertEquals(finished, tracker.current())
    }

    @Test fun fourJobsRemainInOverallAccountingAfterTheyFinishOrFail() {
        val tracker = SessionTracker { 0 }
        tracker.begin("session", listOf(member("a", 1), member("b", 2), member("c", 3), member("d", 4)))
        tracker.observe("session", "a", "a-1", 100f, 100, attempt = 1)
        tracker.mark("session", "a", SessionJobState.COMPLETED)
        tracker.observe("session", "b", "b-1", 40f, 50, title = "Новое название", attempt = 2)
        tracker.mark("session", "b", SessionJobState.RUNNING)
        tracker.observe("session", "c", "c-1", 25f, 30, attempt = 1)
        tracker.mark("session", "c", SessionJobState.FAILED)
        tracker.observe("session", "d", "d-1", 5f, 20, attempt = 1)
        tracker.mark("session", "d", SessionJobState.CANCELLED)

        val snapshot = tracker.current()
        assertEquals(4, snapshot.total)
        assertEquals(0, snapshot.queued)
        assertEquals(1, snapshot.running)
        assertEquals(1, snapshot.completed)
        assertEquals(1, snapshot.failed)
        assertEquals(1, snapshot.cancelled)
        assertEquals(42.5f, snapshot.overallPercent, 0f)
        assertEquals(200L, snapshot.transferredBytes)
        assertEquals(listOf(1, 2, 3, 4), snapshot.members.map { it.sequence })
        assertEquals("Новое название", snapshot.members[1].title)
        assertEquals(2, snapshot.members[1].attempt)
        assertEquals(listOf(100f, 40f, 25f, 5f), snapshot.members.map { it.percent })
    }

    @Test fun bytesUsePerInvocationAbsoluteCountersWithoutCountingProgressAndResultTwice() {
        val tracker = SessionTracker { 0 }
        tracker.begin("session", listOf(member("a")))
        tracker.observe("session", "a", "first", 20f, 100, attempt = 1)
        tracker.observe("session", "a", "first", 20f, 100, attempt = 1)
        tracker.observe("session", "a", "first", 15f, 80, attempt = 1)
        assertEquals(100L, tracker.transferred("a"))
        tracker.observe("session", "a", "first", 40f, 140, attempt = 2)
        tracker.observe("session", "a", "resume", 5f, 20, attempt = 3)
        assertEquals(160L, tracker.transferred("a"))
        tracker.observe("session", "a", "first", Float.NaN, 150, attempt = 2)
        tracker.observe("session", "a", "first", Float.NaN, 150, attempt = 2)
        tracker.observe("session", "a", "resume", Float.NaN, -1, attempt = -1)
        assertEquals(170L, tracker.transferred("a"))
        assertEquals(170L, tracker.current().transferredBytes)
        assertEquals(3, tracker.attempts("a"))
        assertEquals(0L, tracker.transferred("missing"))
        assertEquals(0, tracker.attempts("missing"))
    }

    @Test fun progressAllowsRealAttemptResetsPreservesUnknownAndReservesOneHundredForPublication() {
        val tracker = SessionTracker { 0 }
        tracker.begin("session", listOf(member("a")))
        tracker.observe("session", "a", "native", 60f, 10)
        tracker.observe("session", "a", "native", 10f, 20)
        assertEquals(10f, tracker.current().members.single().percent, 0f)
        for (unknown in listOf(Float.NaN, Float.POSITIVE_INFINITY, Float.NEGATIVE_INFINITY, -1f)) {
            tracker.observe("session", "a", "native", unknown, 20, title = "")
            assertEquals(10f, tracker.current().members.single().percent, 0f)
        }
        tracker.observe("session", "a", "native", 100f, 30)
        assertEquals(99f, tracker.current().overallPercent, 0f)
        tracker.mark("session", "a", SessionJobState.COMPLETED)
        val published = tracker.current()
        assertEquals(100f, published.overallPercent, 0f)
        tracker.observe("session", "a", "late", 0f, 1_000, title = "Запоздалое название", attempt = 100)
        tracker.mark("session", "a", SessionJobState.FAILED)
        assertEquals(published, tracker.current())
    }

    @Test fun wrongSessionAndFinishedCallbacksAreIgnoredAndBeginResetsEveryLedger() {
        var now = 0L
        val tracker = SessionTracker { now }
        tracker.begin("first", listOf(member("a")))
        tracker.observe("first", "a", "native", 20f, 10, attempt = 3)
        tracker.observe("wrong", "a", "native", 90f, 1_000, attempt = 50)
        tracker.observe("first", "missing", "native", 90f, 1_000)
        tracker.mark("wrong", "a", SessionJobState.COMPLETED)
        tracker.setPaused("wrong", true)
        tracker.admit("wrong", listOf(member("wrong")))
        tracker.finish("wrong")
        assertTrue(tracker.current().active)
        assertFalse(tracker.current().paused)
        assertEquals(1, tracker.current().total)
        assertEquals(10L, tracker.current().transferredBytes)
        now = 100
        tracker.finish("first")
        val finished = tracker.current()
        tracker.observe("first", "a", "late", 99f, 1_000)
        tracker.mark("first", "a", SessionJobState.CANCELLED)
        tracker.setPaused("first", true)
        tracker.admit("first", listOf(member("late")))
        now = 500
        assertEquals(finished, tracker.current())

        tracker.begin("second", listOf(member("a")))
        val fresh = tracker.current()
        assertEquals("second", fresh.id)
        assertEquals(0L, fresh.elapsedMillis)
        assertEquals(0L, tracker.transferred("a"))
        assertEquals(0, tracker.attempts("a"))
        tracker.observe("first", "a", "late", 80f, 5_000)
        tracker.finish("first")
        assertEquals(fresh, tracker.current())
        assertEquals(100L, finished.elapsedMillis)
    }

    @Test fun liveAdmissionKeepsExistingStateAndDoesNotResetElapsedTimeOrDuplicateJobs() {
        var now = 0L
        val tracker = SessionTracker { now }
        val initial = mutableListOf(member("a"))
        tracker.begin("session", initial)
        initial.clear()
        tracker.mark("session", "a", SessionJobState.RUNNING)
        tracker.observe("session", "a", "native", 40f, 100)
        now = 200
        tracker.admit("session", listOf(member("a", 99), member("b", 2), member("b", 3)))
        val snapshot = tracker.current()
        assertEquals(200L, snapshot.elapsedMillis)
        assertEquals(2, snapshot.total)
        assertEquals(1, snapshot.running)
        assertEquals(1, snapshot.queued)
        assertEquals(listOf("a", "b"), snapshot.members.map { it.id })
        assertEquals(listOf(1, 2), snapshot.members.map { it.sequence })
        assertEquals(20f, snapshot.overallPercent, 0f)
        assertEquals(100L, snapshot.transferredBytes)
    }

    @Test fun snapshotsAreIndependentImmutableAndBoundedWhileTotalsIncludeEveryMember() {
        val tracker = SessionTracker { 0 }
        tracker.begin("session", (1..40).map { member("job-$it", it) })
        tracker.observe("session", "job-1", "native", 30f, 20)
        tracker.mark("session", "job-1", SessionJobState.COMPLETED)
        tracker.observe("session", "job-40", "native", 50f, 80)
        tracker.mark("session", "job-40", SessionJobState.COMPLETED)
        val old = tracker.current()
        assertEquals(40, old.total)
        assertEquals(38, old.queued)
        assertEquals(2, old.completed)
        assertEquals(32, old.members.size)
        assertEquals((1..32).toList(), old.members.map { it.sequence })
        assertEquals(100L, old.transferredBytes)
        assertEquals(5f, old.overallPercent, 0f)
        assertThrows(UnsupportedOperationException::class.java) { (old.members as MutableList<*>).clear() }
        tracker.observe("session", "job-2", "native", 40f, 10, title = "Изменённое название")
        assertEquals("Видео job-2", old.members[1].title)
        assertEquals(0f, old.members[1].percent, 0f)
        assertEquals("Изменённое название", tracker.current().members[1].title)
    }

    @Test fun parallelCallbacksAreSerializedAndByteTotalsNeverOverflow() {
        val tracker = SessionTracker { 0 }
        tracker.begin("session", (1..4).map { member("job-$it", it) })
        val executor = Executors.newFixedThreadPool(4)
        try {
            val jobs = (1..4).map { job -> executor.submit {
                for (bytes in 0L..999L) {
                    tracker.observe("session", "job-$job", "native-$job", bytes.toFloat() / 10, bytes)
                    assertEquals(4, tracker.current().total)
                }
            } }
            jobs.forEach { it.get(10, TimeUnit.SECONDS) }
            assertEquals(3_996L, tracker.current().transferredBytes)
        } finally { executor.shutdownNow() }
        tracker.observe("session", "job-1", "huge", 50f, Long.MAX_VALUE)
        tracker.observe("session", "job-2", "huge", 50f, Long.MAX_VALUE)
        assertEquals(Long.MAX_VALUE, tracker.transferred("job-1"))
        assertEquals(Long.MAX_VALUE, tracker.current().transferredBytes)
    }
}
