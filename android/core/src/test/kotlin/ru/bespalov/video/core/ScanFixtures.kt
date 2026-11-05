package ru.bespalov.video.core

import kotlinx.serialization.json.*

internal object ScanFixtures {
    const val YOUTUBE = "https://www.youtube.com/@fixture/videos"
    const val PORNHUB = "https://rt.pornhub.com/model/fixture"
    fun id(index: Int) = index.toString().padStart(11, '0')
    fun watch(index: Int) = "https://www.youtube.com/watch?v=${id(index)}"
    fun recent(indices: List<Int>, source: String = YOUTUBE, title: String = "Window"): JsonObject = buildJsonObject {
        put("_type", "playlist"); put("extractor_key", "YoutubeTab"); put("webpage_url", source); put("title", title)
        put("playlist_count", 1_000)
        put("requested_entries", JsonArray(indices.indices.map { JsonPrimitive(it + 1) }))
        put("entries", JsonArray(indices.map { index -> buildJsonObject {
            put("_type", "url"); put("ie_key", "Youtube"); put("id", id(index)); put("url", watch(index)); put("title", "Video $index")
        } }))
    }
    fun full(indices: List<Int>, title: String = "Full"): JsonObject = buildJsonObject {
        put("_type", "playlist"); put("title", title); put("playlist_count", indices.size)
        put("entries", JsonArray(indices.map { index -> buildJsonObject {
            put("_type", "video"); put("extractor_key", "Youtube"); put("id", id(index)); put("webpage_url", watch(index)); put("title", "Video $index")
        } }))
    }
    fun porn(indices: List<Int>, recent: Boolean = true, hasNext: Boolean = false): JsonObject = buildJsonObject {
        put("_type", "playlist"); put("title", "Publications")
        put("entries", JsonArray(indices.map { index -> buildJsonObject {
            put("_type", "url"); put("ie_key", "PornHub"); put("id", "ph$index"); put("url", "https://www.pornhub.com/view_video.php?viewkey=ph$index"); put("vd_page", 1)
        } }))
        put(if (recent) "vd_ph_recent" else "vd_ph_scan", buildJsonObject {
            put("version", 1); put("complete", !recent); put("verified_first_page", true)
            put("source_url", PORNHUB); put("listing_url", PornHubCollections.listingUrl(PORNHUB))
            put("item_count", indices.size)
            put("pages", JsonArray(listOf(buildJsonObject { put("number", 1); put("count", indices.size); put("has_next", hasNext) })))
            if (recent) { put("scope", "recent"); put("order", "most_recent"); put("window_size", 20) }
        })
    }
    fun JsonObject.changed(key: String, value: JsonElement): JsonObject = JsonObject(this + (key to value))
}
