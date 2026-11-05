package ru.bespalov.videodownloader

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Bundle
import android.os.Process
import android.provider.DocumentsContract
import androidx.documentfile.provider.DocumentFile
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import ru.bespalov.video.core.DownloadStatus
import ru.bespalov.video.core.DownloadTask
import ru.bespalov.video.core.JsonStateRepository
import ru.bespalov.video.core.SuccessRecord
import ru.bespalov.videodownloader.download.DownloadService
import ru.bespalov.videodownloader.download.MediaPublisher
import java.util.UUID
import java.io.File

/** Exercise actual ContentResolver/Binder URI grants, rather than a fake exists() implementation. */
@RunWith(AndroidJUnit4::class)
class SafCleanupInstrumentedTest {
    private val context: Context get() = ApplicationProvider.getApplicationContext()
    private val graph: AppGraph get() = (context.applicationContext as DownloaderApplication).graph

    private class Fixture(val id: String) {
        val uri: Uri = DocumentsContract.buildDocumentUri(CleanupDocumentsProvider.AUTHORITY, id)
    }

    private fun control(fixture: Fixture, method: String, extras: Bundle? = null): Bundle =
        requireNotNull(context.contentResolver.call(CleanupDocumentsProvider.CONTROL, method, fixture.id, extras))

    private fun fixture(): Fixture {
        val fixture = Fixture(UUID.randomUUID().toString())
        control(fixture, CleanupDocumentsProvider.CREATE, Bundle().apply { putByteArray(CleanupDocumentsProvider.BYTES, SAMPLE) })
        grant(fixture)
        return fixture
    }

    private fun grant(fixture: Fixture) {
        control(fixture, CleanupDocumentsProvider.GRANT, Bundle().apply { putString(CleanupDocumentsProvider.PACKAGE, context.packageName) })
        assertEquals("The target app must receive a real Android URI grant", PackageManager.PERMISSION_GRANTED,
            context.checkUriPermission(fixture.uri, Process.myPid(), Process.myUid(), GRANTS))
    }

    private fun status(fixture: Fixture) = control(fixture, CleanupDocumentsProvider.STATUS)

    @Test fun revokedDocumentGrantRetainsOwnershipUntilARealRegrant() = runBlocking(Dispatchers.IO) {
        val fixture = fixture()
        try {
            assertArrayEquals(SAMPLE, context.contentResolver.openInputStream(fixture.uri)!!.use { it.readBytes() })
            control(fixture, CleanupDocumentsProvider.REVOKE)
            assertEquals("The real document grant must be absent", PackageManager.PERMISSION_DENIED,
                context.checkUriPermission(fixture.uri, Process.myPid(), Process.myUid(), GRANTS))
            // DocumentFile loses the distinction between a missing document and
            // a failed query; this was the previous cleanup implementation's trap.
            assertFalse(DocumentFile.fromSingleUri(context, fixture.uri)!!.exists())
            val publisher = MediaPublisher(context)
            assertEquals(listOf(fixture.uri.toString()), publisher.deleteOwned(listOf(fixture.uri.toString())))
            assertTrue("A revoked grant must not be reported as a deleted file", status(fixture).getBoolean(CleanupDocumentsProvider.EXISTS))
            assertEquals(0, status(fixture).getInt(CleanupDocumentsProvider.DELETES))

            grant(fixture)
            assertTrue(publisher.deleteOwned(listOf(fixture.uri.toString())).isEmpty())
            assertFalse(status(fixture).getBoolean(CleanupDocumentsProvider.EXISTS))
            assertEquals(1, status(fixture).getInt(CleanupDocumentsProvider.DELETES))
            assertTrue("An already absent, successfully queried document needs no cleanup", publisher.deleteOwned(listOf(fixture.uri.toString())).isEmpty())
        } finally { control(fixture, CleanupDocumentsProvider.FORGET) }
    }

    @Test fun nullCursorAndProviderFailureCannotDiscardTheOwnedUri() = runBlocking(Dispatchers.IO) {
        for (mode in listOf(CleanupDocumentsProvider.NULL_CURSOR, CleanupDocumentsProvider.PROVIDER_ERROR, CleanupDocumentsProvider.STORAGE_MISSING)) {
            val fixture = fixture()
            try {
                control(fixture, CleanupDocumentsProvider.QUERY_MODE, Bundle().apply { putString(CleanupDocumentsProvider.MODE, mode) })
                val publisher = MediaPublisher(context)
                assertEquals("Provider mode $mode must remain retryable", listOf(fixture.uri.toString()), publisher.deleteOwned(listOf(fixture.uri.toString())))
                assertTrue(status(fixture).getBoolean(CleanupDocumentsProvider.EXISTS))
                assertEquals(0, status(fixture).getInt(CleanupDocumentsProvider.DELETES))
                control(fixture, CleanupDocumentsProvider.QUERY_MODE, Bundle().apply { putString(CleanupDocumentsProvider.MODE, CleanupDocumentsProvider.NORMAL) })
                assertTrue(publisher.deleteOwned(listOf(fixture.uri.toString())).isEmpty())
                assertFalse(status(fixture).getBoolean(CleanupDocumentsProvider.EXISTS))
            } finally { control(fixture, CleanupDocumentsProvider.FORGET) }
        }
    }

