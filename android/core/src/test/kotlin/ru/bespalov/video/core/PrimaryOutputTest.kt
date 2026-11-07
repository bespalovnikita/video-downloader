package ru.bespalov.video.core

import kotlinx.coroutines.runBlocking
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.*
import org.junit.Test
import java.nio.file.Files

class PrimaryOutputTest {
    private val sidecar = "content://provider/opaque/1"
    private val video = "content://provider/opaque/2"
    private val task = DownloadTask(id = "primary-test", url = "https://example.org/video", status = DownloadStatus.RUNNING)

    @Test fun sidecarOrderDoesNotChangePrimaryInQueueHistoryOrRecoveredSnapshot() = runBlocking {
        val directory = Files.createTempDirectory("vd-primary").toFile()
        try {
            for (uris in listOf(listOf(sidecar, video), listOf(video, sidecar))) {
                val repository = JsonStateRepository(directory.resolve("state.json"))
                repository.update { AppState(queue = listOf(task)) }
                repository.update { SubscriptionRules.recordSuccess(it, task.id, uris, now = 123, primaryOutputUri = video) }
                repository.update { state -> state.copy(queue = state.queue.map { it.copy(stageDirectory = "/private/stage") }) }
                val restored = JsonStateRepository(repository.file)
                assertFalse(restored.error, restored.readOnly)
                assertEquals(uris, restored.state.value.queue.single().outputUris)
                assertEquals(video, restored.state.value.queue.single().primaryOutputUri)
                assertEquals(video, restored.state.value.history.single().primaryOutputUri)
                assertEquals(DownloadStatus.CLEANUP_REQUIRED, restored.state.value.queue.single().status)
                assertTrue(PublicationCleanup.hasSuccessfulPublication(restored.state.value.queue.single(), restored.state.value.history))
            }
        } finally { directory.deleteRecursively() }
    }

    @Test fun reencodingLegacyStateOmitsEmptyPrimaryMarkersButKeepsRecordedPrimary() {
        val json = JsonStateRepository.JSON
        val legacy = json.decodeFromString<AppState>("""{
            "queue": [{"id":"legacy", "url":"https://example.org/video", "outputUris":["$sidecar", "$video"]}],
            "history": [{"url":"https://example.org/video", "completedAtUtc":123, "outputUris":["$sidecar", "$video"]}]
        }""")
        assertEquals("", legacy.queue.single().primaryOutputUri)
        assertEquals("", legacy.history.single().primaryOutputUri)
        val reencoded = json.parseToJsonElement(json.encodeToString(legacy)).jsonObject
        for (key in listOf("queue", "history")) {
            assertFalse("Re-saving legacy $key must not add bytes for an empty marker",
                reencoded.getValue(key).jsonArray.single().jsonObject.containsKey("primaryOutputUri"))
        }
        val modern = legacy.copy(queue = legacy.queue.map { it.copy(primaryOutputUri = video) },
            history = legacy.history.map { it.copy(primaryOutputUri = video) })
        val encodedModern = json.encodeToString(modern)
        val recorded = json.parseToJsonElement(encodedModern).jsonObject
        for (key in listOf("queue", "history")) {
            assertEquals(video, recorded.getValue(key).jsonArray.single().jsonObject.getValue("primaryOutputUri").jsonPrimitive.content)
        }
        assertEquals(modern, json.decodeFromString<AppState>(encodedModern))
    }

    @Test fun aPrimaryCannotExpandThePublishedManifest() {
        assertThrows(IllegalArgumentException::class.java) {
            SubscriptionRules.recordSuccess(AppState(queue = listOf(task)), task.id, listOf(sidecar), primaryOutputUri = video)
        }
    }

    @Test fun legacyAndInvalidOptionalMarkersDoNotMakeStateOrBackupUnreadable() {
        val state = AppState(queue = listOf(task.copy(outputUris = listOf(sidecar, video), primaryOutputUri = "content://foreign/uri")),
            history = listOf(SuccessRecord(task.url, 123, outputUris = listOf(sidecar, video), primaryOutputUri = "content://foreign/history")))
        val directory = Files.createTempDirectory("vd-primary-legacy").toFile()
        try {
            val file = directory.resolve("state.json")
            file.writeText(JsonStateRepository.JSON.encodeToString(state))
            assertFalse(JsonStateRepository(file).readOnly)
            val encoded = JsonStateRepository.JSON.parseToJsonElement(JsonStateRepository.JSON.encodeToString(state)).jsonObject
            val legacyJson = JsonObject(encoded + listOf("queue", "history").associateWith { key ->
                JsonArray(encoded.getValue(key).jsonArray.map { JsonObject(it.jsonObject - "primaryOutputUri") })
            })
            file.writeText(legacyJson.toString())
            val legacy = JsonStateRepository(file)
            assertFalse(legacy.error, legacy.readOnly)
            assertEquals("", legacy.state.value.queue.single().primaryOutputUri)
            assertEquals("", legacy.state.value.history.single().primaryOutputUri)
            for (invalid in listOf("null", "123", "{}", "[]", "true")) {
                file.writeText(JsonStateRepository.JSON.encodeToString(state).replace(Regex("\"primaryOutputUri\": \"[^\"]*\""), "\"primaryOutputUri\": $invalid"))
                val tolerant = JsonStateRepository(file)
                assertFalse("Optional metadata $invalid: ${tolerant.error}", tolerant.readOnly)
                assertEquals("", tolerant.state.value.queue.single().primaryOutputUri)
                assertEquals("", tolerant.state.value.history.single().primaryOutputUri)
            }
            val imported = BackupImport.sanitize(state)
            assertTrue(imported.queue.single().outputUris.isEmpty())
            assertEquals("", imported.queue.single().primaryOutputUri)
            assertTrue(imported.history.single().outputUris.isEmpty())
            assertEquals("", imported.history.single().primaryOutputUri)
        } finally { directory.deleteRecursively() }
    }
}
