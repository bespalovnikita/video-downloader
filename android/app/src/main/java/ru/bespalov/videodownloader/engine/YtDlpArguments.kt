package ru.bespalov.videodownloader.engine

import ru.bespalov.video.core.FilenameTemplates
import ru.bespalov.video.core.Settings
import ru.bespalov.video.core.SettingsRules
import java.io.File

/** Pure request construction, shared by production and contract tests. */
object YtDlpArguments {
    fun qualityLevels(settings: Settings): List<String> = when (settings.quality) {
        "2160" -> listOf("2160", "1440", "1080", "720")
        "1440" -> listOf("1440", "1080", "720")
        "1080" -> listOf("1080", "720")
        "720" -> listOf("720")
        else -> listOf("best")
    }

    fun metadata(): List<String> = listOf(
        "--ignore-config", "--encoding", "utf-8", "--no-warnings", "--skip-download",
        "--dump-single-json", "--playlist-items", "1", "--socket-timeout", "20",
    )

    fun download(settings: Settings, stage: File, quality: String): List<String> {
        SettingsRules.validate(settings)
        val args = mutableListOf(
            "--ignore-config", "--encoding", "utf-8", "--newline", "--progress", "--no-color",
            "--continue", "--windows-filenames", "--abort-on-error", "--socket-timeout", "20",
            "-N", settings.fragments.coerceIn(1, 8).toString(),
            "-o", File(stage, settings.filenameTemplate).absolutePath,
            "--progress-template", "download:__VD_PROGRESS__|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s|%(progress.downloaded_bytes)s|%(progress.total_bytes,progress.total_bytes_estimate)s|%(info.id)s:%(info.format_id)s",
            "--print", "before_dl:__VD_TITLE__:%(title)s",
            "--print", "after_move:__VD_FILE__:%(filepath)s",
        )
        if (settings.rateLimit.isNotBlank() && settings.rateLimit != "Unlimited")
            args += listOf("--limit-rate", settings.rateLimit)
        if (settings.sponsor) args += listOf("--sponsorblock-remove", "sponsor")
        if (settings.audioOnly) args += listOf("-x", "--audio-format", "mp3") else {
            args += listOf("-f", formatSelector(settings, quality))
            if (settings.container != "Auto") {
                require(settings.container in setOf("MP4", "MKV", "WebM")) { "Unknown container" }
                args += listOf("--merge-output-format", settings.container.lowercase(), "--remux-video", settings.container.lowercase())
            }
        }
        if (settings.writeSubs || settings.embedSubs) args += "--write-subs"
        if (settings.writeSubs || settings.embedSubs || settings.autoSubs) args += listOf("--sub-langs", settings.subtitleLangs)
        if (settings.autoSubs) args += "--write-auto-subs"
        if (settings.embedSubs) args += "--embed-subs"
        if (settings.embedThumbnail) args += listOf("--write-thumbnail", "--embed-thumbnail")
        if (settings.embedMetadata) args += "--embed-metadata"
        if (settings.embedChapters) args += "--embed-chapters"
        // Android's bundled Python has no curl_cffi. An unconditional
        // --impersonate chrome would make otherwise supported requests fail.
        return args
    }

    private fun formatSelector(settings: Settings, quality: String): String {
        val filter = if (quality == "best") "" else "[height<=$quality]"
        val code = when (settings.codec) { "H264" -> "avc1"; "VP9" -> "vp9"; "AV1" -> "av01"; else -> "" }
        val generic = "bv*$filter+ba/b$filter"
        return if (code.isBlank()) generic else "bv*$filter[vcodec^=$code]+ba/b$filter[vcodec^=$code]/$generic"
    }
}

sealed interface MarkerEvent {
    data class Title(val value: String) : MarkerEvent
    data class Media(val path: String) : MarkerEvent
    data class Progress(val value: EngineProgress, val component: String) : MarkerEvent
}

object ProgressMarkers {
    fun parse(line: String): MarkerEvent? = when {
        line.startsWith("__VD_TITLE__:") -> MarkerEvent.Title(line.removePrefix("__VD_TITLE__:").trim())
        line.startsWith("__VD_FILE__:") -> MarkerEvent.Media(line.removePrefix("__VD_FILE__:").trim())
        line.startsWith("__VD_PROGRESS__|") -> {
            val parts = line.split('|', limit = 7)
            if (parts.size != 7) null else MarkerEvent.Progress(
                EngineProgress(
                    percent = parts[1].trim().removeSuffix("%").toFloatOrNull()?.takeIf(Float::isFinite)?.coerceIn(0f, 100f) ?: 0f,
                    speed = parts[2].trim(), downloadedBytes = parts[4].toLongOrNull()?.coerceAtLeast(0) ?: 0,
                    totalBytes = parts[5].toLongOrNull()?.coerceAtLeast(0) ?: 0, eta = parts[3].trim(),
                ), parts[6],
            )
        }
        else -> null
    }
}
