package ru.bespalov.video.core

import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.encodeToString
import org.junit.Assert.*
import org.junit.Test
import java.nio.file.Files

class RepositoryAndInputsTest {
    @Test fun stateUpdatesAreSerializedAndHistorySurvivesQueueClear() = runBlocking {
        val directory = Files.createTempDirectory("vd-core-state").toFile()
        try {
            val file = directory.resolve("state.json")
            val repository = JsonStateRepository(file)
            (0 until 20).map { index -> async {
                repository.update { it.copy(queue = it.queue + DownloadTask(id = "row-$index", url = "https://example.com/$index")) }
            } }.awaitAll()
            assertEquals(20, repository.state.value.queue.size)
            val first = repository.state.value.queue.first()
            repository.update { SubscriptionRules.recordSuccess(it, first.id, listOf("content://downloads/owned/1"), now = 123) }
            repository.update { it.copy(queue = emptyList()) }
            val restored = JsonStateRepository(file)
            assertFalse(restored.readOnly)
            assertTrue(restored.state.value.queue.isEmpty())
            assertEquals(listOf(SuccessRecord(first.url, 123, outputUris = listOf("content://downloads/owned/1"))), restored.state.value.history)
            assertTrue(restored.backupFile.exists())
            assertTrue(directory.listFiles()!!.none { it.name.endsWith(".tmp") })
        } finally { directory.deleteRecursively() }
    }

    @Test fun corruptStateIsPreservedAndCannotBeOverwritten() = runBlocking {
        val directory = Files.createTempDirectory("vd-core-corrupt").toFile()
        try {
            val file = directory.resolve("state.json")
            val original = "{ broken user state".toByteArray()
            file.writeBytes(original)
            val repository = JsonStateRepository(file)
            assertTrue(repository.readOnly)
            assertTrue(repository.error.isNotBlank())
            assertThrows(IllegalStateException::class.java) { runBlocking { repository.update { it.copy(settings = it.settings.copy(threads = 3)) } } }
            assertArrayEquals(original, file.readBytes())
            assertFalse(repository.backupFile.exists())
        } finally { directory.deleteRecursively() }
    }

    @Test fun invalidOrFailedTransformLeavesCommittedSnapshotUntouched() = runBlocking {
        val directory = Files.createTempDirectory("vd-core-atomic").toFile()
        try {
            val file = directory.resolve("state.json")
            val repository = JsonStateRepository(file)
            repository.update { it.copy(settings = it.settings.copy(threads = 3)) }
            val bytes = file.readBytes()
            assertThrows(IllegalArgumentException::class.java) { runBlocking {
                repository.update { it.copy(queue = listOf(DownloadTask(url = "file:///secret"))) }
            } }
            assertArrayEquals(bytes, file.readBytes())
            assertEquals(3, repository.state.value.settings.threads)
            val fixedTime = file.lastModified() - 10000
            assertTrue(file.setLastModified(fixedTime))
            repository.update { it.copy(settings = it.settings.copy()) }
            assertEquals(fixedTime, file.lastModified())
        } finally { directory.deleteRecursively() }
    }

    @Test fun interruptedWorkIsRecoverableButNeverAutomaticallyQueued() {
        val running = DownloadTask(id = "running", url = "https://example.com/a", status = DownloadStatus.RUNNING, stageDirectory = "/owned/stage")
        val cleanup = running.copy(id = "cleanup", url = "https://example.com/b", status = DownloadStatus.CANCELLING,
            outputUris = listOf("content://owned/1"), pendingRemoval = true)
        val state = JsonStateRepository.recoverInterrupted(AppState(queue = listOf(running, cleanup)))
        assertEquals(DownloadStatus.INTERRUPTED, state.queue[0].status)
        assertEquals("/owned/stage", state.queue[0].stageDirectory)
        assertEquals(DownloadStatus.CLEANUP_REQUIRED, state.queue[1].status)
        assertEquals(cleanup.outputUris, state.queue[1].outputUris)
        assertTrue(state.queue.none { it.status == DownloadStatus.QUEUED || it.status == DownloadStatus.RUNNING })
    }

    @Test fun successBeforeProcessDeathRetainsHistoryAndRequiresPrivateStageCleanup() {
        val task = DownloadTask(url = "https://example.com/published", status = DownloadStatus.COMPLETED,
            progress = 100f, outputUris = listOf("content://downloads/owned/1"),
            stageDirectory = "/private/staging/run/task", runId = "owned-run")
        val history = listOf(SuccessRecord(task.url, 123, outputUris = task.outputUris))
        val recovered = JsonStateRepository.recoverInterrupted(AppState(queue = listOf(task), history = history))
        assertEquals(DownloadStatus.CLEANUP_REQUIRED, recovered.queue.single().status)
        assertEquals(task.stageDirectory, recovered.queue.single().stageDirectory)
        assertEquals(task.runId, recovered.queue.single().runId)
        assertEquals(task.outputUris, recovered.queue.single().outputUris)
        assertEquals(history, recovered.history)
        val fullyCleaned = JsonStateRepository.recoverInterrupted(AppState(queue = listOf(task.copy(stageDirectory = "", runId = "")), history = history))
        assertEquals(DownloadStatus.COMPLETED, fullyCleaned.queue.single().status)
        assertEquals(history, fullyCleaned.history)
    }

