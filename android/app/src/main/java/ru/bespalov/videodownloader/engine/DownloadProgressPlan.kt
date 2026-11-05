package ru.bespalov.videodownloader.engine

import ru.bespalov.video.core.ErrorText
import java.security.MessageDigest
import java.util.Locale

/** A component's 100% is not a verified, published file. Keep each fraction separate. */
internal class DownloadProgressPlan {
    private var plan: MarkerEvent.Plan? = null
    private var selectedComponents: Set<String> = emptySet()
    private val fractions = linkedMapOf<String, Float>()
    private val observed = linkedMapOf<String, Int>()

    fun reset() { plan = null; selectedComponents = emptySet(); fractions.clear(); observed.clear() }

    fun begin(value: MarkerEvent.Plan, previous: EngineProgress = EngineProgress()): EngineProgress {
        plan = value
        selectedComponents = if (value.mediaId.isNotBlank()) value.formatIds.take(32)
            .mapTo(linkedSetOf()) { "${value.mediaId}:$it" } else emptySet()
        fractions.clear()
        observed.clear()
        val knownPosition = value.mediaIndex > 0 && value.mediaCount > 0 && value.mediaIndex <= value.mediaCount
        return previous.copy(
            percent = if (knownPosition) 99f * (value.mediaIndex - 1) / value.mediaCount else 0f,
            componentPercent = null, component = "", componentIndex = null,
            componentCount = selectedComponents.size.takeIf { it > 0 }, planKnown = selectedComponents.isNotEmpty(),
            playlistIndex = value.mediaIndex.takeIf { knownPosition }, playlistCount = value.mediaCount.takeIf { knownPosition },
            detail = "", speed = "", eta = "", downloadedBytes = 0, totalBytes = 0, stage = EngineStage.STARTING,
        )
    }

    /** Subtitle, thumbnail or another material's hook cannot enlarge a known media plan. */
    fun accepts(sample: MarkerEvent.Progress): Boolean = selectedComponents.isEmpty() || sample.component in selectedComponents

    fun observe(sample: MarkerEvent.Progress, previous: EngineProgress): EngineProgress {
        if (!accepts(sample)) return previous
        // Unknown extractors may emit arbitrary component labels. Retain only
        // bounded, fixed-size tracking keys, rather than keeping their raw text.
        val key = if (selectedComponents.isNotEmpty()) sample.component else MessageDigest.getInstance("SHA-256")
            .digest(sample.component.toByteArray(Charsets.UTF_8)).joinToString("") { "%02x".format(it) }
        val part = observed[key] ?: (observed.size + 1).takeIf { observed.size < MAX_OBSERVED_COMPONENTS }
            ?.also { observed[key] = it }
        val selected = selectedComponents.size
        val parts = maxOf(selected, observed.size, 1)
        val raw = sample.value.componentPercent?.takeIf(Float::isFinite)?.coerceIn(0f, 100f)
        var percent = previous.percent.takeIf(Float::isFinite)?.coerceIn(0f, 99f) ?: 0f
        if (raw != null) {
            if (selected > 0) fractions[sample.component] = raw
            percent = if (selected > 0) fractions.values.sum() / parts * 0.99f else raw.coerceAtMost(99f)
            val current = plan
            if (current != null && current.mediaIndex > 0 && current.mediaCount > 0 && current.mediaIndex <= current.mediaCount)
                percent = 99f * (current.mediaIndex - 1 + percent / 99f) / current.mediaCount
        }
        val kind = when {
            codecPresent(sample.videoCodec) -> if (codecPresent(sample.audioCodec)) "Видео и аудио" else "Видео"
            codecPresent(sample.audioCodec) -> "Аудио"
            else -> "Поток"
        }
        val rawText = raw?.let { String.format(Locale.getDefault(), "%.1f%%", it) } ?: "?"
        val position = previous.playlistIndex?.let { "Материал $it/${previous.playlistCount} · " }.orEmpty()
        val detail = "$position$kind ${part ?: "?"}/${if (selected > 0) parts.toString() else "?"} · $rawText"
        return sample.value.copy(
            percent = percent.coerceIn(0f, 99f), title = previous.title,
            transferredBytes = previous.transferredBytes, componentPercent = raw, component = sample.component,
            detail = detail, stage = EngineStage.DOWNLOADING, attempt = previous.attempt, quality = previous.quality,
            playlistIndex = previous.playlistIndex, playlistCount = previous.playlistCount,
            componentIndex = part, componentCount = parts.takeIf { selected > 0 }, planKnown = selected > 0,
            invocation = previous.invocation,
        )
    }

    private fun codecPresent(value: String): Boolean = value.isNotBlank() && value.lowercase() !in setOf("none", "na", "n/a")

    private companion object { const val MAX_OBSERVED_COMPONENTS = 256 }
}

internal enum class DownloadRetryDecision { FAIL, RETRY, LOWER_QUALITY }

/** Once processing starts, restarting the whole request would redownload finished media. */
internal class DownloadRetryBoundary {
    var processingStarted: Boolean = false
        private set

    fun observe(line: String, marker: MarkerEvent?) {
        if (marker is MarkerEvent.Media || ProgressMarkers.isPostprocessing(line)) processingStarted = true
    }

    fun nativeFinished() { processingStarted = true }

    fun decide(diagnostic: String, attempt: Int, canLowerQuality: Boolean): DownloadRetryDecision {
        // An unavailable format cannot repair a denied or deleted source.
        // Ignore URL IDs when recognising HTTP statuses and human diagnostics.
        val requestDiagnostic = urls.replace(diagnostic, "")
        return when {
            processingStarted || diagnostic.lineSequence().any(ProgressMarkers::isPostprocessing) -> DownloadRetryDecision.FAIL
            permanentRequest.containsMatchIn(requestDiagnostic) -> DownloadRetryDecision.FAIL
            canLowerQuality && requestDiagnostic.contains("Requested format is not available", ignoreCase = true) -> DownloadRetryDecision.LOWER_QUALITY
            attempt < 4 && ErrorText.isTransient(diagnostic) -> DownloadRetryDecision.RETRY
            else -> DownloadRetryDecision.FAIL
        }
    }

    private companion object {
        val urls = Regex("https?://[^\\s<>]+", RegexOption.IGNORE_CASE)
        val permanentRequest = Regex(
            "\\bHTTP(?:/\\d(?:\\.\\d)?\\s+|\\s+(?:Error\\s+)?)(?:400|401|403|404|405|410|451)\\b|private video|sign in|login required|video unavailable|video has been removed",
            RegexOption.IGNORE_CASE,
        )
    }
}
