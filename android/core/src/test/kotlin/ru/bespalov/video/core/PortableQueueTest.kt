package ru.bespalov.video.core

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayOutputStream
import java.util.Base64
import java.util.zip.GZIPOutputStream

class PortableQueueTest {
    private val json = Json
    private fun packed(bytes: ByteArray): String = ByteArrayOutputStream().let { output ->
        GZIPOutputStream(output).use { it.write(bytes) }
        PortableQueue.PREFIX + Base64.getEncoder().encodeToString(output.toByteArray())
    }
    private fun packed(text: String): String = packed(text.toByteArray(Charsets.UTF_8))
    private fun document(items: String, version: String = "1", format: String = "video-downloader.queue") =
        """{"format":"$format","version":$version,"items":$items}"""
    private fun entry(url: String, title: String = "") = PortableQueue.Entry(url, title)
    private fun reject(text: String) = assertThrows(IllegalArgumentException::class.java) { PortableQueue.decode(text) }
    private fun fixture(name: String) = checkNotNull(javaClass.getResourceAsStream("/portable-queue/$name")).use { it.readBytes().toString(Charsets.UTF_8) }

    @Test fun recognizesTheFormatFamilyWithoutFallingBackToUrlExtraction() {
        assertTrue(PortableQueue.isPortable(" \uFEFF \nVDQUEUE1:invalid"))
        assertTrue(PortableQueue.isPortable("VDQUEUE2:invalid"))
        assertTrue(PortableQueue.isPortable("VDQUEUE malformed"))
        assertFalse(PortableQueue.isPortable("https://example.org/VDQUEUE1:"))
        reject("VDQUEUE2:invalid")
    }

    @Test fun sharedGoldenFixtureAndWrappedCopyAreDecodedWithoutChangingIdentity() {
        val expected = json.parseToJsonElement(fixture("queue-v1.expected.json")).jsonObject["items"]!!.jsonArray.map {
            entry(it.jsonObject["url"]!!.jsonPrimitive.content, it.jsonObject["title"]!!.jsonPrimitive.content)
        }
        assertEquals(expected, PortableQueue.decode(fixture("queue-v1.txt")))
        assertEquals(expected, PortableQueue.decode(fixture("queue-v1-wrapped.txt")))
        assertEquals(expected, PortableQueue.decode(PortableQueue.encode(expected)))
        assertTrue(PortableQueue.encode(expected).startsWith(PortableQueue.PREFIX))
        assertFalse(PortableQueue.encode(expected).contains('\n'))
        assertEquals("https://EXAMPLE.org/watch?Token=AbC%2fD&x=1#part", expected[1].url)
    }

    @Test fun unicodeAndControlCharactersInTitlesRoundTripAsData() {
        val rows = listOf(entry("https://example.org/Видео/%D0%90?Case=AbC%2fD", "🎬 Кириллица \"кавычки\"\nстрока\tтабуляция\\путь"))
        assertEquals(rows, PortableQueue.decode(PortableQueue.encode(rows)))
        assertEquals(emptyList<PortableQueue.Entry>(), PortableQueue.decode(PortableQueue.encode(emptyList())))
    }

    @Test fun missingTitleDefaultsToEmptyWhileNullOrWrongTypesAreRejected() {
        assertEquals(listOf(entry("https://example.org/a")), PortableQueue.decode(packed(document("""[{"url":"https://example.org/a"}]"""))))
        listOf("null", "123", "false", "[]", "{}").forEach { title ->
            reject(packed(document("""[{"url":"https://example.org/a","title":$title}]""")))
        }
        reject(packed(document("""[{"title":"missing URL"}]""")))
        reject(packed(document("""[{"url":123}]""")))
    }

    @Test fun formatAndVersionMustBeKnownAndVersionAnExactInteger() {
        listOf("0", "2", "1.0", "1e0", "\"1\"", "null", "true").forEach { version -> reject(packed(document("[]", version))) }
        reject(packed(document("[]", format = "another.queue")))
        reject(packed("[]"))
        reject(packed("""{"format":"video-downloader.queue","version":1,"items":null}"""))
        reject(packed(document("[null]")))
    }

