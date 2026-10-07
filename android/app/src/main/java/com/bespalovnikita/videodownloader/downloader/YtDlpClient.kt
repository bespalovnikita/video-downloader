package com.bespalovnikita.videodownloader.downloader

import com.bespalovnikita.videodownloader.model.DownloadProgress
import com.bespalovnikita.videodownloader.model.DownloadResult
import com.bespalovnikita.videodownloader.model.DownloadSettings
import com.bespalovnikita.videodownloader.model.PreviewData
import com.yausername.youtubedl_android.YoutubeDL
import com.yausername.youtubedl_android.YoutubeDLRequest
import org.json.JSONObject
import java.io.File
import java.util.concurrent.atomic.AtomicReference

class YtDlpClient {
    fun fetchPreview(url: String): PreviewData {
        val request = YoutubeDLRequest(url)
            .addOption("--dump-single-json")
            .addOption("--skip-download")
            .addOption("--no-warnings")
            .addOption("--playlist-items", "1")

        val response = YoutubeDL.getInstance().execute(request)
        val root = JSONObject(response.out.trim())
        val entries = root.optJSONArray("entries")
        val source = if (entries != null && entries.length() > 0) {
            entries.optJSONObject(0) ?: root
        } else root

        val formats = source.optJSONArray("formats")
        var maxHeight = 0
        var maxFps = 0.0
        val codecs = linkedSetOf<String>()
        val hdr = linkedSetOf<String>()

        if (formats != null) {
            for (i in 0 until formats.length()) {
                val format = formats.optJSONObject(i) ?: continue
                val vcodec = format.optString("vcodec")
                if (vcodec.isBlank() || vcodec == "none") continue

                maxHeight = maxOf(maxHeight, format.optInt("height", 0))
                maxFps = maxOf(maxFps, format.optDouble("fps", 0.0))

                when {
                    vcodec.startsWith("av01") -> codecs += "AV1"
                    vcodec.startsWith("vp9") -> codecs += "VP9"
                    vcodec.startsWith("avc1") || vcodec.startsWith("h264") -> codecs += "H264"
                    else -> codecs += vcodec.substringBefore('.')
                }

                val range = format.optString("dynamic_range")
                if (range.isNotBlank() && range != "SDR" && range != "None") hdr += range
            }
        }

        var thumbnail = source.optString("thumbnail")
        var thumbnailScore = -1L
        val thumbs = source.optJSONArray("thumbnails")
        if (thumbs != null) {
            for (i in 0 until thumbs.length()) {
                val t = thumbs.optJSONObject(i) ?: continue
                val candidate = t.optString("url")
                if (candidate.isBlank()) continue
                val score = t.optLong("width", 0L) * t.optLong("height", 0L)
                if (score >= thumbnailScore) {
                    thumbnail = candidate
                    thumbnailScore = score
                }
            }
        }

        return PreviewData(
            url = url,
            title = source.optString("title"),
            uploader = source.optString("uploader"),
            thumbnailUrl = thumbnail,
            durationSeconds = source.optLong("duration", 0),
            maxHeight = maxHeight,
            maxFps = maxFps,
            codecs = codecs.joinToString(", "),
            hdr = hdr.joinToString(", "),
            savedAtEpochMs = System.currentTimeMillis()
        )
    }

    fun download(
        url: String,
        settings: DownloadSettings,
        stagingDir: File,
        processId: String,
        onProgress: (DownloadProgress) -> Unit
    ): DownloadResult {
        stagingDir.mkdirs()
        val finalPath = AtomicReference("")

        val request = YoutubeDLRequest(url)
            .addOption("--newline")
            .addOption("--no-color")
            .addOption("--no-playlist")
            .addOption("--no-mtime")
            .addOption("-N", settings.fragments.coerceIn(1, 8))
            .addOption("-o", File(stagingDir, settings.filenameTemplate).absolutePath)
            .addOption("--print", "after_move:__VD_FILE__:%(filepath)s")

        if (settings.rateLimit.isNotBlank()) request.addOption("--limit-rate", settings.rateLimit)
        if (settings.sponsorBlock) request.addOption("--sponsorblock-remove", "sponsor")

        if (settings.audioOnly) {
            request.addOption("-x").addOption("--audio-format", "mp3")
        } else {
            request.addOption("-f", DownloadPolicy.formatSelector(settings))
            if (settings.container != "Auto") {
                request.addOption("--merge-output-format", settings.container.lowercase())
            }
        }

        if (settings.writeSubtitles || settings.embedSubtitles) {
            request.addOption("--write-subs")
            if (settings.subtitleLangs.isNotBlank()) request.addOption("--sub-langs", settings.subtitleLangs)
        }
        if (settings.writeAutoSubtitles) request.addOption("--write-auto-subs")
        if (settings.embedSubtitles) request.addOption("--embed-subs")
        if (settings.embedThumbnail) {
            request.addOption("--write-thumbnail")
            request.addOption("--embed-thumbnail")
        }
        if (settings.embedMetadata) request.addOption("--embed-metadata")
        if (settings.embedChapters) request.addOption("--embed-chapters")

        val response = YoutubeDL.getInstance().execute(
            request = request,
            processId = processId,
            redirectErrorStream = false
        ) { progress, eta, line ->
            if (line.startsWith("__VD_FILE__:")) {
                finalPath.set(line.removePrefix("__VD_FILE__:").trim())
            }
            onProgress(DownloadProgress(progress, eta, line))
        }

        if (finalPath.get().isBlank()) {
            response.out.lineSequence()
                .lastOrNull { it.startsWith("__VD_FILE__:") }
                ?.removePrefix("__VD_FILE__:")
                ?.trim()
                ?.let(finalPath::set)
        }

        if (finalPath.get().isBlank()) {
            stagingDir.walkTopDown()
                .filter { it.isFile && !it.name.endsWith(".part") && !it.name.endsWith(".ytdl") }
                .maxByOrNull { it.length() }
                ?.absolutePath
                ?.let(finalPath::set)
        }

        return DownloadResult(finalPath.get(), response.out)
    }

    fun stop(processId: String): Boolean = YoutubeDL.getInstance().destroyProcessById(processId)
}
