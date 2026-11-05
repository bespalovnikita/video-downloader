package ru.bespalov.videodownloader.engine

/** Failed empty previews may be retried by a later request; this never schedules work. */
object PreviewCachePolicy {
    const val READY_TTL_MILLIS = 72 * 60 * 60_000L
    const val FAILURE_RETRY_MILLIS = 60_000L

    fun isFresh(value: EngineMetadata, now: Long): Boolean {
        val lifetime = if (value.images.isEmpty() && value.previewError.isNotBlank())
            FAILURE_RETRY_MILLIS else READY_TTL_MILLIS
        if (now < value.savedAtMillis) return false
        return now - value.savedAtMillis in 0 until lifetime
    }
}

/** Only request context is reported; response bodies and cookies never enter diagnostics. */
data class PreviewDiagnostic(
    val stage: String,
    val videoUrl: String,
    val candidateUrl: String = "",
    val imageIndex: Int = 0,
    val httpStatus: Int? = null,
    val bytes: Int? = null,
    val reason: String,
)