    @Test fun unknownFieldsNeverTurnIntoLocalStateOrOwnership() {
        val text = packed("""{"format":"video-downloader.queue","version":1,"history":[{"url":"https://secret.example/a"}],"items":[{"url":"https://example.org/a","title":"Known","status":"COMPLETED","outputUris":["file:///private/foreign.mp4"],"settingsSnapshot":{"cookiesFile":"secret"},"subscriptionId":"foreign"}]}""")
        val decoded = PortableQueue.decode(text)
        assertEquals(listOf(entry("https://example.org/a", "Known")), decoded)
        val task = PortableQueue.merge(AppState(), decoded).queue.single()
        assertEquals(DownloadStatus.QUEUED, task.status)
        assertTrue(task.deferredNextStart)
        assertTrue(task.outputUris.isEmpty())
        assertEquals("", task.stageDirectory)
        assertEquals("", task.subscriptionId)
        assertNull(task.settingsSnapshot)
    }

    @Test fun unknownFieldsStillRequireStrictJsonTokensAndQuotedStrings() {
        val invalid = listOf("01", "-01", "+1", ".5", "1.", "1e", "1e+", "NaN", "Infinity", "undefined")
        invalid.forEach { value ->
            reject(packed("""{"format":"video-downloader.queue","version":1,"items":[],"ignored":{"nested":[$value]}}"""))
        }
        for (control in 0 until 0x20) {
            reject(packed("""{"format":"video-downloader.queue","version":1,"items":[],"ignored":"raw${control.toChar()}control"}"""))
        }
        val valid = """{"format":"video-downloader.queue","version":1,"items":[],"ignored":{"nested":[null,true,false,0,-0,1,-1,0.25,-0.25,1e2,1E+2,1e-2,"escaped\ncontrol"]}}"""
        assertEquals(emptyList<PortableQueue.Entry>(), PortableQueue.decode(packed(valid)))
    }

    @Test fun sharedUrlRulesPreserveAcceptedAddressesThroughImportAndPersistence() {
        val cases = json.parseToJsonElement(fixture("url-cases.json")).jsonArray
        val accepted = mutableListOf<PortableQueue.Entry>()
        cases.forEach { case ->
            val url = case.jsonObject["url"]!!.jsonPrimitive.content
            val row = entry(url, "Точное название 🎬")
            if (case.jsonObject["valid"]!!.jsonPrimitive.content == "true") {
                accepted += row
                assertEquals(listOf(row), PortableQueue.decode(PortableQueue.encode(listOf(row))))
            } else {
                assertThrows(IllegalArgumentException::class.java) { PortableQueue.encode(listOf(row)) }
                reject(packed(document("""[{"url":${JsonPrimitive(url)}}]""")))
            }
        }
        val decoded = PortableQueue.decode(PortableQueue.encode(accepted))
        assertEquals(accepted, decoded)
        val imported = PortableQueue.merge(AppState(), decoded)
        JsonStateRepository.validate(imported)
        assertEquals(accepted.map { it.url }, imported.queue.map { it.url })
        assertTrue(imported.queue.all { it.deferredNextStart })
    }

    @Test fun invalidEntriesIncludingLateDuplicateMustRejectWholePayload() {
        val bad = listOf("ftp://example.org/a", "file:///tmp/a", "https://user:password@example.org/a", "https://example.org/a b", "https://example.org/\nsecret", "not a url")
        bad.forEach { url -> assertThrows(IllegalArgumentException::class.java) { PortableQueue.encode(listOf(entry(url))) } }
        val repeated = """[{"url":"https://example.org/a","title":"first"},{"url":"https://example.org/a","title":null}]"""
        reject(packed(document(repeated)))
        assertThrows(IllegalArgumentException::class.java) { PortableQueue.encode(listOf(entry("https://example.org/a", "\uD800"))) }
        reject(packed(document("""[{"url":"https://example.org/a","title":"\ud800"}]""")))
    }

