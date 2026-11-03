package ru.bespalov.videodownloader

import android.app.KeyguardManager
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.compose.ui.test.assertHasClickAction
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.core.content.FileProvider
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.ViewModelProvider
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.uiautomator.UiDevice
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import ru.bespalov.video.core.DownloadStatus
import ru.bespalov.video.core.DownloadTask
import ru.bespalov.video.core.JournalContext
import ru.bespalov.video.core.JournalLevel
import ru.bespalov.video.core.JournalProfile
import ru.bespalov.video.core.JournalRecord
import ru.bespalov.video.core.Settings
import ru.bespalov.videodownloader.download.DownloadService
import ru.bespalov.videodownloader.download.MediaPublisher
import ru.bespalov.videodownloader.download.SessionJobState
import ru.bespalov.videodownloader.engine.EngineProgress
import ru.bespalov.videodownloader.engine.EngineTrace
import java.io.File
import java.nio.ByteBuffer
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.CopyOnWriteArrayList

/** Device contracts use the packaged native runtime, real Activity/clipboard and ContentResolver. */
@RunWith(AndroidJUnit4::class)
class SessionJournalInstrumentedTest {
    @get:Rule val compose = createEmptyComposeRule()
    private val context: Context get() = ApplicationProvider.getApplicationContext()
    private val graph: AppGraph get() = (context.applicationContext as DownloaderApplication).graph

    private fun initialize() = runBlocking(Dispatchers.IO) {
        withTimeout(90_000) { graph.ready.await() }
        assertTrue(graph.startupError.value, graph.runtimeReady.value)
        assertFalse(graph.repository.error, graph.repository.readOnly)
        withTimeout(20_000) { while (DownloadService.isRunning.value || DownloadService.hasOwnedTasks) delay(50) }
    }

    private fun sample() = InstrumentationRegistry.getInstrumentation().context.assets.open("offline-sample.mp4").use { it.readBytes() }
    private fun padded(bytes: Int): ByteArray = sample() + ByteBuffer.allocate(bytes).apply {
        putInt(capacity()); put("free".toByteArray(Charsets.US_ASCII))
    }.array()
    private fun await(timeout: Long = 90_000, predicate: () -> Boolean) = runBlocking(Dispatchers.IO) {
        withTimeout(timeout) { while (!predicate()) delay(50) }
    }
    private fun settings(previous: Settings, token: String) = previous.copy(
        profile = "Universal", threads = 4, fragments = 1, quality = "Best", codec = "Auto", container = "Auto",
        filenameTemplate = "$token-%(id)s.%(ext)s", outputTreeUri = "", cookiesFile = "", rateLimit = "Unlimited",
        audioOnly = false, archive = false, sponsor = false, writeSubs = false, autoSubs = false,
        embedSubs = false, embedThumbnail = false, embedMetadata = false, embedChapters = false, logProfile = "Detailed",
    )

