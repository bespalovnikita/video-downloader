package ru.bespalov.videodownloader.engine

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.intOrNull
import ru.bespalov.video.core.Settings
import ru.bespalov.video.core.AudioTracks
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
            "--ignore-config", "--encoding", "utf-8", "--newline", "--progress", "--no-color", "--no-quiet",
            "--continue", "--windows-filenames", "--abort-on-error", "--socket-timeout", "20",
            "-N", settings.fragments.coerceIn(1, 8).toString(),
            "-o", File(stage, settings.filenameTemplate).absolutePath,
            "--progress-template", "download:__VD_PROGRESS__|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s|%(progress.downloaded_bytes)s|%(progress.total_bytes,progress.total_bytes_estimate)s|%(info.id)s:%(info.format_id)s|%(info.vcodec)s|%(info.acodec)s",
            "--print", "before_dl:__VD_TITLE__:%(title)s",
            // A full requested_formats dump contains signed media URLs. Retain only IDs/counts.
            "--print", "before_dl:__VD_PLAN__:%(.{id,format_id,requested_formats.:.format_id,playlist_autonumber,n_entries})j",
            "--print", "after_move:__VD_FILE__:%(filepath)s",
        )
        if (settings.rateLimit.isNotBlank() && settings.rateLimit != "Unlimited")
            args += listOf("--limit-rate", settings.rateLimit)
        if (settings.sponsor) args += listOf("--sponsorblock-remove", "sponsor")
        AudioTracks.formatSelector(settings, quality)?.let { args += listOf("-f", it) }
        if (settings.audioOnly) args += listOf("-x", "--audio-format", "mp3") else {
            if (AudioTracks.multiple(settings)) args += "--audio-multistreams"
            val container = AudioTracks.container(settings)
            if (container != "Auto") {
                args += listOf("--merge-output-format", container.lowercase(), "--remux-video", container.lowercase())
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

}

sealed interface MarkerEvent {
    data class Title(val value: String) : MarkerEvent
    data class Media(val path: String) : MarkerEvent
    data class Plan(val mediaId: String, val formatIds: List<String>, val mediaIndex: Int = 0, val mediaCount: Int = 0) : MarkerEvent
    data class Progress(
        val value: EngineProgress,
        val component: String,
        val rawPercent: String = "",
        val videoCodec: String = "",
        val audioCodec: String = "",
        val downloadedBytesKnown: Boolean = true,
    ) : MarkerEvent
}

object ProgressMarkers {
    fun parse(line: String): MarkerEvent? = when {
        line.startsWith("__VD_TITLE__:") -> MarkerEvent.Title(line.removePrefix("__VD_TITLE__:").trim())
        line.startsWith("__VD_FILE__:") -> MarkerEvent.Media(line.removePrefix("__VD_FILE__:").trim())
        line.startsWith("__VD_PLAN__:") -> parsePlan(line.removePrefix("__VD_PLAN__:"))
        line.startsWith("__VD_PROGRESS__|") -> {
            val parts = line.split('|', limit = 9)
            val raw = parts.getOrNull(1)?.trim()?.removeSuffix("%").orEmpty()
            val percent = raw.toFloatOrNull()?.takeIf(Float::isFinite)?.coerceIn(0f, 100f)
            if (parts.size < 7) null else MarkerEvent.Progress(
                EngineProgress(
                    percent = percent ?: 0f, componentPercent = percent,
                    speed = parts[2].trim(), downloadedBytes = parts[4].toLongOrNull()?.coerceAtLeast(0) ?: 0,
                    totalBytes = parts[5].toLongOrNull()?.coerceAtLeast(0) ?: 0, eta = parts[3].trim(),
                ), parts[6], raw, parts.getOrNull(7).orEmpty(), parts.getOrNull(8).orEmpty(),
                parts[4].toLongOrNull()?.let { it >= 0 } == true,
            )
        }
        else -> null
    }

    fun isPostprocessing(line: String): Boolean = postprocessing.containsMatchIn(line.trimStart())

    private fun parsePlan(json: String): MarkerEvent.Plan? {
        if (json.length > 32768 || !boundedDepth(json, 8)) return null
        return try {
            val root = Json.parseToJsonElement(json) as? JsonObject ?: return null
            fun text(name: String): String = (root[name] as? JsonPrimitive)?.takeIf { it.isString }?.content.orEmpty()
            // Extractor-specific or unavailable playlist positions must not
            // discard an otherwise usable video/audio component plan.
            fun number(name: String): Int = (root[name] as? JsonPrimitive)
                ?.takeUnless { it.isString }?.intOrNull?.coerceAtLeast(0) ?: 0
            val formats = (root["requested_formats.:.format_id"] as? JsonArray).orEmpty()
                .asSequence().take(32).mapNotNull { (it as? JsonPrimitive)?.takeIf { value -> value.isString }?.content }
                .filter(String::isNotBlank).distinct().toList().ifEmpty { listOfNotNull(text("format_id").takeIf(String::isNotBlank)) }
            var index = number("playlist_autonumber")
            var count = number("n_entries")
            if (index > count || index == 0 || count == 0) { index = 0; count = 0 }
            MarkerEvent.Plan(text("id"), formats, index, count)
        } catch (_: Exception) { null }
    }

    /** Bound nesting before parsing, including malformed JSON, without counting brackets in strings. */
    private fun boundedDepth(text: String, maximum: Int): Boolean {
        var depth = 0
        var quoted = false
        var escaped = false
        for (character in text) {
            if (quoted) {
                if (escaped) escaped = false
                else when (character) { '\\' -> escaped = true; '"' -> quoted = false }
            } else when (character) {
                '"' -> quoted = true
                '{', '[' -> { depth++; if (depth > maximum) return false }
                '}', ']' -> depth--
            }
        }
        return true
    }

    private val postprocessing = Regex(
        "^\\[(?:Merger|VideoRemuxer|VideoConvertor|ExtractAudio|Metadata|EmbedSubtitle|EmbedThumbnail|ModifyChapters|ThumbnailsConvertor|Fixup[^\\]]*)\\]|^ERROR:\\s*(?:Postprocessing:|ffmpeg\\b|ffprobe\\b)",
        RegexOption.IGNORE_CASE,
    )
}
