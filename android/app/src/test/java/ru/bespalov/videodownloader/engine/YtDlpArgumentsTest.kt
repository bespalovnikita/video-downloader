package ru.bespalov.videodownloader.engine

import org.junit.Assert.*
import org.junit.Test
import ru.bespalov.video.core.Settings
import java.io.File

class YtDlpArgumentsTest {
    private val stage = File("/private/staging/owned")

    @Test fun downloadUsesStructuredProgressAndPrivateOutputWithoutDesktopImpersonation() {
        val arguments = YtDlpArguments.download(Settings(), stage, "best")
        assertTrue(arguments.containsAll(listOf("--ignore-config", "--progress", "--continue", "--abort-on-error")))
        assertEquals("download:__VD_PROGRESS__|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s|%(progress.downloaded_bytes)s|%(progress.total_bytes,progress.total_bytes_estimate)s|%(info.id)s:%(info.format_id)s",
            arguments[arguments.indexOf("--progress-template") + 1])
        assertEquals("/private/staging/owned/%(title)s [%(id)s].%(ext)s", arguments[arguments.indexOf("-o") + 1])
        assertTrue(arguments.contains("after_move:__VD_FILE__:%(filepath)s"))
        assertFalse(arguments.contains("--impersonate"))
        assertFalse(arguments.contains("--simulate"))
        assertFalse(arguments.contains("--skip-download"))
    }

    @Test fun metadataCannotDownloadAndLimitsCollectionPreviewToFirstItem() {
        val arguments = YtDlpArguments.metadata()
        assertTrue(arguments.containsAll(listOf("--ignore-config", "--skip-download", "--dump-single-json")))
        assertEquals("1", arguments[arguments.indexOf("--playlist-items") + 1])
        assertFalse(arguments.contains("--flat-playlist"))
        assertFalse(arguments.contains("--impersonate"))
    }

    @Test fun codecFallbackKeepsResolutionBoundAndContainerAppliesToMergeAndRemux() {
        val settings = Settings(quality = "1080", codec = "H264", container = "MP4", fragments = 7)
        val arguments = YtDlpArguments.download(settings, stage, "1080")
        assertEquals("7", arguments[arguments.indexOf("-N") + 1])
        assertEquals("bv*[height<=1080][vcodec^=avc1]+ba/b[height<=1080][vcodec^=avc1]/bv*[height<=1080]+ba/b[height<=1080]",
            arguments[arguments.indexOf("-f") + 1])
        assertEquals("mp4", arguments[arguments.indexOf("--merge-output-format") + 1])
        assertEquals("mp4", arguments[arguments.indexOf("--remux-video") + 1])
        assertEquals(listOf("1080", "720"), YtDlpArguments.qualityLevels(settings))
    }

    @Test fun audioAndSubtitlesHaveActualNativeArguments() {
        val arguments = YtDlpArguments.download(Settings(audioOnly = true, autoSubs = true, embedSubs = true,
            subtitleLangs = "ru.*,en.*", embedThumbnail = true, rateLimit = "5M", sponsor = true), stage, "best")
        assertTrue(arguments.containsAll(listOf("-x", "--audio-format", "mp3", "--write-auto-subs", "--write-subs",
            "--embed-subs", "--embed-thumbnail", "--embed-metadata", "--embed-chapters", "--sponsorblock-remove")))
        assertEquals("ru.*,en.*", arguments[arguments.indexOf("--sub-langs") + 1])
        assertEquals("5M", arguments[arguments.indexOf("--limit-rate") + 1])
        assertFalse(arguments.contains("--remux-video"))
        assertFalse(arguments.contains("-f"))
    }

    @Test fun unsafePinnedSettingsFailBeforeAnyNativeLaunch() {
        for (settings in listOf(Settings(filenameTemplate = "../../other/file.mp4"), Settings(fragments = 0),
            Settings(container = "unknown"), Settings(rateLimit = "--exec"))) {
            assertThrows(IllegalArgumentException::class.java) { YtDlpArguments.download(settings, stage, "best") }
        }
    }

    @Test fun progressAcceptsRealMarkersAndCannotCreateNonFiniteOrNegativeUiValues() {
        val event = ProgressMarkers.parse("__VD_PROGRESS__| 12.5% | 1.00MiB/s | 00:14 |123|1000|video:137") as MarkerEvent.Progress
        assertEquals(12.5f, event.value.percent, 0.001f)
        assertEquals(123L, event.value.downloadedBytes)
        assertEquals(1000L, event.value.totalBytes)
        assertEquals("video:137", event.component)
        assertEquals("00:14", event.value.eta)
        for (percent in listOf("NaN", "Infinity", "-Infinity", "N/A")) {
            val malformed = ProgressMarkers.parse("__VD_PROGRESS__|$percent|N/A|N/A|-2|N/A|id") as MarkerEvent.Progress
            assertEquals(0f, malformed.value.percent, 0f)
            assertEquals(0L, malformed.value.downloadedBytes)
            assertEquals(0L, malformed.value.totalBytes)
        }
        assertNull(ProgressMarkers.parse("[download] 100% completed"))
        assertNull(ProgressMarkers.parse("__VD_PROGRESS__|25%"))
        assertEquals(MarkerEvent.Title("Русское имя"), ProgressMarkers.parse("__VD_TITLE__:Русское имя"))
        assertEquals(MarkerEvent.Media("/private/staging/owned/clip.mp4"), ProgressMarkers.parse("__VD_FILE__:/private/staging/owned/clip.mp4"))
    }
}