    @Test fun fourParallelDownloadsRetainMembersAcrossPauseHomeAndRotationAndFreezeAfterCleanup() {
        initialize()
        val device = UiDevice.getInstance(InstrumentationRegistry.getInstrumentation())
        device.wakeUp()
        device.executeShellCommand("wm dismiss-keyguard")
        val keyguard = requireNotNull(context.getSystemService(KeyguardManager::class.java))
        await(5_000) { device.isScreenOn && !keyguard.isKeyguardLocked }
        val fast = padded(1024 * 1024)
        val slow = padded(2 * 1024 * 1024)
        val paths = (0..3).map { "/session-$it.mp4" }
        SessionLoopbackServer(paths.associateWith { if (it == paths.first()) fast else slow },
            paths.associateWith { if (it == paths.first()) 80L else 100L }, simultaneousTransfers = 4).use { origin ->
            val token = "session-${UUID.randomUUID()}"
            val tasks = paths.mapIndexed { index, path -> DownloadTask(url = origin.baseUrl + path, title = "$token video $index") }
            val ids = tasks.map { it.id }.toSet()
            val urls = tasks.map { it.url }.toSet()
            val previous = graph.repository.state.value.settings
            val previousSession = DownloadService.session.value?.id
            try {
                runBlocking(Dispatchers.IO) { graph.repository.update { it.copy(settings = settings(previous, token), queue = it.queue + tasks) } }
                await(5_000) { graph.journal.profile == JournalProfile.Detailed }
                ActivityScenario.launch(MainActivity::class.java).use { activity ->
                    activity.onActivity { DownloadService.start(it, tasks.map { task -> task.id }) }
                    await {
                        val session = DownloadService.session.value
                        session != null && session.id != previousSession && session.total == 4 && session.running == 4 &&
                            ids.all { id -> (DownloadService.progress.value[id]?.downloadedBytes ?: 0) > 0 }
                    }
                    val id = requireNotNull(DownloadService.session.value).id
                    val progress = DownloadService.progress.value.filterKeys { it in ids }
                    assertEquals(4, progress.size)
                    assertTrue("No native component may publish 100% before success", progress.values.all { it.percent.isFinite() && it.percent in 0f..99f })
                    assertTrue("A component plan must arrive from the real before_dl marker", progress.values.all { it.planKnown })
                    activity.onActivity { DownloadService.pauseAll(it) }
                    await(30_000) {
                        DownloadService.session.value?.let { it.id == id && it.paused && it.queued == 4 && it.running == 0 } == true &&
                            graph.repository.state.value.queue.filter { it.id in ids }.all { it.status == DownloadStatus.PAUSED }
                    }
                    val pauseTime = requireNotNull(DownloadService.session.value).elapsedMillis
                    await(5_000) { (DownloadService.session.value?.elapsedMillis ?: 0) >= pauseTime + 700 }
                    activity.recreate()
                    assertEquals("Rotation must retain the foreground owner's session", id, DownloadService.session.value?.id)
                    assertTrue(DownloadService.session.value?.paused == true)
                    activity.onActivity { DownloadService.resumeAll(it) }
                    await(30_000) { DownloadService.session.value?.let { !it.paused && it.running > 0 } == true }
                    activity.onActivity { it.startActivity(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
                    await(10_000) { activity.state == Lifecycle.State.CREATED }
                    val beforeBackground = requireNotNull(DownloadService.session.value)
                    await(15_000) {
                        val now = DownloadService.session.value
                        now != null && now.id == id && now.transferredBytes > beforeBackground.transferredBytes && now.elapsedMillis > beforeBackground.elapsedMillis
                    }
                    assertEquals("Transfer must continue while the actual Home task keeps Activity stopped", Lifecycle.State.CREATED, activity.state)
                    await(90_000) { DownloadService.session.value?.let { it.id == id && it.completed >= 1 && it.running > 0 } == true }
                    val mixed = requireNotNull(DownloadService.session.value)
                    assertEquals("Finished workers cannot disappear from the overall denominator", 4, mixed.total)
                    assertEquals(ids, mixed.members.map { it.id }.toSet())
                    assertTrue(mixed.members.any { it.state == SessionJobState.COMPLETED && it.percent == 100f })
                    assertTrue(mixed.overallPercent in 0f..99.99f)
                    await(120_000) { DownloadService.session.value?.let { it.id == id && !it.active && it.completed == 4 } == true && !DownloadService.isRunning.value }
                    val final = requireNotNull(DownloadService.session.value)
                    assertEquals(4, final.total)
                    assertEquals(100f, final.overallPercent, 0f)
                    assertEquals(0, final.failed)
                    assertEquals(0, final.cancelled)
                    val completed = graph.repository.state.value.queue.filter { it.id in ids }
                    assertEquals(4, completed.size)
                    completed.forEach { row ->
                        assertEquals("Native result failed: ${row.rawError}", DownloadStatus.COMPLETED, row.status)
                        assertEquals("Only the verified published result is 100%", 100f, row.progress, 0f)
                        assertTrue("Final session must wait for private cleanup", row.stageDirectory.isEmpty())
                        assertTrue(row.outputUris.isNotEmpty())
                        assertTrue(hash(row.outputUris.first()).isNotEmpty())
                        assertTrue(graph.repository.state.value.history.any { it.url == row.url && it.outputUris == row.outputUris })
                    }
                    runBlocking(Dispatchers.IO) { delay(1_200) }
                    assertEquals("A completed session must stop its clock", final.elapsedMillis, DownloadService.session.value?.elapsedMillis)
                    val journal = graph.journal.snapshotText()
                    assertTrue("The overall trace must identify its member videos", journal.contains("[Progress/overall]"))
                    tasks.forEach { task ->
                        assertTrue("Every native worker must be traceable by job ID", journal.contains(task.id))
                    }
                    assertTrue(journal.contains("video_seq="))
                    assertTrue("Detailed capture must include genuine yt-dlp output", journal.contains("[Verbose/yt-dlp]"))
                }
            } finally { cleanup(ids, urls, previous) }
        }
    }

    @Test fun journalProfilesAndRealClipboardAndContentExportPreserveMultilineButHideSecrets() {
        initialize()
        val previous = graph.repository.state.value.settings
        val token = UUID.randomUUID().toString()
        val directory = File(context.filesDir, "exports").apply { mkdirs() }
        val exported = File(directory, "journal-$token.txt")
        try {
            ActivityScenario.launch(MainActivity::class.java).use { activity ->
                lateinit var model: DownloaderViewModel
                activity.onActivity { model = ViewModelProvider(it)[DownloaderViewModel::class.java] }
                compose.onNodeWithTag("openJournal").performClick()
                compose.onNodeWithTag("journalDialog").assertIsDisplayed()
                for (profile in JournalProfile.entries) {
                    val oldProfile = JournalProfile.parse(graph.repository.state.value.settings.logProfile)
                    if (profile != oldProfile) {
                        compose.onNodeWithText(oldProfile.displayName).performClick()
                        compose.onNodeWithText(profile.displayName).performClick()
                    }
                    await(10_000) { graph.repository.state.value.settings.logProfile == profile.id && graph.journal.profile == profile }
                    graph.journal.clear()
                    val prefix = "$token-${profile.id}"
                    listOf(JournalLevel.Error, JournalLevel.Information, JournalLevel.Progress, JournalLevel.Verbose).forEach { level ->
                        graph.log(JournalRecord(level, "device-test", "$prefix-$level"))
                    }
                    val captured = graph.journal.snapshotText()
                    assertTrue(captured.contains("$prefix-Error"))
                    assertEquals(profile != JournalProfile.Errors, captured.contains("$prefix-Information"))
                    assertEquals(profile in setOf(JournalProfile.Progress, JournalProfile.Detailed), captured.contains("$prefix-Progress"))
                    assertEquals(profile == JournalProfile.Detailed, captured.contains("$prefix-Verbose"))
                    if (profile == JournalProfile.Errors) {
                        activity.onActivity { model.addUrls("не URL") }
                        await(5_000) {
                            model.ui.value.message?.text?.contains("Не найдено ссылок HTTP(S)") == true &&
                                graph.journal.snapshotText().contains("[Error/app]")
                        }
                        assertTrue("A real ViewModel validation failure must remain visible in Errors profile",
                            graph.journal.snapshotText().contains("Не найдено ссылок HTTP(S)"))
                        activity.onActivity { model.dismissMessage() }
                        await(5_000) { model.ui.value.message == null }
                    }
                }
                val secret = "private-$token"
                val public = "https://www.youtube.com/watch?v=AbC&list=PL123&token=$secret"
                graph.log(JournalRecord(JournalLevel.Error, "device-test",
                    "$token first line\nsecond line\n$public\nhttps://www.pornhub.com/view_video.php?viewkey=PublicKey&signature=$secret\nAuthorization: Bearer $secret\n.example.org\tTRUE\t/\tFALSE\t9999999999\tsession\t$secret\nliteral\\n stays literal",
                    JournalContext(session = "test-$token", job = "job-$token", videoSequence = 3, video = public)))
                await(5_000) { model.ui.value.logs.any { it.contains(token) && it.contains("first line") } }
                compose.onNodeWithTag("copyLogs").performClick()
                var clipboard = ""
                await(5_000) {
                    activity.onActivity { clipboard = (it.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager).primaryClip?.getItemAt(0)?.text?.toString().orEmpty() }
                    clipboard.contains("$token first line")
                }
                assertSafeDiagnostic(clipboard, secret, token)
                activity.onActivity { model.dismissMessage() }
                val uri = FileProvider.getUriForFile(context, "${context.packageName}.files", exported)
                assertEquals("content", uri.scheme)
                compose.onNodeWithTag("saveLogs").assertHasClickAction()
                // The actual VM and ContentResolver write a real provider URI, just as a picker result does.
                activity.onActivity { model.exportLogs(uri) }
                await(10_000) { exported.isFile && exported.length() > 0 && model.ui.value.message?.text == "Журнал сохранён." }
                val saved = context.contentResolver.openInputStream(uri)!!.bufferedReader().use { it.readText() }
                assertSafeDiagnostic(saved, secret, token)
                runBlocking(Dispatchers.IO) { graph.journalFiles.flush() }
                val files = requireNotNull(graph.journalFiles.file.parentFile).listFiles().orEmpty()
                    .filter { it.name.startsWith("journal") && it.name.endsWith(".log") }.joinToString("\n") { it.readText() }
                assertTrue("The asynchronous file sink must receive the sanitized record", files.contains("$token first line"))
                assertFalse(files.contains(secret))
                activity.onActivity { model.dismissMessage() }
                compose.onNodeWithTag("clearLogs").performClick()
                await(5_000) { model.ui.value.logs.isEmpty() }
                assertTrue("Clearing the visible journal must retain previously written files", graph.journalFiles.file.exists())
            }
        } finally {
            runBlocking(Dispatchers.IO) { graph.repository.update { it.copy(settings = previous) } }
            graph.journal.clear()
            exported.delete()
        }
    }

    @Test fun destroyingARealActiveServiceWaitsForNativeCancellationAndFreezesItsSession() {
        initialize()
        SessionLoopbackServer(mapOf("/interrupted.mp4" to padded(1024 * 1024)), mapOf("/interrupted.mp4" to 100L)).use { origin ->
            val token = "destroy-${UUID.randomUUID()}"
            val task = DownloadTask(url = "${origin.baseUrl}/interrupted.mp4", title = token)
            val previous = graph.repository.state.value.settings
            try {
                runBlocking(Dispatchers.IO) { graph.repository.update { it.copy(settings = settings(previous, token).copy(threads = 1), queue = it.queue + task) } }
                ActivityScenario.launch(MainActivity::class.java).use { activity ->
                    activity.onActivity { DownloadService.start(it, listOf(task.id)) }
                    await {
                        DownloadService.session.value?.active == true &&
                            (DownloadService.progress.value[task.id]?.downloadedBytes ?: 0) > 0
                    }
                    val sessionId = requireNotNull(DownloadService.session.value).id
                    var stopped = false
                    // This invokes onDestroy during transfer, rather than the orderly notification Stop path.
                    activity.onActivity { stopped = it.stopService(Intent(it, DownloadService::class.java)) }
                    assertTrue("The real foreground Service must exist before destruction", stopped)
                    await(30_000) {
                        val session = DownloadService.session.value
                        session != null && session.id == sessionId && !session.active &&
                            !DownloadService.isRunning.value && !DownloadService.hasOwnedTasks &&
                            graph.repository.state.value.queue.single { it.id == task.id }.status == DownloadStatus.INTERRUPTED
                    }
                    val final = requireNotNull(DownloadService.session.value)
                    assertEquals(1, final.cancelled)
                    assertEquals(0, final.completed)
                    assertEquals(0, final.running)
                    assertFalse(DownloadService.progress.value.containsKey(task.id))
                    val row = graph.repository.state.value.queue.single { it.id == task.id }
                    assertTrue("Destroyed native transfer must not publish any result", row.outputUris.isEmpty())
                    assertFalse(graph.repository.state.value.history.any { it.url == task.url })
                    runBlocking(Dispatchers.IO) { delay(1_200) }
                    assertEquals("Late callbacks after onDestroy must not restart the timer", final.elapsedMillis, DownloadService.session.value?.elapsedMillis)
                }
            } finally { cleanup(setOf(task.id), setOf(task.url), previous) }
        }
    }

    @Test fun actualAudioPostprocessingFailureDoesNotRestartTheTransferredMedia() {
        initialize()
        val invalid = "This is deliberately not a valid MP4 container.\n".repeat(4096).toByteArray()
        // GenericIE also probes a direct media URL with GET. Keep webpage
        // discovery separate so this counter measures only the media transfer.
        val page = """<!doctype html><html><head><title>Broken audio fixture</title></head><body><video controls><source src="/broken.mp4" type="video/mp4"></video></body></html>""".toByteArray()
        SessionLoopbackServer(mapOf("/broken.html" to page, "/broken.mp4" to invalid)).use { origin ->
            val token = UUID.randomUUID().toString()
            val task = DownloadTask(url = "${origin.baseUrl}/broken.html")
            val stage = File(context.cacheDir, "staging/processing-$token").apply { mkdirs() }
            val traces = CopyOnWriteArrayList<EngineTrace>()
            val samples = CopyOnWriteArrayList<EngineProgress>()
            val before = graph.repository.state.value.history
            try {
                val failure = runBlocking(Dispatchers.IO) {
                    runCatching {
                        withTimeout(90_000) {
                            graph.engine.download("processing-$token", task, settings(graph.repository.state.value.settings, token).copy(audioOnly = true), stage,
                                onTrace = { traces.add(it) }, onProgress = { samples.add(it) })
                        }
                    }.exceptionOrNull()
                }
                assertNotNull("An invalid audio source must fail real FFmpeg postprocessing", failure)
                assertFalse("The failure must come from the native operation, not a watchdog", failure is TimeoutCancellationException)
                assertTrue("Real media bytes must have arrived before postprocessing fails", samples.any { it.downloadedBytes > 0 })
                assertTrue("The processing boundary must be identified in a genuine native trace", traces.any { it.event == "processing_failed" })
                assertEquals("A processing error cannot retry the whole native request", 1, traces.count { it.event == "attempt_start" })
                assertFalse(traces.any { it.event == "retry_scheduled" || it.event == "quality_fallback" })
                assertEquals("The source webpage must be enumerated only once", 1, origin.getCount("/broken.html"))
                assertEquals("The completed HTTP transfer must not begin again", 1, origin.getCount("/broken.mp4"))
                assertTrue(samples.all { it.percent.isFinite() && it.percent in 0f..99f })
                assertEquals("A direct engine failure cannot manufacture successful history", before, graph.repository.state.value.history)
            } finally { stage.deleteRecursively() }
        }
    }

    private fun assertSafeDiagnostic(text: String, secret: String, token: String) {
        assertTrue(text.contains("$token first line\n    second line"))
        assertTrue("Literal backslash-n must not be converted indiscriminately", text.contains("literal\\n stays literal"))
        assertTrue("Public YouTube IDs must remain useful", text.contains("v=AbC&list=PL123"))
        assertTrue("The public Pornhub key must remain useful", text.contains("viewkey=PublicKey"))
        assertFalse("Secret headers, cookies and signed parameters must be absent", text.contains(secret))
        assertTrue(text.contains("[скрыто]"))
        assertFalse("No timezone suffix or textual record counter belongs in the timestamp", Regex("\\[\\d{4}-\\d{2}-\\d{2}[^]]*(?:[+]\\d{2}:\\d{2}|#\\d{3,})").containsMatchIn(text))
    }

    private fun hash(uri: String): String = context.contentResolver.openInputStream(Uri.parse(uri))!!.use { input ->
        val digest = MessageDigest.getInstance("SHA-256")
        val bytes = ByteArray(8192)
        while (true) { val count = input.read(bytes); if (count < 0) break; digest.update(bytes, 0, count) }
        digest.digest().joinToString("") { "%02x".format(it) }
    }

    private fun cleanup(ids: Set<String>, urls: Set<String>, settings: Settings) {
        if (DownloadService.isRunning.value) {
            DownloadService.stopAll(context)
        }
        await(30_000) { !DownloadService.isRunning.value && !DownloadService.hasOwnedTasks }
        runBlocking(Dispatchers.IO) {
            val state = graph.repository.state.value
            val ownTasks = state.queue.filter { it.id in ids }
            assertTrue(MediaPublisher(context).deleteOwned(ownTasks.flatMap { it.outputUris }).isEmpty())
            graph.repository.update { it.copy(settings = settings, queue = it.queue.filterNot { row -> row.id in ids }, history = it.history.filterNot { row -> row.url in urls }) }
            val root = File(context.filesDir, "staging").canonicalFile.toPath()
            ownTasks.filter { it.stageDirectory.isNotBlank() }.forEach { task ->
                val stage = File(task.stageDirectory)
                val path = stage.canonicalFile.toPath()
                if (path.startsWith(root) && path != root && path.fileName.toString() == task.id) stage.deleteRecursively()
            }
        }
    }
}
