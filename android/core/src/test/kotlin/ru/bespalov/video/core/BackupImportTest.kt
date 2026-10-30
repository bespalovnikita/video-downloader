package ru.bespalov.video.core

import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class BackupImportTest {
    @Test fun externalPathsAndCleanupInstructionsNeverBecomeLocalOwnership() {
        val settings = Settings(cookiesFile = "/data/user/0/other.app/cookies.txt", outputTreeUri = "content://provider/tree/foreign")
        val original = DownloadTask(
            id = "foreign-task", url = "https://example.com/Video?token=A%2FB#part", title = "Saved title",
            status = DownloadStatus.CLEANUP_REQUIRED, progress = 89f, speed = "1M/s", downloaded = "100MB", eta = "00:10",
            transferredBytes = 123, rawError = "cleanup failed", outputUris = listOf("file:///storage/emulated/0/unrelated.mp4", "content://provider/document/foreign"),
            stageDirectory = "/storage/emulated/0/DCIM", runId = "old-process", pendingRemoval = true,
            settingsSnapshot = settings, archiveDate = "2026-01-01",
        )
        val history = SuccessRecord(original.url, 1234, outputUris = original.outputUris)
        val imported = AppState(settings = settings, queue = listOf(original), history = listOf(history))

        val safe = BackupImport.sanitize(imported)
        val task = safe.queue.single()
        assertEquals("", safe.settings.cookiesFile)
        assertEquals("", safe.settings.outputTreeUri)
        assertEquals(DownloadStatus.INTERRUPTED, task.status)
        assertFalse(task.pendingRemoval)
        assertTrue(task.outputUris.isEmpty())
        assertTrue(safe.history.single().outputUris.isEmpty())
        assertEquals("", task.stageDirectory)
        assertEquals("", task.runId)
        assertNull(task.settingsSnapshot)
        assertEquals("", task.archiveDate)
        assertEquals("", task.rawError)
        assertEquals("", task.speed)
        assertEquals("", task.downloaded)
        assertEquals("", task.eta)
        assertEquals(0f, task.progress, 0f)
        assertEquals(0L, task.transferredBytes)
        assertTrue(task.deferredNextStart)

        assertEquals(original.url, task.url)
        assertEquals(original.title, task.title)
        assertEquals(history.copy(outputUris = emptyList()), safe.history.single())
        // Immutable input still contains the foreign paths; the caller can
        // preserve the original backup without editing or using those paths.
        assertEquals(original, imported.queue.single())
        assertEquals(settings, imported.settings)
    }

    @Test fun importedStatusesNeverResumeNativeWorkOrCleanupAutomatically() {
        for (status in DownloadStatus.entries) {
            val task = DownloadTask(url = "https://example.com/video", status = status)
            val safe = BackupImport.sanitize(AppState(queue = listOf(task))).queue.single()
            assertEquals(if (status == DownloadStatus.COMPLETED) DownloadStatus.COMPLETED else DownloadStatus.INTERRUPTED, safe.status)
            assertFalse(safe.pendingRemoval)
            assertTrue(safe.outputUris.isEmpty())
            assertEquals("", safe.stageDirectory)
        }
    }

    @Test fun importedQueueIdsCannotAliasCanonicalStagingPathsOrOldCommands() {
        val upper = "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA"
        val lower = upper.lowercase()
        val input = AppState(queue = listOf(
            DownloadTask(id = upper, url = "https://example.com/Video"),
            DownloadTask(id = lower, url = "https://example.com/video"),
        ))
        JsonStateRepository.validate(input)
        val safe = BackupImport.sanitize(input)
        val ids = safe.queue.map { it.id }
        assertEquals(2, ids.distinct().size)
        ids.forEach { id ->
            assertEquals(id, UUID.fromString(id).toString())
            assertFalse(id in setOf(upper, lower))
        }
        assertEquals(input.queue.map { it.url }, safe.queue.map { it.url })
    }

    @Test fun exactUrlsSourceAttributionAndHistorySurviveSanitizing() {
        val source = Subscription(id = "source", url = "https://example.com/channel", shortName = "Канал", initialized = true)
        val urls = listOf("https://example.com/Video?Token=A%2FB", "https://example.com/video?Token=A%2FB")
        val state = AppState(
            subscriptions = listOf(source),
            queue = urls.mapIndexed { index, url -> DownloadTask(id = "task-$index", url = url, title = "Видео $index", subscriptionId = source.id, subscriptionName = source.shortName) },
            items = urls.map { SubscriptionEntry(source.id, it, state = EntryState.QUEUED) },
            history = urls.mapIndexed { index, url -> SuccessRecord(url, 1000L + index, source.id, listOf("content://old/owned/$index")) },
        )
        val safe = BackupImport.sanitize(state)
        assertEquals(urls, safe.queue.map { it.url })
        assertEquals(state.subscriptions, safe.subscriptions)
        assertEquals(state.items, safe.items)
        assertEquals(state.history.map { it.url to it.completedAtUtc }, safe.history.map { it.url to it.completedAtUtc })
        assertEquals(listOf("source", "source"), safe.history.map { it.subscriptionId })
    }

    @Test fun invalidSnapshotsAreRejectedBeforeCapabilitiesAreRemoved() {
        assertThrows(IllegalArgumentException::class.java) { BackupImport.sanitize(AppState(version = 99)) }
        assertThrows(IllegalArgumentException::class.java) {
            BackupImport.sanitize(AppState(queue = listOf(DownloadTask(url = "https://example.com/a", settingsSnapshot = Settings(filenameTemplate = "../../foreign")))))
        }
        assertThrows(IllegalArgumentException::class.java) {
            BackupImport.sanitize(AppState(queue = listOf(DownloadTask(url = "file:///foreign"))))
        }
    }
}
