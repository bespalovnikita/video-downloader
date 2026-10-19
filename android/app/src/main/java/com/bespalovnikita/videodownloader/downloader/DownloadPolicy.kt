package com.bespalovnikita.videodownloader.downloader

import com.bespalovnikita.videodownloader.model.DownloadSettings

object DownloadPolicy {
    fun formatSelector(settings: DownloadSettings): String {
        val codecPrefix = when (settings.videoCodec) {
            "H264" -> "avc1"
            "VP9" -> "vp9"
            "AV1" -> "av01"
            else -> ""
        }

        val quality = settings.quality
        if (quality == "Best") {
            return if (codecPrefix.isNotBlank()) {
                "bv*[vcodec^=$codecPrefix]+ba/b[vcodec^=$codecPrefix]/bv*+ba/b"
            } else {
                "bv*+ba/b"
            }
        }

        return if (codecPrefix.isNotBlank()) {
            "bv*[height<=$quality][vcodec^=$codecPrefix]+ba/" +
                "b[height<=$quality][vcodec^=$codecPrefix]/" +
                "bv*[height<=$quality]+ba/b[height<=$quality]"
        } else {
            "bv*[height<=$quality]+ba/b[height<=$quality]"
        }
    }

    fun isTransientError(message: String): Boolean {
        val value = message.lowercase()
        return listOf(
            "timed out",
            "timeout",
            "connection reset",
            "connection refused",
            "temporary failure",
            "temporarily unavailable",
            "network is unreachable",
            "http error 429",
            "too many requests",
            "http error 500",
            "http error 502",
            "http error 503",
            "http error 504"
        ).any(value::contains)
    }
}
