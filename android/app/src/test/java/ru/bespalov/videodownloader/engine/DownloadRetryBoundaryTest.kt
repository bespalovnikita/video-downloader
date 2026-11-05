package ru.bespalov.videodownloader.engine

import org.junit.Assert.*
import org.junit.Test

class DownloadRetryBoundaryTest {
    @Test fun transientRetryAndQualityFallbackRemainBoundedBeforeProcessing() {
        val boundary = DownloadRetryBoundary()
        assertEquals(DownloadRetryDecision.RETRY, boundary.decide("connection reset", 1, true))
        assertEquals(DownloadRetryDecision.RETRY, boundary.decide("HTTP Error 503", 3, true))
        assertEquals(DownloadRetryDecision.FAIL, boundary.decide("connection reset", 4, true))
        assertEquals(DownloadRetryDecision.LOWER_QUALITY, boundary.decide("Requested format is not available", 1, true))
        assertEquals(DownloadRetryDecision.FAIL, boundary.decide("Requested format is not available", 1, false))
        assertEquals(DownloadRetryDecision.FAIL, boundary.decide("HTTP Error 403; connection timed out", 1, true))
        assertEquals(DownloadRetryDecision.FAIL, boundary.decide("HTTP Error 403; Requested format is not available", 1, true))
        assertEquals(DownloadRetryDecision.FAIL, boundary.decide("private video: Requested format is not available", 1, true))
        assertEquals(DownloadRetryDecision.LOWER_QUALITY,
            boundary.decide("https://example.test/HTTP403-connection-reset Requested format is not available", 1, true))
    }

    @Test fun postprocessingDiagnosticsSealTheEntireNativeInvocation() {
        val lines = listOf("[Merger] Merging formats", "[VideoRemuxer] Remuxing", "[VideoConvertor] Converting",
            "[ExtractAudio] Extracting audio", "[Metadata] Adding metadata", "[EmbedSubtitle] Embedding",
            "[EmbedThumbnail] Embedding", "[ModifyChapters] Modifying", "[ThumbnailsConvertor] Converting",
            "[FixupM4a] Correcting container", "ERROR: Postprocessing: connection reset",
            "ERROR: ffmpeg exited with code 1", "ERROR: ffprobe returned an error")
        for (line in lines) {
            val boundary = DownloadRetryBoundary()
            boundary.observe(line, ProgressMarkers.parse(line))
            assertTrue(line, boundary.processingStarted)
            assertEquals(line, DownloadRetryDecision.FAIL, boundary.decide("connection reset", 1, true))
            assertEquals(line, DownloadRetryDecision.FAIL, boundary.decide("Requested format is not available", 1, true))
            // A following playlist material must not reopen the retry boundary.
            boundary.observe("", MarkerEvent.Plan("next", listOf("single"), 2, 3))
            assertEquals(line, DownloadRetryDecision.FAIL, boundary.decide("HTTP Error 503", 1, true))
        }
    }

    @Test fun afterMoveAndSuccessfulNativeReturnAlsoForbidRedownloading() {
        val media = DownloadRetryBoundary()
        media.observe("__VD_FILE__:/private/owned/clip.mp4", MarkerEvent.Media("/private/owned/clip.mp4"))
        assertEquals(DownloadRetryDecision.FAIL, media.decide("connection reset", 1, true))
        val verified = DownloadRetryBoundary()
        verified.nativeFinished()
        assertEquals(DownloadRetryDecision.FAIL, verified.decide("ffprobe connection timed out", 1, true))
        assertEquals(DownloadRetryDecision.FAIL, verified.decide("Requested format is not available", 1, true))
        assertEquals(DownloadRetryDecision.RETRY, DownloadRetryBoundary().decide("connection reset", 1, true))
    }

    @Test fun exceptionDiagnosticsProtectProcessingWhenCallbackDidNotContainTheLine() {
        val boundary = DownloadRetryBoundary()
        assertEquals(DownloadRetryDecision.FAIL, boundary.decide("warning\n  ERROR: Postprocessing: connection reset", 1, true))
        assertEquals(DownloadRetryDecision.FAIL, boundary.decide("ERROR: ffmpeg connection reset", 1, true))
        assertFalse(ProgressMarkers.isPostprocessing("[download] 100% of file"))
        assertFalse(ProgressMarkers.isPostprocessing("https://example.test/video/ffmpeg"))
    }
}
