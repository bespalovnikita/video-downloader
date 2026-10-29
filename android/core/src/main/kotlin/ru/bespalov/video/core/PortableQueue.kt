package ru.bespalov.video.core

import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.encodeToStream
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction
import java.util.Base64
import java.util.zip.GZIPInputStream
import java.util.zip.GZIPOutputStream

/** The shared Windows/Android clipboard/file format carries only ordered URLs and titles. */
object PortableQueue {
    const val PREFIX = "VDQUEUE1:"
    const val MAX_ITEMS = 100_000
    const val MAX_JSON_BYTES = 16 * 1024 * 1024
    const val MAX_COMPRESSED_BYTES = 16 * 1024 * 1024
    const val MAX_TEXT_BYTES = 24 * 1024 * 1024
    const val MAX_CLIPBOARD_TEXT = 262_144
    private const val MAX_DEPTH = 32
    private val json = Json { encodeDefaults = true; isLenient = false; allowSpecialFloatingPointValues = false }
    private val base64 = Base64.getEncoder()

    @Serializable
    data class Entry(val url: String, val title: String = "")

    @Serializable
    private data class Document(val format: String = "video-downloader.queue", val version: Int = 1, val items: List<Entry>)

    /** Recognition is cheap; malformed recognized input must never fall through to URL extraction. */
    fun isPortable(text: String): Boolean = normalize(text).startsWith("VDQUEUE")

    @OptIn(ExperimentalSerializationApi::class)
    fun encode(entries: List<Entry>): String {
        val checked = validated(entries)
        val bytes = BoundedOutput(MAX_JSON_BYTES)
        json.encodeToStream(Document.serializer(), Document(items = checked), bytes)
        val compressed = BoundedOutput(MAX_COMPRESSED_BYTES)
        GZIPOutputStream(compressed).use { it.write(bytes.toByteArray()) }
        return PREFIX + base64.encodeToString(compressed.toByteArray())
    }

