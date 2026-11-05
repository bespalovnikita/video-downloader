package ru.bespalov.video.core

import org.junit.Assert.*
import org.junit.Test

class PublicationCleanupTest {
    private val task = DownloadTask(url = "https://example.com/Video?list=AbC", status = DownloadStatus.COMPLETED, progress = 100f,
        outputUris = listOf("content://owned/media", "content://owned/subtitles"), stageDirectory = "/private/staging/run/task")
    private val success = SuccessRecord(task.url, outputUris = task.outputUris)

    @Test fun publicationCanBeKeptAfterInterruptedPrivateCleanup() {
        val recovered = JsonStateRepository.recoverInterrupted(AppState(queue = listOf(task), history = listOf(success)))
        assertEquals(DownloadStatus.CLEANUP_REQUIRED, recovered.queue.single().status)
        assertTrue(PublicationCleanup.hasSuccessfulPublication(recovered.queue.single(), recovered.history))
        assertTrue(PublicationCleanup.hasSuccessfulPublication(task.copy(status = DownloadStatus.CLEANUP_REQUIRED,
            pendingRemoval = true), listOf(success)))
    }

    @Test fun everyCurrentOutputNeedsSuccessForTheExactUrl() {
        assertFalse(PublicationCleanup.hasSuccessfulPublication(task, emptyList()))
        assertFalse(PublicationCleanup.hasSuccessfulPublication(task, listOf(success.copy(url = task.url.lowercase()))))
        assertFalse(PublicationCleanup.hasSuccessfulPublication(task, listOf(success.copy(outputUris = task.outputUris.take(1)))))
        assertFalse(PublicationCleanup.hasSuccessfulPublication(task.copy(outputUris = emptyList()), listOf(success)))
        assertTrue(PublicationCleanup.hasSuccessfulPublication(task.copy(outputUris = task.outputUris.take(1)), listOf(success)))
    }

    @Test fun reusedProviderUrisDoNotTurnAnIncompleteAttemptIntoASuccess() {
        for (progress in listOf(0f, 30f, 99f)) {
            val partial = task.copy(status = DownloadStatus.CLEANUP_REQUIRED, progress = progress)
            assertFalse("An older success with the same URI must not bypass rollback of a new attempt",
                PublicationCleanup.hasSuccessfulPublication(partial, listOf(success)))
        }
    }

    @Test fun legacyCompletedSnapshotsRecoverTheCommittedSuccessMarker() {
        for (stage in listOf("", task.stageDirectory)) {
            val legacy = task.copy(progress = 0f, stageDirectory = stage)
            val recovered = JsonStateRepository.recoverInterrupted(AppState(queue = listOf(legacy), history = listOf(success)))
            assertEquals(100f, recovered.queue.single().progress, 0f)
            assertTrue(PublicationCleanup.hasSuccessfulPublication(recovered.queue.single(), recovered.history))
        }
    }
}
