package ru.bespalov.video.core

import kotlinx.serialization.decodeFromString
import kotlinx.serialization.json.Json
import java.net.URLDecoder
import java.nio.charset.StandardCharsets

/** Redaction happens before any diagnostic enters retained memory or a file. */
object JournalSanitizer {
    const val MAX_MESSAGE_CHARS = 4096
    private const val MAX_INPUT_CHARS = 16 * 1024
    private const val HIDDEN = "[скрыто]"
    private const val TRUNCATED = " …[усечено]"
    private val publicQueryKeys = setOf(
        "v", "list", "viewkey", "id", "video_id", "playlist", "playlist_id", "channel_id",
        "index", "page", "p", "t", "start", "end",
    )
    private val options = setOf(RegexOption.IGNORE_CASE)
    private val url = Regex("\\bhttps?://[^\\s<>\"']+", options)
    private val cookieRow = Regex("(?:#HttpOnly_)?\\.?[^\\s\"'\\\\]+(?:\\t|\\\\t)(?:TRUE|FALSE)(?:\\t|\\\\t)[^\\r\\n]*", options)
    private val cookieDiagnostic = Regex(
        "(\\b(?:cookie\\s+file\\s+(?:entr(?:y|ies)|line)\\b(?:\\s+due\\s+to\\s+invalid\\s+length\\s+\\d+\\s*:)?|invalid\\s+Netscape\\s+format\\s+cookies?\\s+file\\b|(?:(?:failed|unable)\\s+to|could\\s+not)\\s+(?:parse|load|read)\\s+cookies?(?:\\s+file)?\\b))[^\\r\\n]*", options)
    private val secretOption = Regex(
        "(--(?:cookies(?:-from-browser)?|username|password|video-password|add-headers?|proxy|client-certificate(?:-key|-password)?|certificate-password)\\b)(?:[\"'][ \\t]*,[ \\t]*|[ \\t]*=[ \\t]*|[ \\t]+)", options)
    private val header = Regex("(\\b(?:authorization|proxy-authorization|cookie|set-cookie))[\"']?\\s*:\\s*[^\\r\\n]*", options)
    private val assignment = Regex(
        "(\\b(?:password|passwd|access_token|refresh_token|token|api[_-]?key|authorization|cookie|session(?:id)?|secret))[\"']?[ \\t]*[:=][ \\t]*", options)
    private val bearer = Regex("\\bBearer\\s+[^\\s,;'\"\\r\\n]+", options)

    fun redact(value: String?, multiline: Boolean = false): String {
        if (value.isNullOrEmpty()) return ""
        var truncated = value.length > MAX_INPUT_CHARS
        var text = if (truncated) safeTake(value, MAX_INPUT_CHARS) else value
        if (truncated) {
            // A truncated authority must not disguise unfinished userinfo as a host.
            val tail = url.findAll(text).lastOrNull()
            if (tail != null && tail.range.last == text.lastIndex)
                text = text.take(tail.range.first) + "[слишком длинный URL скрыт]"
        }
        text = decodeEngineText(text)
        text = cookieDiagnostic.replace(text) { it.groupValues[1] + " [содержимое cookies скрыто]" }
        text = cookieRow.replace(text, "[строка cookies скрыта]")
        text = redactValues(text, secretOption) { it.groupValues[1] + " " + HIDDEN }
        text = url.replace(text) { redactUrl(it.value) }
        text = header.replace(text) { it.groupValues[1] + ": " + HIDDEN }
        val sanitizedUrls = url.findAll(text).map { it.range }.toList()
        text = redactValues(text, assignment, sanitizedUrls) { match ->
            match.groupValues[1] + (if (':' in match.value) ": " else "=") + HIDDEN
        }
        text = bearer.replace(text, "Bearer $HIDDEN")
        val output = StringBuilder(minOf(text.length, MAX_MESSAGE_CHARS))
        var index = 0
        while (index < text.length) {
            val character = text[index]
            when {
                character == '\r' && multiline -> {
                    if (index + 1 < text.length && text[index + 1] == '\n') index++
                    output.append("\n    ")
                }
                character == '\n' && multiline -> output.append("\n    ")
                character == '\r' -> output.append("\\r")
                character == '\n' -> output.append("\\n")
                character == '\t' -> output.append("\\t")
                character.code < 32 || character.code in 0x7f..0x9f -> output.append("\\u").append(character.code.toString(16).padStart(4, '0'))
                character.isHighSurrogate() -> {
                    if (index + 1 < text.length && text[index + 1].isLowSurrogate()) output.append(character).append(text[++index])
                    else output.append('\uFFFD')
                }
                character.isLowSurrogate() -> output.append('\uFFFD')
                else -> output.append(character)
            }
            if (output.length > MAX_MESSAGE_CHARS) { truncated = true; break }
            index++
        }
        return if (truncated || output.length > MAX_MESSAGE_CHARS)
            safeTake(output.toString(), MAX_MESSAGE_CHARS - TRUNCATED.length) + TRUNCATED else output.toString()
    }

