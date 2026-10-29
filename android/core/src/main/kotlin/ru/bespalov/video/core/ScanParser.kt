package ru.bespalov.video.core

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.longOrNull
import java.net.URI

internal fun JsonObject.text(name: String): String = (this[name] as? JsonPrimitive)?.takeIf { it.isString }?.content.orEmpty()

/** Full yt-dlp enumeration; preview metadata and arbitrary flat entries are not scans. */
object ScanParser {
    const val MAX_METADATA_BYTES = 32 * 1024 * 1024

    fun parse(json: String, sourceUrl: String, maxItems: Int = 10_000): FullScan {
        require(maxItems in 1..10_000 && Urls.isHttp(sourceUrl)) { "Некорректный источник или предел проверки." }
        require(json.toByteArray(Charsets.UTF_8).size <= MAX_METADATA_BYTES) { "Метаданные превышают 32 МиБ." }
        val root = Json.parseToJsonElement(json) as? JsonObject ?: error("Ответ не содержит коллекцию.")
        if (PornHubCollections.listingUrl(sourceUrl) != null) return PornHubCollections.parse(root, sourceUrl, maxItems)
        val found = linkedMapOf<String, DiscoveredMedia>()
        var enumerated = 0
        var collections = 0
        fun complete(value: JsonObject) {
            for (name in listOf("_error", "incomplete", "_incomplete", "truncated")) {
                val flag = value[name]
                require(flag == null || flag == JsonNull || flag == JsonPrimitive(false)) { "Источник сообщает о неполном обходе." }
            }
        }
        fun collection(value: JsonObject, depth: Int) {
            require(depth <= 64 && ++collections <= 10_000) { "Вложенность или размер коллекции превышает предел." }
            complete(value)
            val entries = value["entries"] as? JsonArray ?: error("Коллекция не перечислена полностью.")
            require(entries.size <= maxItems) { "В источнике больше допустимого числа материалов." }
            for (field in listOf("playlist_count", "n_entries")) {
                val count = value[field]
                if (count != null && count != JsonNull) require((count as? JsonPrimitive)?.longOrNull == entries.size.toLong()) {
                    "Количество материалов указывает на неполный обход."
                }
            }
            value["requested_entries"]?.takeIf { it != JsonNull }?.let { requested ->
                require(requested is JsonArray && requested.size == entries.size && requested.withIndex().all {
                    (it.value as? JsonPrimitive)?.intOrNull == it.index + 1
                }) { "Обход вернул только часть коллекции." }
            }
            entries.forEach { raw ->
                val entry = raw as? JsonObject ?: error("Обход содержит недоступный материал.")
                complete(entry)
                val type = entry.text("_type")
                if ("entries" in entry || type in setOf("playlist", "multi_video", "compat_list")) {
                    collection(entry, depth + 1)
                } else {
                    require(type !in setOf("url", "url_transparent")) { "Вложенный источник не раскрыт до материалов." }
                    require(++enumerated <= maxItems) { "В источнике больше допустимого числа материалов." }
                    require(entry.text("availability") in setOf("", "public", "unlisted")) { "В коллекции есть недоступный материал." }
                    var url = listOf(entry.text("webpage_url"), entry.text("url")).firstOrNull(Urls::isHttp).orEmpty()
                    if (url.isEmpty() && (entry.text("ie_key") == "Youtube" || entry.text("extractor_key") == "Youtube")) {
                        val id = entry.text("id")
                        if (Regex("^[A-Za-z0-9_-]{11}$").matches(id)) url = "https://www.youtube.com/watch?v=$id"
                    }
                    require(Urls.isHttp(url)) { "Материал не содержит корректного HTTP(S) URL." }
                    found.putIfAbsent(url, DiscoveredMedia(url, entry.text("title").ifEmpty { url }))
                }
            }
        }
        collection(root, 0)
        val title = root.text("title").ifEmpty { sourceUrl }
        var feed = ""
        val host = URI(sourceUrl).host.lowercase()
        if (host == "youtube.com" || host.endsWith(".youtube.com")) {
            val channel = listOf(root.text("channel_id"), root.text("uploader_id"))
                .firstOrNull { Regex("^UC[A-Za-z0-9_-]{22}$").matches(it) }
            if (channel != null) feed = "https://www.youtube.com/feeds/videos.xml?channel_id=$channel"
        }
        val suggested = listOf(root.text("channel"), root.text("uploader"), root.text("title")).firstOrNull(String::isNotBlank).orEmpty()
        return FullScan(title, found.values.toList(), feed, suggestedName = suggested)
    }
}

