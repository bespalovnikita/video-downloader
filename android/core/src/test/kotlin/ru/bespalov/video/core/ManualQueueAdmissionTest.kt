package ru.bespalov.video.core

import org.junit.Assert.*
import org.junit.Test

class ManualQueueAdmissionTest {
    private val url = "https://example.com/Video?id=A%2FB"
    private val task = DownloadTask(id = "manual", url = url, deferredNextStart = true)
    private val history = listOf(SuccessRecord(url, 42, outputUris = listOf("content://owned/earlier")))
    private fun request(seen: List<SuccessRecord> = emptyList()) = listOf(ManualQueueCandidate(task.id, task.url, seen))

    @Test fun approvedExactRowAndHistoryAreEligibleWithoutMutatingQueueHistoryOrOwnership() {
        val state = AppState(queue = listOf(task), history = history)
        assertEquals(setOf(task.id), ManualQueueAdmission.eligibleIds(state, request(history)))
        assertEquals(listOf(task), state.queue)
        assertEquals(history, state.history)
        assertTrue(task.outputUris.isEmpty())
        assertTrue(task.deferredNextStart)
        assertEquals(setOf(task.id), ManualQueueAdmission.eligibleIds(
            state.copy(queue = listOf(task.copy(title = "Fresh metadata")), settings = Settings(quality = "720")), request(history)))
    }

    @Test fun changedHistoryNeedsAnotherDecisionRatherThanSilentlyRepeating() {
        val state = AppState(queue = listOf(task), history = history)
        assertTrue(ManualQueueAdmission.eligibleIds(state, request()).isEmpty())
        assertTrue(ManualQueueAdmission.eligibleIds(state.copy(history = history + SuccessRecord(url, 43)), request(history)).isEmpty())
        assertTrue(ManualQueueAdmission.eligibleIds(state.copy(history = history.map { it.copy(outputUris = listOf("content://other/file")) }), request(history)).isEmpty())
        assertTrue(ManualQueueAdmission.eligibleIds(state.copy(history = emptyList()), request(history)).isEmpty())
        assertEquals(setOf(task.id), ManualQueueAdmission.eligibleIds(state.copy(history = listOf(SuccessRecord(url.lowercase(), 43))), request()))
    }

    @Test fun runningCompletedCancelledRemovedAndOwnedRowsCannotBeAdmittedAsFreshManualInputs() {
        val blocked = DownloadStatus.entries.filterNot { it == DownloadStatus.QUEUED }.map { task.copy(status = it) } + listOf(
            task.copy(deferredNextStart = false), task.copy(pendingRemoval = true),
            task.copy(subscriptionId = "subscription"), task.copy(subscriptionName = "A source"),
            task.copy(stageDirectory = "/private/staging"), task.copy(runId = "earlier-run"),
            task.copy(outputUris = listOf("content://owned/result")), task.copy(settingsSnapshot = Settings()),
        )
        blocked.forEach { row ->
            assertTrue("Must not admit $row", ManualQueueAdmission.eligibleIds(AppState(queue = listOf(row)), request()).isEmpty())
        }
    }

    @Test fun staleIdsUrlsAndAmbiguousRequestsAreRejectedButIndependentRowsRemainEligible() {
        val other = task.copy(id = "other", url = "https://example.com/other")
        val state = AppState(queue = listOf(task, other))
        assertTrue(ManualQueueAdmission.eligibleIds(state, listOf(ManualQueueCandidate("deleted", url, emptyList()))).isEmpty())
        assertTrue(ManualQueueAdmission.eligibleIds(state, listOf(ManualQueueCandidate(task.id, url.lowercase(), emptyList()))).isEmpty())
        val ambiguous = request() + ManualQueueCandidate(task.id, "https://example.com/replaced", emptyList()) +
            ManualQueueCandidate(other.id, other.url, emptyList())
        assertEquals(setOf(other.id), ManualQueueAdmission.eligibleIds(state, ambiguous))
        assertTrue(ManualQueueAdmission.eligibleIds(state.copy(queue = emptyList()), request()).isEmpty())
    }
}
