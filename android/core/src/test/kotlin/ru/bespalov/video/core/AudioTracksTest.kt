package ru.bespalov.video.core

import kotlinx.coroutines.runBlocking
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import org.junit.Assert.*
import org.junit.Test
import java.nio.file.Files

class AudioTracksTest {
    @Test fun oldSettingsRemainAutomaticAndProfilesPreserveAudioChoice() {
        val old = Json.decodeFromString<Settings>("{}")
        assertEquals("Default", old.audioTrack)
        assertEquals("bv*+ba/b", AudioTracks.formatSelector(old, "best"))
        assertNull(AudioTracks.formatSelector(old.copy(audioOnly = true), "best"))
        val selected = old.copy(audioTrack = "Languages", audioLanguages = "ru,en")
        for (profile in Profiles.names.keys) {
            val changed = Profiles.apply(selected, profile)
            assertEquals("Languages", changed.audioTrack)
            assertEquals("ru,en", changed.audioLanguages)
        }
        assertEquals(selected, Json.decodeFromString<Settings>(Json.encodeToString(selected)))
    }

    @Test fun invalidLanguagesAndUnsupportedContainersCannotReachNativeEngine() {
        for (language in listOf("", "ru,", "ru.*", "ru]+ba", "en\n", "--exec", "ru,en,fr,de,it,es,ja,ko,zh")) {
            assertThrows(IllegalArgumentException::class.java) { SettingsRules.validate(Settings(audioTrack = "Languages", audioLanguages = language)) }
        }
        for (settings in listOf(Settings(audioTrack = "unknown"),
            Settings(audioTrack = "Languages", audioLanguages = "ru,en", audioOnly = true),
            Settings(audioTrack = "Languages", audioLanguages = "ru,en", container = "MP4"),
            Settings(audioTrack = "Languages", audioLanguages = "ru,en", container = "WebM"))) {
            assertThrows(IllegalArgumentException::class.java) { SettingsRules.validate(settings) }
        }
        SettingsRules.validate(Settings(audioTrack = "Languages", audioLanguages = "pt-BR"))
        SettingsRules.validate(Settings(audioTrack = "Languages", audioLanguages = " RU, ru ", audioOnly = true))
    }

    @Test fun selectionAndPinnedJobSettingsSurviveRestartAndInvalidSavePreservesData() = runBlocking {
        val directory = Files.createTempDirectory("vd-audio-settings").toFile()
        try {
            val file = directory.resolve("state.json")
            val selected = Settings(audioTrack = "Languages", audioLanguages = "ru,en")
            val repository = JsonStateRepository(file)
            repository.update { it.copy(settings = selected, queue = listOf(DownloadTask(url = "https://example.com/video", settingsSnapshot = selected))) }
            val restored = JsonStateRepository(file)
            assertFalse(restored.readOnly)
            assertEquals(selected, restored.state.value.settings)
            assertEquals(selected, restored.state.value.queue.single().settingsSnapshot)
            val previous = file.readBytes()
            assertThrows(IllegalArgumentException::class.java) { runBlocking {
                restored.update { it.copy(settings = selected.copy(audioOnly = true)) }
            } }
            assertArrayEquals(previous, file.readBytes())
        } finally { directory.deleteRecursively() }
    }
}