    @Test fun presentDocumentWithAFailedDeleteRemainsRetryable() = runBlocking(Dispatchers.IO) {
        val fixture = fixture()
        try {
            control(fixture, CleanupDocumentsProvider.DELETE_FAILS, Bundle().apply { putBoolean(CleanupDocumentsProvider.FAIL, true) })
            val publisher = MediaPublisher(context)
            assertEquals(listOf(fixture.uri.toString()), publisher.deleteOwned(listOf(fixture.uri.toString())))
            assertTrue(status(fixture).getBoolean(CleanupDocumentsProvider.EXISTS))
            assertEquals(1, status(fixture).getInt(CleanupDocumentsProvider.DELETES))
            control(fixture, CleanupDocumentsProvider.DELETE_FAILS, Bundle().apply { putBoolean(CleanupDocumentsProvider.FAIL, false) })
            assertTrue(publisher.deleteOwned(listOf(fixture.uri.toString())).isEmpty())
            assertFalse(status(fixture).getBoolean(CleanupDocumentsProvider.EXISTS))
        } finally { control(fixture, CleanupDocumentsProvider.FORGET) }
    }

    @Test fun servicePersistsReusedDocumentForRollbackAfterAccessReturns() {
        runBlocking(Dispatchers.IO) {
            withTimeout(90_000) { graph.ready.await() }
            assertFalse(graph.repository.error, graph.repository.readOnly)
            awaitServiceIdle()
        }
        val fixture = fixture()
        val task = DownloadTask(url = "https://example.invalid/saf-cleanup-${fixture.id}",
            status = DownloadStatus.CLEANUP_REQUIRED, outputUris = listOf(fixture.uri.toString()))
        val earlierSuccess = SuccessRecord(task.url, completedAtUtc = 1234, outputUris = task.outputUris)
        try {
            // Filesystem-backed SAF providers may use a path as a document ID.
            // Delete an old document, then create a new incomplete publication
            // at that exact URI while the earlier successful history remains.
            runBlocking(Dispatchers.IO) { assertTrue(MediaPublisher(context).deleteOwned(task.outputUris).isEmpty()) }
            control(fixture, CleanupDocumentsProvider.FORGET)
            control(fixture, CleanupDocumentsProvider.CREATE, Bundle().apply { putByteArray(CleanupDocumentsProvider.BYTES, SAMPLE) })
            grant(fixture)
            runBlocking(Dispatchers.IO) {
                graph.repository.update { it.copy(queue = it.queue + task, history = it.history + earlierSuccess) }
            }
            control(fixture, CleanupDocumentsProvider.REVOKE)
            ActivityScenario.launch(MainActivity::class.java).use { activity ->
                // A keep request cannot retain an incomplete publication. It must
                // still roll back, while preserving ownership if access is denied.
                activity.onActivity { DownloadService.removeTask(it, task.id, deleteFiles = false) }
                runBlocking(Dispatchers.IO) {
                    withTimeout(20_000) {
                        while (true) {
                            val row = graph.repository.state.value.queue.firstOrNull { it.id == task.id }
                            assertTrue("A provider denial must retain the task and its cleanup manifest", row != null)
                            if (row!!.pendingRemoval && row.rawError.isNotBlank() && !DownloadService.isRunning.value && !DownloadService.hasOwnedTasks) break
                            delay(50)
                        }
                    }
                    val row = graph.repository.state.value.queue.single { it.id == task.id }
                    assertEquals(DownloadStatus.CLEANUP_REQUIRED, row.status)
                    assertEquals(task.outputUris, row.outputUris)
                    val reopened = JsonStateRepository(File(context.filesDir, "state.json"))
                    assertFalse(reopened.error, reopened.readOnly)
                    assertEquals("The denied document must survive a snapshot reopen", row,
                        reopened.state.value.queue.single { it.id == task.id })
                    assertEquals(listOf(earlierSuccess), graph.repository.state.value.history.filter { it.url == task.url })
                    assertTrue(status(fixture).getBoolean(CleanupDocumentsProvider.EXISTS))
                }

                grant(fixture)
                activity.onActivity { DownloadService.removeTask(it, task.id, deleteFiles = false) }
                runBlocking(Dispatchers.IO) {
                    withTimeout(20_000) {
                        while (graph.repository.state.value.queue.any { it.id == task.id }) delay(50)
                    }
                    awaitServiceIdle()
                    assertFalse("Regrant and explicit retry must remove the real provider file", status(fixture).getBoolean(CleanupDocumentsProvider.EXISTS))
                    assertEquals("Rollback must retain the older successful history without adopting the reused URI",
                        listOf(earlierSuccess), graph.repository.state.value.history.filter { it.url == task.url })
                }
            }
        } finally {
            try {
                runBlocking(Dispatchers.IO) {
                    awaitServiceIdle()
                    graph.repository.update { it.copy(queue = it.queue.filterNot { row -> row.id == task.id },
                        history = it.history.filterNot { row -> row.url == task.url }) }
                }
            } finally { control(fixture, CleanupDocumentsProvider.FORGET) }
        }
    }

    private suspend fun awaitServiceIdle() = withTimeout(20_000) {
        while (DownloadService.isRunning.value || DownloadService.hasOwnedTasks) delay(50)
    }

    companion object {
        private val SAMPLE = "SAF cleanup fixture, never a user's document".toByteArray()
        private const val GRANTS = Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
    }
}
