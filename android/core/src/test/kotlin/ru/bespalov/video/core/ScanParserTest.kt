package ru.bespalov.video.core

import org.junit.Assert.*
import org.junit.Test

class ScanParserTest {
    private val source = "https://example.com/collection"
    private val full = """{"title":"Channel","entries":[{"webpage_url":"https://example.com/Video","title":""},{"webpage_url":"https://example.com/video","title":"Known"}],"playlist_count":2}"""

    @Test fun nestedFullCollectionsDeduplicateOnlyExactUrls() {
        val json = """{"title":"Channel","entries":[{"entries":[{"webpage_url":"https://example.com/A"},{"webpage_url":"https://example.com/a"}]},{"webpage_url":"https://example.com/A"}]}"""
        assertEquals(listOf("https://example.com/A", "https://example.com/a"), ScanParser.parse(json, source).items.map { it.url })
    }

    @Test fun partialAndUnresolvedCollectionsFailRatherThanRebase() {
        val invalid = listOf(
            """{"entries":[null]}""", """{"entries":[],"incomplete":true}""",
            """{"entries":[],"playlist_count":1}""", """{"entries":[],"requested_entries":[2]}""",
            """{"entries":[{"_type":"url","url":"https://example.com/video"}]}""",
            """{"entries":[{"webpage_url":"file:///secret"}]}""",
            """{"entries":[{"availability":"private","webpage_url":"https://example.com/video"}]}""",
            """{"webpage_url":"https://example.com/video"}""",
        )
        invalid.forEach { json -> assertThrows(RuntimeException::class.java) { ScanParser.parse(json, source) } }
        assertThrows(IllegalArgumentException::class.java) { ScanParser.parse(full, source, 1) }
    }

    @Test fun rssTitlesCannotIntroduceUnconfirmedOrCaseDistinctUrls() {
        val scan = ScanParser.parse(full, source)
        val feed = RssParser.parse("""<rss version="2.0"><channel><item><title>From feed</title><link>https://example.com/Video</link></item><item><title>Wrong title</title><link>https://example.com/video</link></item><item><title>Unconfirmed</title><link>https://example.com/feed-only</link></item></channel></rss>""")
        val enriched = RssParser.enrich(scan, feed)
        assertEquals(listOf("From feed", "Known"), enriched.items.map { it.title })
        assertEquals(scan.items.map { it.url }, enriched.items.map { it.url })
        assertTrue(enriched.warning.contains("1"))
        val atom = RssParser.parse("""<feed xmlns="http://www.w3.org/2005/Atom"><entry><title>A</title><link rel="self" href="https://example.com/self"/><link rel="alternate" href="https://example.com/Video"/></entry></feed>""")
        assertEquals("https://example.com/Video", atom.single().url)
    }

    @Test fun feedRejectsDtdAndOversizedMembership() {
        assertThrows(IllegalArgumentException::class.java) {
            RssParser.parse("""<!DOCTYPE rss [<!ENTITY secret SYSTEM "file:///etc/passwd">]><rss><channel><item><title>&secret;</title><link>https://example.com/a</link></item></channel></rss>""")
        }
        assertThrows(IllegalArgumentException::class.java) {
            RssParser.parse("<rss><channel>" + "<item><link>https://example.com/a</link></item>".repeat(2) + "</channel></rss>", 1)
        }
    }

    @Test fun youtubeFeedIsDiscoveredOnlyWithValidChannelId() {
        val id = "UC" + "a".repeat(22)
        val scan = ScanParser.parse("""{"entries":[],"channel_id":"$id"}""", "https://www.youtube.com/@fixture/videos")
        assertEquals("https://www.youtube.com/feeds/videos.xml?channel_id=$id", scan.feedUrl)
    }

    @Test fun pornhubFlatEntriesNeedCompleteVersionedManifest() {
        val url = "https://rt.pornhub.com/model/fixture"
        val listing = PornHubCollections.listingUrl(url)!!
        val json = """{"entries":[{"_type":"url","ie_key":"PornHub","id":"ph1","url":"https://www.pornhub.com/view_video.php?viewkey=ph1","vd_page":1}],"vd_ph_scan":{"version":1,"complete":true,"verified_first_page":true,"source_url":"$url","listing_url":"$listing","item_count":1,"pages":[{"number":1,"count":1,"has_next":false}]}}"""
        assertEquals(1, ScanParser.parse(json, url).items.size)
        assertThrows(IllegalArgumentException::class.java) { ScanParser.parse(json.replace("\"complete\":true", "\"complete\":false"), url) }
        assertThrows(IllegalArgumentException::class.java) { ScanParser.parse(json.replace("\"count\":1", "\"count\":2"), url) }
        assertNull(PornHubCollections.listingUrl("https://evil.com/model/fixture"))
    }
}