    @Test fun duplicatesUseFirstOrdinalMatchAndKeepQueryCase() {
        val rows = listOf(entry("https://example.org/?Token=AbC", "first"), entry("https://example.org/?Token=AbC", "second"), entry("https://example.org/?Token=abc", "lower"))
        assertEquals(listOf(rows[0], rows[2]), PortableQueue.decode(PortableQueue.encode(rows)))
        val text = packed(document("""[{"url":"https://example.org/a","title":""},{"url":"https://example.org/a","title":"later"}]"""))
        assertEquals(listOf(entry("https://example.org/a")), PortableQueue.decode(text))
    }

    @Test fun malformedEncodingCompressionUtf8AndJsonAreRejected() {
        listOf("", "https://example.org/a", "VDQUEUE2:AAAA", "VDQUEUE1:AAAA-AAA", "VDQUEUE1:AAAA=AAA", "VDQUEUE1:AA===", "VDQUEUE1:AAA", "VDQUEUE1:AA\u00A0AA").forEach(::reject)
        reject("VDQUEUE1:" + Base64.getEncoder().encodeToString("not gzip".toByteArray()))
        val valid = PortableQueue.encode(listOf(entry("https://example.org/a")))
        reject(valid.dropLast(4))
        reject(packed(byteArrayOf(0xc3.toByte(), 0x28)))
        reject(packed("{"))
        reject(packed("""{"format":"video-downloader.queue","version":1,"items":[],}"""))
    }

    @Test fun depthAndExpandedSizeAreBoundedBeforeJsonParsing() {
        reject(packed("[".repeat(33) + "0" + "]".repeat(33)))
        val bomb = ByteArrayOutputStream()
        GZIPOutputStream(bomb).use { gzip -> repeat(2049) { gzip.write(ByteArray(8192) { ' '.code.toByte() }) } }
        reject(PortableQueue.PREFIX + Base64.getEncoder().encodeToString(bomb.toByteArray()))
        reject(PortableQueue.PREFIX + " ".repeat(PortableQueue.MAX_TEXT_BYTES))
        assertThrows(IllegalArgumentException::class.java) {
            PortableQueue.encode(List(PortableQueue.MAX_ITEMS + 1) { entry("https://example.org/a") })
        }
    }

    @Test fun encodingEscapedHugeTitlesIsBoundedAsItWritesJson() {
        assertThrows(IllegalArgumentException::class.java) {
            PortableQueue.encode(listOf(entry("https://example.org/a", "\u0000".repeat(PortableQueue.MAX_JSON_BYTES / 6 + 1))))
        }
    }

    @Test fun mergeAppendsInOrderAndPreservesEveryExistingLifecycleField() {
        val existing = DownloadTask(url = "https://example.org/existing", title = "https://example.org/existing", status = DownloadStatus.RUNNING,
            progress = 42f, outputUris = listOf("content://owned/12"), stageDirectory = "/owned/stage", subscriptionId = "source", subscriptionName = "Channel", rawError = "diagnostic")
        val named = DownloadTask(url = "https://example.org/named", title = "Keep this title", status = DownloadStatus.COMPLETED)
        val history = listOf(SuccessRecord("https://example.org/previous"))
        val state = AppState(queue = listOf(existing, named), history = history, settings = Settings(cookiesFile = "private", outputTreeUri = "content://grant"))
        val rows = listOf(entry("https://example.org/new-b", "B"), entry(existing.url, "Restored title"), entry("https://example.org/new-a", "A"), entry(named.url, "Do not replace"))
        val merged = PortableQueue.merge(state, rows)
        assertEquals(listOf(existing.url, named.url, rows[0].url, rows[2].url), merged.queue.map { it.url })
        assertEquals(existing.copy(title = "Restored title"), merged.queue[0])
        assertEquals(named, merged.queue[1])
        assertEquals(history, merged.history)
        assertEquals(state.settings, merged.settings)
        merged.queue.drop(2).forEach { task ->
            assertEquals(DownloadStatus.QUEUED, task.status)
            assertTrue(task.deferredNextStart)
            assertEquals("", task.subscriptionId)
            assertTrue(task.outputUris.isEmpty())
            assertEquals(0f, task.progress)
        }
        assertThrows(IllegalArgumentException::class.java) { PortableQueue.merge(state, rows + entry("file:///foreign")) }
        assertEquals(2, state.queue.size)
    }
}