    fun decode(text: String): List<Entry> {
        require(text.length <= MAX_TEXT_BYTES) { "Текст очереди превышает допустимые 24 МиБ." }
        val normalized = normalize(text)
        require(normalized.startsWith(PREFIX)) { "Не найден переносимый формат очереди VDQUEUE1." }
        var encodedCount = 0
        var padding = 0
        for (index in PREFIX.length until normalized.length) {
            when (normalized[index]) {
                ' ', '\t', '\r', '\n' -> Unit
                in 'A'..'Z', in 'a'..'z', in '0'..'9', '+', '/' -> {
                    require(padding == 0) { "Некорректное кодирование переносимой очереди." }
                    encodedCount++
                }
                '=' -> {
                    padding++
                    require(padding <= 2) { "Некорректное кодирование переносимой очереди." }
                    encodedCount++
                }
                else -> throw IllegalArgumentException("Некорректное кодирование переносимой очереди.")
            }
        }
        require(encodedCount > 0 && encodedCount % 4 == 0) { "Некорректное кодирование переносимой очереди." }
        require(encodedCount <= ((MAX_COMPRESSED_BYTES + 2) / 3) * 4) { "Сжатая очередь превышает допустимые 16 МиБ." }
        // Decode directly from ASCII characters instead of allocating another 24 MiB
        // string and byte array merely to remove chat line wrapping.
        val compressed = try {
            Base64.getDecoder().wrap(Base64Text(normalized, PREFIX.length)).use { readBounded(it, MAX_COMPRESSED_BYTES) }
        } catch (failure: IllegalArgumentException) { throw failure }
        catch (_: Exception) { throw IllegalArgumentException("Некорректное кодирование переносимой очереди.") }
        val bytes = try { GZIPInputStream(ByteArrayInputStream(compressed)).use { readBounded(it, MAX_JSON_BYTES) } }
            catch (failure: IllegalArgumentException) { throw failure }
            catch (_: Exception) { throw IllegalArgumentException("Повреждены сжатые данные переносимой очереди.") }
        val document = try {
            Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes)).toString()
        } catch (_: Exception) { throw IllegalArgumentException("Переносимая очередь содержит некорректный UTF-8.") }
        checkDepth(document)
        val root = try { json.parseToJsonElement(document) as? JsonObject }
            catch (_: Exception) { throw IllegalArgumentException("Переносимая очередь содержит некорректный JSON.") }
        require(root != null && string(root["format"]) == "video-downloader.queue") { "Неизвестный формат переносимой очереди." }
        val version = root["version"] as? JsonPrimitive
        require(version != null && !version.isString && version.content == "1") { "Неподдерживаемая версия переносимой очереди." }
        val items = root["items"] as? JsonArray ?: throw IllegalArgumentException("В переносимой очереди отсутствует список материалов.")
        require(items.size <= MAX_ITEMS) { "В очереди больше 100 000 материалов." }
        val entries = items.map { item ->
            val entry = item as? JsonObject ?: throw IllegalArgumentException("Некорректная запись переносимой очереди.")
            val url = string(entry["url"]) ?: throw IllegalArgumentException("У записи очереди отсутствует строковый URL.")
            val title = if ("title" in entry) string(entry["title"])
                ?: throw IllegalArgumentException("Название материала должно быть строкой.") else ""
            Entry(url, title)
        }
        return validated(entries)
    }

    /** Existing lifecycle/ownership is untouched; imported rows always wait for an explicit Start. */
    fun merge(state: AppState, entries: List<Entry>): AppState {
        val checked = validated(entries)
        val incoming = checked.associateBy { it.url }
        val known = state.queue.map { it.url }.toSet()
        return state.copy(queue = state.queue.map { task ->
            val title = incoming[task.url]?.title.orEmpty()
            if ((task.title.isBlank() || task.title == task.url) && title.isNotBlank()) task.copy(title = title) else task
        } + checked.filterNot { it.url in known }.map {
            DownloadTask(url = it.url, title = it.title, deferredNextStart = true)
        })
    }

    private fun validated(entries: List<Entry>): List<Entry> {
        require(entries.size <= MAX_ITEMS) { "В очереди больше 100 000 материалов." }
        entries.forEach { entry ->
            require(Urls.isHttp(entry.url)) { "Очередь содержит некорректный HTTP(S) URL." }
            // Also rejects lone UTF-16 surrogates in directly constructed or escaped JSON strings.
            validateText(entry.url)
            validateText(entry.title)
        }
        return entries.distinctBy { it.url }
    }

    private fun normalize(text: String): String = text.trim().removePrefix("\uFEFF").trim()
    private fun string(value: kotlinx.serialization.json.JsonElement?): String? =
        (value as? JsonPrimitive)?.takeIf { it.isString }?.content

    private fun validateText(value: String) {
        var bytes = 0L
        var index = 0
        while (index < value.length) {
            val character = value[index++]
            bytes += when {
                character.code < 0x80 -> 1
                character.code < 0x800 -> 2
                character.isHighSurrogate() -> {
                    require(index < value.length && value[index].isLowSurrogate()) { "Очередь содержит некорректный текст Unicode." }
                    index++
                    4
                }
                character.isLowSurrogate() -> throw IllegalArgumentException("Очередь содержит некорректный текст Unicode.")
                else -> 3
            }
            require(bytes <= MAX_JSON_BYTES) { "Текст записи очереди превышает допустимые 16 МиБ." }
        }
    }

    private class Base64Text(private val text: String, private var index: Int) : InputStream() {
        override fun read(): Int {
            while (index < text.length) {
                val character = text[index++]
                if (character !in " \t\r\n") return character.code
            }
            return -1
        }
        override fun read(bytes: ByteArray, offset: Int, length: Int): Int {
            if (length == 0) return 0
            var count = 0
            while (index < text.length && count < length) {
                val character = text[index++]
                if (character !in " \t\r\n") bytes[offset + count++] = character.code.toByte()
            }
            return if (count == 0) -1 else count
        }
    }

    private class BoundedOutput(private val limit: Int) : ByteArrayOutputStream() {
        override fun write(value: Int) {
            require(size().toLong() + 1 <= limit) { "Данные переносимой очереди превышают допустимые 16 МиБ." }
            super.write(value)
        }
        override fun write(bytes: ByteArray, offset: Int, length: Int) {
            require(size().toLong() + length <= limit) { "Данные переносимой очереди превышают допустимые 16 МиБ." }
            super.write(bytes, offset, length)
        }
    }

    private fun readBounded(input: InputStream, limit: Int): ByteArray {
        val output = ByteArrayOutputStream()
        val buffer = ByteArray(8192)
        while (true) {
            val count = input.read(buffer)
            if (count < 0) break
            require(output.size().toLong() + count <= limit) { "Распакованная очередь превышает допустимые 16 МиБ." }
            output.write(buffer, 0, count)
        }
        return output.toByteArray()
    }

    /** Bound nesting and validate tokens before the tree parser, including ignored fields. */
    private fun checkDepth(document: String) {
        var depth = 0
        var quoted = false
        var escaped = false
        var index = 0
        while (index < document.length) {
            val character = document[index++]
            if (quoted) {
                require(character.code >= 0x20) { "Переносимая очередь содержит некорректный JSON." }
                if (escaped) escaped = false
                else when (character) { '\\' -> escaped = true; '"' -> quoted = false }
            } else when (character) {
                '"' -> quoted = true
                '{', '[' -> { depth++; require(depth <= MAX_DEPTH) { "Превышена вложенность данных очереди." } }
                '}', ']' -> { depth--; require(depth >= 0) { "Переносимая очередь содержит некорректный JSON." } }
                ' ', '\t', '\r', '\n', ':', ',' -> Unit
                else -> {
                    val start = index - 1
                    while (index < document.length && document[index] !in "{}[]\" \t\r\n:,") index++
                    require(validPrimitive(document, start, index)) { "Переносимая очередь содержит некорректный JSON." }
                }
            }
        }
        require(depth == 0 && !quoted) { "Переносимая очередь содержит некорректный JSON." }
    }

    /** JSON number grammar without recursion, token copies or a backtracking expression. */
    private fun validPrimitive(document: String, start: Int, end: Int): Boolean {
        val size = end - start
        if (size == 4 && (document.regionMatches(start, "true", 0, 4) || document.regionMatches(start, "null", 0, 4))) return true
        if (size == 5 && document.regionMatches(start, "false", 0, 5)) return true
        var index = start
        if (index < end && document[index] == '-') index++
        if (index == end) return false
        when (document[index]) {
            '0' -> index++
            in '1'..'9' -> { do { index++ } while (index < end && document[index] in '0'..'9') }
            else -> return false
        }
        if (index < end && document[index] == '.') {
            index++
            val fraction = index
            while (index < end && document[index] in '0'..'9') index++
            if (index == fraction) return false
        }
        if (index < end && document[index] in "eE") {
            index++
            if (index < end && document[index] in "+-") index++
            val exponent = index
            while (index < end && document[index] in '0'..'9') index++
            if (index == exponent) return false
        }
        return index == end
    }
}