object PornHubCollections {
    private val hostPattern = Regex("^(?:[a-z0-9-]+\\.)?pornhub(?:premium)?\\.(?:com|net|org)$", RegexOption.IGNORE_CASE)
    private val pathPattern = Regex("^/(channels|model|pornstar|users)/([A-Za-z0-9_-]+)(?:/videos(?:/upload)?)?/?$")

    fun listingUrl(url: String): String? = try {
        val uri = URI(url)
        val path = pathPattern.matchEntire(uri.rawPath)
        if (!Urls.isHttp(url) || !hostPattern.matches(uri.host) ||
            (uri.port != -1 && uri.port != if (uri.scheme.lowercase() == "https") 443 else 80) || path == null) null
        else "https://${uri.host.lowercase()}/${path.groupValues[1]}/${path.groupValues[2]}/videos" +
            if (path.groupValues[1] == "channels") "" else "/upload"
    } catch (_: Exception) { null }

    internal fun parse(root: JsonObject, sourceUrl: String, maxItems: Int): FullScan {
        val proof = root["vd_ph_scan"] as? JsonObject ?: error("Нет подтверждения полноты публикаций Pornhub.")
        require((proof["version"] as? JsonPrimitive)?.intOrNull == 1 &&
            (proof["complete"] as? JsonPrimitive)?.booleanOrNull == true &&
            (proof["verified_first_page"] as? JsonPrimitive)?.booleanOrNull == true &&
            proof.text("source_url") == sourceUrl && proof.text("listing_url") == listingUrl(sourceUrl)) {
            "Полнота списка публикаций Pornhub не подтверждена."
        }
        val entries = root["entries"] as? JsonArray ?: error("Некорректный список публикаций.")
        val pages = proof["pages"] as? JsonArray ?: error("Нет списка страниц публикаций.")
        val count = (proof["item_count"] as? JsonPrimitive)?.intOrNull ?: -1
        require(count in 0..maxItems && count == entries.size && pages.size in 1..500) { "Список публикаций обрезан или превышает предел." }
        val pageCounts = pages.mapIndexed { index, raw ->
            val page = raw as? JsonObject ?: error("Некорректная страница публикаций.")
            val number = (page["number"] as? JsonPrimitive)?.intOrNull
            val size = (page["count"] as? JsonPrimitive)?.intOrNull ?: -1
            require(number == index + 1 && size >= 0 && (size != 0 || (count == 0 && pages.size == 1)) &&
                (page["has_next"] as? JsonPrimitive)?.booleanOrNull == (index + 1 < pages.size)) { "Нарушена последовательность страниц Pornhub." }
            size
        }.toMutableList()
        val host = Regex("pornhub(?:premium)?\\.(?:com|net|org)$", RegexOption.IGNORE_CASE).find(URI(sourceUrl).host)!!.value.lowercase()
        val seen = hashSetOf<String>()
        val result = entries.map { raw ->
            val item = raw as? JsonObject ?: error("Некорректный материал публикаций.")
            val id = item.text("id")
            val url = item.text("url")
            val page = (item["vd_page"] as? JsonPrimitive)?.intOrNull ?: 0
            require(item.text("_type") == "url" && item.text("ie_key") == "PornHub" && Regex("^[a-z0-9]+$").matches(id) &&
                url == "https://www.$host/view_video.php?viewkey=$id" && seen.add(url) && page in 1..pageCounts.size) { "Посторонний или повторный материал Pornhub." }
            pageCounts[page - 1] = pageCounts[page - 1] - 1
            require(pageCounts[page - 1] >= 0) { "Количество материалов страницы не совпадает." }
            DiscoveredMedia(url, item.text("title").ifEmpty { url })
        }
        require(pageCounts.all { it == 0 }) { "Количество материалов страницы не совпадает." }
        return FullScan(root.text("title").ifEmpty { sourceUrl }, result,
            suggestedName = listOf(root.text("channel"), root.text("uploader"), root.text("title")).firstOrNull(String::isNotBlank).orEmpty())
    }
}
