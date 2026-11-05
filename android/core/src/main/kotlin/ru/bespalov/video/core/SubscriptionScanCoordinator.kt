package ru.bespalov.video.core

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withTimeout
import java.io.IOException

data class SubscriptionScanLimits(
    val scanTimeoutMillis: Long = 180_000,
    val channelScanTimeoutMillis: Long = 900_000,
    val recentTimeoutMillis: Long = 30_000,
    val channelRecentTimeoutMillis: Long = 45_000,
    val feedTimeoutMillis: Long = 12_000,
    val maxItems: Int = 10_000,
    val maxChannelPages: Int = 500,
    val maxMetadataBytes: Int = ScanParser.MAX_METADATA_BYTES,
    val maxFeedBytes: Int = RssParser.MAX_BYTES,
) {
    init {
        require(scanTimeoutMillis in 1..180_000 && channelScanTimeoutMillis in 1..900_000 &&
            recentTimeoutMillis in 1..30_000 && channelRecentTimeoutMillis in 1..45_000 &&
            feedTimeoutMillis in 1..12_000 && maxItems in 1..10_000 && maxChannelPages in 1..500 &&
            maxMetadataBytes in 1..ScanParser.MAX_METADATA_BYTES && maxFeedBytes in 1..RssParser.MAX_BYTES) {
            "Лимиты проверки подписки нельзя превышать."
        }
    }
}

data class ScanMetadata(val json: String, val diagnostics: String = "")

class ScanTimeoutException(message: String) : IOException(message)

/** The native adapter and tests run the same discovery/fallback/deadline policy. */
class SubscriptionScanCoordinator(private val limits: SubscriptionScanLimits = SubscriptionScanLimits()) {
    private val incomplete = Regex("\\bERROR:|\\bincomplete\\b|\\btruncat\\w*|\\bpartial (?:playlist|collection)|" +
        "\\b(?:unable|failed) to (?:download|extract|retrieve).*(?:page|playlist|entries)|\\bgiving up after", RegexOption.IGNORE_CASE)

    suspend fun scan(
        url: String,
        feedUrl: String? = null,
        initialized: Boolean = false,
        knownUrls: Set<String> = emptySet(),
        forceFull: Boolean = false,
        enumerate: suspend (ScanScope, timeoutMillis: Long) -> ScanMetadata,
        readFeed: suspend (String, timeoutMillis: Long) -> String,
        onProgress: (String) -> Unit = {},
    ): FullScan {
        require(Urls.isHttp(url)) { "Укажите HTTP(S) URL коллекции." }
        val special = PornHubCollections.listingUrl(url) != null
        val totalTimeout = if (special) limits.channelScanTimeoutMillis else limits.scanTimeoutMillis
        fun report(stage: String) { runCatching { onProgress(stage) } }
        suspend fun extract(scope: ScanScope, timeout: Long): FullScan {
            val metadata = enumerate(scope, timeout)
            currentCoroutineContext().ensureActive()
            require(metadata.json.toByteArray(Charsets.UTF_8).size.toLong() +
                metadata.diagnostics.toByteArray(Charsets.UTF_8).size <= limits.maxMetadataBytes) { "Метаданные превышают допустимый размер." }
            if (incomplete.containsMatchIn(metadata.diagnostics)) throw IOException(
                "Обход коллекции не завершён: " + JournalSanitizer.redact(metadata.diagnostics))
            report("Parsing")
            val result = if (scope == ScanScope.Full) ScanParser.parse(metadata.json, url, limits.maxItems, limits.maxChannelPages)
                else RecentScanParser.parse(metadata.json, url, limits.maxChannelPages)
            currentCoroutineContext().ensureActive()
            return result
        }
        try {
            return withTimeout(totalTimeout) {
                var result: FullScan? = null
                if (limits.maxItems >= RecentScanParser.RECENT_WINDOW_SIZE &&
                    RecentScanParser.eligible(url, initialized, knownUrls, forceFull)) {
                    report("RecentWindow")
                    val probeTimeout = if (special) limits.channelRecentTimeoutMillis else limits.recentTimeoutMillis
                    try {
                        val window = withTimeout(probeTimeout) { extract(ScanScope.RecentWindow, probeTimeout) }
                        // Twenty is a probe size, never a cap on the number of new items.
                        // No exact anchor means the missing gap requires a full scan.
                        if (window.items.any { it.url in knownUrls }) result = window
                    } catch (_: TimeoutCancellationException) {
                        currentCoroutineContext().ensureActive()
                    } catch (cancel: CancellationException) {
                        throw cancel
                    } catch (_: Exception) {
                        currentCoroutineContext().ensureActive()
                    }
                    if (result == null) report("FullFallback")
                }
                if (result == null) {
                    report("Enumerating")
                    result = extract(ScanScope.Full, totalTimeout)
                }
                currentCoroutineContext().ensureActive()
                val scan = checkNotNull(result)
                val feed = feedUrl?.takeIf(String::isNotBlank) ?: scan.feedUrl
                val used = if (scan.scope == ScanScope.Full) "полный обход" else "проверенное окно новых публикаций"
                var enriched = scan
                if (feed.isNotBlank()) {
                    if (!Urls.isHttp(feed)) enriched = scan.copy(feedUrl = feed, warning = "URL ленты некорректен; использован $used yt-dlp.")
                    else {
                        report("Feed")
                        try {
                            val xml = withTimeout(limits.feedTimeoutMillis) { readFeed(feed, limits.feedTimeoutMillis) }
                            require(xml.toByteArray(Charsets.UTF_8).size <= limits.maxFeedBytes) { "Лента превышает допустимый размер." }
                            enriched = RssParser.enrich(scan, RssParser.parse(xml, limits.maxItems)).copy(feedUrl = feed)
                        } catch (_: TimeoutCancellationException) {
                            currentCoroutineContext().ensureActive()
                            enriched = scan.copy(feedUrl = feed, warning = "Лента не ответила вовремя; использован $used yt-dlp.")
                        } catch (cancel: CancellationException) {
                            throw cancel
                        } catch (error: Exception) {
                            currentCoroutineContext().ensureActive()
                            enriched = scan.copy(feedUrl = feed, warning = "Лента недоступна; использован $used yt-dlp. " +
                                JournalSanitizer.redact(error.message ?: error.javaClass.simpleName))
                        }
                    }
                }
                currentCoroutineContext().ensureActive()
                report("Ready")
                currentCoroutineContext().ensureActive()
                enriched
            }
        } catch (timeout: TimeoutCancellationException) {
            // Own deadline failure is an error for this source, not user cancellation.
            currentCoroutineContext().ensureActive()
            throw ScanTimeoutException("Проверка источника не завершилась за отведённое время.").apply { initCause(timeout) }
        }
    }
}