    @Test fun urlAndTemplateIdentityIsExactAndSafe() {
        val upper = "https://example.com/Video?Token=A%2FB#part"
        val lower = "https://example.com/video?Token=A%2FB#part"
        assertEquals(listOf(upper, lower), Urls.extract("$upper\n$lower $upper"))
        assertEquals(listOf(upper), Urls.fromFile("clip.URL", "[InternetShortcut]\r\nURL=$upper\r\n"))
        assertFalse(Urls.isHttp("file:///secret"))
        assertFalse(Urls.isHttp("https://user:secret@example.com/video"))
        assertFalse(Urls.isHttp("https://example.com/\nvideo"))
        FilenameTemplates.validate("%(title)s [%(id)s].%(ext)s")
        listOf("../clip.mp4", "sub/../../clip.mp4", "/clip.mp4", "C:\\clip.mp4", "\\\\server\\file", "name\nfile").forEach {
            assertThrows(IllegalArgumentException::class.java) { FilenameTemplates.validate(it) }
        }
        assertThrows(IllegalArgumentException::class.java) {
            JsonStateRepository.validate(AppState(queue = listOf(DownloadTask(id = "../escape", url = upper))))
        }
    }

    @Test fun crossPlatformHostsKeepTheirExactIdentityAndRejectInvalidPorts() {
        val urls = listOf("https://пример.рф/Видео?Token=AbC%2fD", "https://some_host.example/a",
            "https://127.1/a", "https://-bad.example/a", "https://example.org.:65535/a",
            "https://[::1]:65535/a", "https://[fe80::1%25eth0]/a")
        assertEquals(urls, Urls.extract(urls.joinToString("\n")))
        JsonStateRepository.validate(AppState(queue = urls.map { DownloadTask(url = it) }))
        listOf("https://example.org:65536/a", "https://example.org:9999999999/a", "https://example.org:-1/a",
            "https://example..org/a", "https://user:secret@пример.рф/a", "https://[::1]:65536/a",
            "https://exa%2Fmple.org/a", "https://" + "a".repeat(64) + ".org/a").forEach { url ->
            assertFalse("Invalid authority must be rejected: $url", Urls.isHttp(url))
        }
    }

    @Test fun profileChangesResetOnlyProfileOptionsAndKeepUserPaths() {
        val settings = Settings(cookiesFile = "/private/cookies.txt", outputTreeUri = "content://tree/selected", threads = 3,
            sponsor = true, showTitles = false)
        for (previous in Profiles.names.keys) for (next in Profiles.names.keys) {
            val actual = Profiles.apply(Profiles.apply(settings, previous), next)
            assertEquals(Profiles.apply(settings, next), actual)
            assertEquals(settings.cookiesFile, actual.cookiesFile)
            assertEquals(settings.outputTreeUri, actual.outputTreeUri)
            assertEquals(settings.threads, actual.threads)
        }
        assertEquals("Канал (2)", SubscriptionNames.create("Канал", "id", listOf("Канал")))
        assertFalse(SubscriptionNames.isValid("../outside"))
    }

    @Test fun invalidPinnedSettingsCannotBypassCurrentSettingsValidation() {
        val valid = DownloadTask(url = "https://example.com/video", settingsSnapshot = Settings(fragments = 9))
        assertThrows(IllegalArgumentException::class.java) { JsonStateRepository.validate(AppState(queue = listOf(valid))) }
        for (settings in listOf(Settings(quality = "unbounded"), Settings(codec = "unknown"), Settings(container = "exe"),
            Settings(rateLimit = "--exec"), Settings(subtitleLangs = "en\n--exec"))) {
            assertThrows(IllegalArgumentException::class.java) { JsonStateRepository.validate(AppState(settings = settings)) }
        }
    }

    @Test fun permanentErrorsOverrideTransientHintsAndRussianDoesNotMutateRaw() {
        val raw = "ERROR: HTTP Error 403: Forbidden | connection timed out"
        assertFalse(ErrorText.isTransient(raw))
        assertTrue(ErrorText.isTransient("ERROR: HTTP Error 503: Service Unavailable"))
        assertFalse(ErrorText.isTransient("ERROR: https://example.com/video/503?message=timeout unknown extractor result"))
        assertTrue(ErrorText.display(raw).contains("403"))
        assertEquals(raw, ErrorText.display(raw, "en"))
        assertEquals("Ошибка доступа", ErrorText.display("Ошибка доступа"))
    }
}