    /** Parse bounded diagnostic tokens iteratively: recursive quoted regexes can
     * overflow the JVM stack on a legitimate long password or cookie path. */
    private fun redactValues(text: String, prefix: Regex, ignored: List<IntRange> = emptyList(),
        replacement: (MatchResult) -> String): String {
        val output = StringBuilder(text.length)
        var cursor = 0
        for (match in prefix.findAll(text)) {
            if (match.range.first < cursor || ignored.any { match.range.first >= it.first && match.range.last <= it.last }) continue
            output.append(text, cursor, match.range.first)
            output.append(replacement(match))
            cursor = valueEnd(text, match.range.last + 1)
        }
        return output.append(text, cursor, text.length).toString()
    }

    private fun valueEnd(text: String, start: Int): Int {
        if (start >= text.length) return start
        val quote = text[start].takeIf { it == '\"' || it == '\'' }
        var index = start + if (quote == null) 0 else 1
        while (index < text.length) {
            val current = text[index]
            if (current == '\r' || current == '\n' || (quote == null && current.isWhitespace())) return index
            if (quote != null && current == quote) return index + 1
            if (quote != null && current == '\\' && index + 1 < text.length && text[index + 1] !in "\r\n") index++
            index++
        }
        return index
    }

    private fun decodeEngineText(text: String): String {
        val marker = text.indexOf(" text=\"")
        val start = if (marker >= 0) marker + 6 else if (text.startsWith("text=\"")) 5 else return text
        return try { text.take(start) + Json.decodeFromString<String>(text.substring(start)) }
        catch (_: Exception) { text.take(start) + "[исходный вывод скрыт: неполная JSON-строка]" }
    }

    private fun redactUrl(original: String): String {
        var value = original
        val authority = value.indexOf("://") + 3
        val authorityEnd = value.indexOfAny(charArrayOf('/', '?', '#'), authority).let { if (it < 0) value.length else it }
        val userInfo = value.lastIndexOf('@', authorityEnd - 1)
        if (userInfo >= authority) value = value.take(authority) + HIDDEN + "@" + value.substring(userInfo + 1)
        val fragment = value.indexOf('#')
        val hasFragment = fragment >= 0
        if (hasFragment) value = value.take(fragment)
        val query = value.indexOf('?')
        if (query >= 0) {
            val result = StringBuilder(value.take(query + 1))
            var start = query + 1
            for (index in start..value.length) {
                if (index < value.length && value[index] != '&' && value[index] != ';') continue
                val parameter = value.substring(start, index)
                val equals = parameter.indexOf('=')
                val key = if (equals < 0) parameter else parameter.take(equals)
                val decoded = try { URLDecoder.decode(key, StandardCharsets.UTF_8.name()).lowercase(java.util.Locale.ROOT) } catch (_: IllegalArgumentException) { "" }
                when {
                    decoded in publicQueryKeys -> result.append(parameter)
                    parameter.isNotEmpty() -> result.append(if (equals < 0) HIDDEN else "$key=$HIDDEN")
                }
                if (index < value.length) result.append(value[index])
                start = index + 1
            }
            value = result.toString()
        }
        return value + if (hasFragment) "#$HIDDEN" else ""
    }

    internal fun safeTake(value: String, length: Int): String {
        var size = length.coerceIn(0, value.length)
        if (size > 0 && value[size - 1].isHighSurrogate()) size--
        return value.take(size)
    }
}
