package ru.bespalov.videodownloader

import android.content.Context
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.yausername.youtubedl_android.YoutubeDL
import com.yausername.youtubedl_android.YoutubeDLRequest
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import ru.bespalov.video.core.Settings
import ru.bespalov.videodownloader.engine.YtDlpArguments
import java.io.File
import java.util.UUID

@RunWith(AndroidJUnit4::class)
class AudioTrackInstrumentedTest {
    @get:Rule val compose = createEmptyComposeRule()
    private val context: Context get() = ApplicationProvider.getApplicationContext()
    private val graph: AppGraph get() = (context.applicationContext as DownloaderApplication).graph
    private fun initialize() = runBlocking(Dispatchers.IO) {
        withTimeout(90_000) { graph.ready.await() }
        assertTrue(graph.startupError.value, graph.runtimeReady.value)
    }

    @Test fun packagedYtDlpSelectsRequestedTracksWithoutDownloadingOrSubstitutingAnotherLanguage() = runBlocking(Dispatchers.IO) {
        initialize()
        val assets = InstrumentationRegistry.getInstrumentation().context.assets
        val source = assets.open("formats.json").bufferedReader().use { it.readText() }
        val cases = JSONArray(assets.open("cases.json").bufferedReader().use { it.readText() })
        val directory = File(context.cacheDir, "audio-cases-${UUID.randomUUID()}").apply { mkdirs() }
        val historyBefore = graph.repository.state.value.history
        try {
            for (index in 0 until cases.length()) {
                val case = cases.getJSONObject(index)
                val name = case.getString("name")
                val metadata = JSONObject(source)
                if (case.has("formats")) {
                    val subset = case.getJSONArray("formats")
                    val ids = (0 until subset.length()).map { subset.getString(it) }.toSet()
                    val formats = metadata.getJSONArray("formats")
                    metadata.put("formats", JSONArray((0 until formats.length()).map { formats.getJSONObject(it) }.filter { it.getString("format_id") in ids }))
                }
                val info = File(directory, "$name.json").apply { writeText(metadata.toString()) }
                val settings = Settings(audioTrack = case.optString("audioTrack", "Default"), audioLanguages = case.optString("audioLanguages", "ru"),
                    codec = case.optString("codec", "Auto"), audioOnly = case.optBoolean("audioOnly"), embedMetadata = false, embedChapters = false)
                val arguments = YtDlpArguments.download(settings, directory, case.getString("quality")) + listOf(
                    "--simulate", "--load-info-json", info.absolutePath, "--print", "video:__AUDIO_SELECTION__:%(format_id)s")
                val request = YoutubeDLRequest(emptyList<String>()).apply { addCommands(arguments) }
                val response = runCatching { YoutubeDL.execute(request, "audio-case-${UUID.randomUUID()}") }
                if (case.isNull("expected")) {
                    assertTrue("$name must fail instead of substituting another language", response.isFailure || response.getOrThrow().exitCode != 0)
                    val error = response.exceptionOrNull()?.message ?: response.getOrThrow().err
                    assertTrue("$name must fail because the requested format is unavailable: $error", error.contains("Requested format"))
                } else {
                    assertTrue("$name failed: ${response.exceptionOrNull()}", response.isSuccess)
                    assertEquals(name, 0, response.getOrThrow().exitCode)
                    assertTrue(name + ": " + response.getOrThrow().out, response.getOrThrow().out.lineSequence().any {
                        it.trim() == "__AUDIO_SELECTION__:${case.getString("expected")}"
                    })
                }
            }
            assertEquals("Format selection cannot write successful history", historyBefore, graph.repository.state.value.history)
        } finally { directory.deleteRecursively() }
    }

    @Test fun settingsScreenSavesLanguageSelectionWithoutStartingDownloads() {
        initialize()
        val previous = graph.repository.state.value.settings
        val queue = graph.repository.state.value.queue.map { Triple(it.id, it.status, it.outputUris) }
        try {
            runBlocking(Dispatchers.IO) { graph.repository.update { it.copy(settings = Settings()) } }
            ActivityScenario.launch(MainActivity::class.java).use {
                compose.onNodeWithTag("settingsTab").performClick()
                compose.onNodeWithTag("audioTrack").performScrollTo()
                compose.onNode(hasText("По умолчанию") and hasAnyAncestor(hasTestTag("audioTrack"))).performClick()
                compose.onNodeWithText("По языкам").performClick()
                compose.onNodeWithTag("audioLanguages").assertIsEnabled().performTextClearance()
                compose.onNodeWithTag("audioLanguages").performTextInput("ru,en")
                compose.onNodeWithTag("saveSettings").performScrollTo().performClick()
                compose.waitUntil(10_000) { graph.repository.state.value.settings.audioLanguages == "ru,en" }
                assertEquals("Languages", graph.repository.state.value.settings.audioTrack)
                assertEquals(queue, graph.repository.state.value.queue.map { task -> Triple(task.id, task.status, task.outputUris) })
            }
        } finally { runBlocking(Dispatchers.IO) { graph.repository.update { it.copy(settings = previous) } } }
    }
}
