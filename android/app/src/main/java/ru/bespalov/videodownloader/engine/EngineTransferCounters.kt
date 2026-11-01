package ru.bespalov.videodownloader.engine

import java.security.MessageDigest

/**
 * One job/session owns this ledger across native retries and pause/resume. yt-dlp
 * reports absolute stream bytes, including an existing .part file when resumed.
 * Keep that baseline instead of adding the whole resumed file a second time.
 */
class EngineTransferCounters(private val maximumComponents: Int = 4096) {
    init { require(maximumComponents in 1..32768) }

    private val counters = LinkedHashMap<String, Long>(16, 0.75f, true)
    private var total = 0L
    private var trackingLimited = false

    val totalBytes: Long @Synchronized get() = total

    internal data class Observation(val totalBytes: Long, val reset: Boolean = false, val newlyLimited: Boolean = false)

    @Synchronized
    internal fun observe(component: String, downloadedBytes: Long): Observation {
        if (downloadedBytes < 0) return Observation(total)
        // Extractors control component IDs. A fixed-size digest bounds retained key memory too.
        val key = MessageDigest.getInstance("SHA-256").digest(component.toByteArray(Charsets.UTF_8))
            .joinToString("") { "%02x".format(it) }
        val previous = counters.put(key, downloadedBytes)
        val reset = previous != null && downloadedBytes < previous
        val delta = if (previous == null || reset) downloadedBytes else downloadedBytes - previous
        total = if (Long.MAX_VALUE - total < delta) Long.MAX_VALUE else total + delta
        var newlyLimited = false
        if (counters.size > maximumComponents) {
            counters.remove(counters.keys.first())
            newlyLimited = !trackingLimited
            trackingLimited = true
        }
        return Observation(total, reset, newlyLimited)
    }
}
