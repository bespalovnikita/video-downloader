package ru.bespalov.video.core

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import java.net.URI
import java.util.Locale

/** Only these adapters prove that a bounded prefix contains the newest publications. */
object RecentScanParser {
    const val RECENT_WINDOW_SIZE = 20
    private val youtubePath = Regex("^/(?:@[^/]+|(?:channel|c|user)/[^/]+)/(?:videos|shorts|streams)/?$")
    private val youtubeId = Regex("^[A-Za-z0-9_-]{11}$")

    fun isNewestYouTubeTab(url: String): Boolean = try {
        val uri = URI(url)
        val scheme = uri.scheme?.lowercase(Locale.ROOT)
        Urls.isHttp(url) && uri.host?.lowercase(Locale.ROOT) in setOf("youtube.com", "www.youtube.com", "m.youtube.com") &&
            uri.port in setOf(-1, if (scheme == "https") 443 else 80) && uri.rawQuery == null && uri.rawFragment == null &&
            youtubePath.matches(uri.rawPath.orEmpty())
    } catch (_: Exception) { false }

    fun eligible(sourceUrl: String, initialized: Boolean, knownUrls: Set<String>, forceFull: Boolean = false): Boolean =
        !forceFull && initialized && knownUrls.isNotEmpty() &&
            (isNewestYouTubeTab(sourceUrl) || PornHubCollections.listingUrl(sourceUrl) != null)

    fun parse(json: String, sourceUrl: String, maxPages: Int = 500): FullScan {
        require(maxPages in 1..500 && Urls.isHttp(sourceUrl)) { "Некорректный источник или предел проверки окна." }
        require(json.toByteArray(Charsets.UTF_8).size <= ScanParser.MAX_METADATA_BYTES) { "Метаданные превышают 32 МиБ." }
        val root = Json.parseToJsonElement(json) as? JsonObject ?: error("Ответ не содержит окно публикаций.")
        complete(root)
        if (PornHubCollections.listingUrl(sourceUrl) != null)
            return PornHubCollections.parse(root, sourceUrl, RECENT_WINDOW_SIZE,
                minOf(maxPages, RECENT_WINDOW_SIZE), RECENT_WINDOW_SIZE)
        require(isNewestYouTubeTab(sourceUrl)) { "Источник не гарантирует порядок новых публикаций." }
        require(root.text("extractor_key") == "YoutubeTab" && root.text("webpage_url") == sourceUrl) {
            "Не подтверждено окно публикаций выбранного YouTube-источника."
        }
        val entries = root["entries"] as? JsonArray ?: error("Нет списка новых публикаций YouTube.")
        require(entries.size <= RECENT_WINDOW_SIZE) { "Окно публикаций превышает 20 материалов." }
        for (field in listOf("playlist_count", "n_entries")) {
            root[field]?.takeIf { it != JsonNull }?.let { count ->
                require((count.scanLong() ?: -1) >= entries.size) { "Некорректное количество публикаций YouTube." }
            }
        }
        root["requested_entries"]?.takeIf { it != JsonNull }?.let { requested ->
            require(requested is JsonArray && requested.size == entries.size && requested.withIndex().all {
                it.value.scanInt() == it.index + 1
            }) { "Окно YouTube пропустило публикации или нарушило их порядок." }
        }
        val seen = hashSetOf<String>()
        val found = entries.map { raw ->
            val item = raw as? JsonObject ?: error("Окно YouTube содержит недоступный материал.")
            complete(item)
            require("entries" !in item && item.text("_type") in setOf("url", "video") &&
                (item.text("ie_key") == "Youtube" || item.text("extractor_key") == "Youtube") &&
                item.text("availability") in setOf("", "public", "unlisted")) {
                "Окно YouTube содержит нераскрытую коллекцию, недоступный или неизвестный материал."
            }
            val id = item.text("id")
            require(youtubeId.matches(id)) { "Некорректный ID публикации YouTube." }
            val canonical = "https://www.youtube.com/watch?v=$id"
            var address = item.text("webpage_url").ifBlank { item.text("url") }
            if (address.isEmpty() || address == id) address = canonical
            // Only new discovery URLs from this validated flat adapter are formed
            // like YoutubeIE full discovery. Never rewrite persisted/user URLs.
            require(address in setOf(canonical, "https://www.youtube.com/shorts/$id", "https://www.youtube.com/live/$id") &&
                seen.add(canonical)) { "Окно YouTube содержит повторный или посторонний URL." }
            DiscoveredMedia(canonical, item.text("title").ifBlank { canonical })
        }
        val title = root.text("title").ifBlank { sourceUrl }
        val channel = root.text("channel_id")
        val feed = if (Regex("^UC[A-Za-z0-9_-]{22}$").matches(channel))
            "https://www.youtube.com/feeds/videos.xml?channel_id=$channel" else ""
        val suggested = listOf(root.text("channel"), root.text("uploader"), title).firstOrNull(String::isNotBlank).orEmpty()
        return FullScan(title, found, feed, suggestedName = suggested, scope = ScanScope.RecentWindow)
    }

    private fun complete(value: JsonObject) {
        for (name in listOf("_error", "incomplete", "_incomplete", "truncated")) {
            val flag = value[name]
            require(flag == null || flag == JsonNull || flag == JsonPrimitive(false)) { "Окно публикаций содержит ошибку." }
        }
    }
}
