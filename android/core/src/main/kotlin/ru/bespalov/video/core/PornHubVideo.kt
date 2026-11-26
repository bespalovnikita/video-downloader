package ru.bespalov.video.core

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import java.net.URI
import java.util.Locale

object PornHubVideo {
    private val hostPattern = Regex("""(?:[a-z0-9-]+\.)?(pornhub(?:premium)?\.(?:com|net|org))""", RegexOption.IGNORE_CASE)
    private const val FILE_ID = """(?:(?:ph)?[0-9a-f]{13}|[0-9]{6,12})"""
    private val bareId = Regex(FILE_ID)
    private val bracketedId = Regex("""\[($FILE_ID)\](?:\.[a-zA-Z0-9]{1,8})?\z""")
    private val viewKey = Regex("[a-z0-9]{1,64}")
    private val profilePath = Regex("/(?:model|pornstar)/[a-zA-Z0-9_-]{1,128}")

    fun watchUrl(input: String): String? {
        val value = input.trim().trim('"')
        val uri = runCatching { URI(value) }.getOrNull()
        val scheme = uri?.scheme?.lowercase(Locale.ROOT)
        if (uri != null && (scheme == "http" || scheme == "https")) {
            val host = hostPattern.matchEntire(uri.host.orEmpty()) ?: return null
            if (uri.rawUserInfo != null || (uri.port != -1 && uri.port != if (scheme == "https") 443 else 80) ||
                (uri.rawPath != "/view_video.php" && uri.rawPath != "/video/show")) return null
            val key = uri.rawQuery.orEmpty().split('&').singleOrNull { it.substringBefore('=') == "viewkey" }
                ?.substringAfter('=', "") ?: return null
            return if (viewKey.matches(key)) watchUrl(host.groupValues[1].lowercase(Locale.ROOT), key) else null
        }
        if ("://" in value) return null
        val id = bareId.matchEntire(value)?.value ?: bracketedId.find(value)?.groupValues?.get(1) ?: return null
        return watchUrl("pornhub.com", id)
    }

    private fun watchUrl(site: String, id: String) = "https://www.$site/view_video.php?viewkey=$id"

    fun parseProfiles(json: String, watchUrl: String): List<String> {
        val watch = URI(watchUrl)
        val invalid = "Некорректный ответ поиска моделей Pornhub."
        val root = Json.parseToJsonElement(json) as? JsonObject ?: error(invalid)
        val profiles = root["vd_ph_profiles"] as? JsonArray ?: error(invalid)
        require(root.text("id") == watch.rawQuery.substringAfter("viewkey=") && profiles.size in 1..50) { invalid }
        val origin = "https://${watch.host}"
        return profiles.map { element ->
            val url = (element as? JsonPrimitive)?.takeIf { it.isString }?.content ?: error(invalid)
            require(url.startsWith(origin) && profilePath.matches(url.removePrefix(origin))) { invalid }
            url
        }.distinct()
    }
}
