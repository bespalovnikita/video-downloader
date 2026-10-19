package com.bespalovnikita.videodownloader.downloader

import com.bespalovnikita.videodownloader.model.DownloadSettings
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class DownloadPolicyTest {
    @Test
    fun h2641080KeepsGenericFallback() {
        val selector = DownloadPolicy.formatSelector(
            DownloadSettings(quality = "1080", videoCodec = "H264")
        )
        assertTrue(selector.contains("vcodec^=avc1"))
        assertTrue(selector.contains("bv*[height<=1080]+ba"))
    }

    @Test
    fun autoBestHasSimpleFallback() {
        val selector = DownloadPolicy.formatSelector(
            DownloadSettings(quality = "Best", videoCodec = "Auto")
        )
        assertTrue(selector.contains("bv*+ba"))
    }

    @Test
    fun transientClassifierDoesNotRetryPermanentErrors() {
        assertTrue(DownloadPolicy.isTransientError("HTTP Error 429: Too Many Requests"))
        assertTrue(DownloadPolicy.isTransientError("connection reset by peer"))
        assertFalse(DownloadPolicy.isTransientError("Video unavailable"))
        assertFalse(DownloadPolicy.isTransientError("Sign in to confirm your age"))
    }
}
