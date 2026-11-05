package ru.bespalov.video.core

import kotlinx.serialization.json.*
import org.junit.Assert.*
import org.junit.Test
import ru.bespalov.video.core.ScanFixtures.changed

class RecentScanParserTest {
    @Test fun onlyExplicitNewestTabsAndSupportedPublicationListsAreEligible() {
        val valid = listOf("https://www.youtube.com/@fixture/videos", "http://m.youtube.com/user/fixture/shorts/",
            "https://youtube.com:443/channel/UCfixture/streams", "https://www.youtube.com/c/fixture/videos")
        valid.forEach { assertTrue(it, RecentScanParser.isNewestYouTubeTab(it)) }
        val invalid = listOf("https://www.youtube.com/@fixture", "https://www.youtube.com/playlist?list=PL1",
            "https://www.youtube.com/@fixture/videos?sort=dd", "https://www.youtube.com/@fixture/videos#latest",
            "https://www.youtube.com:8443/@fixture/videos", "https://www.youtube.com.evil.invalid/@fixture/videos",
            "https://youtube.com@evil.invalid/@fixture/videos", "https://user:password@youtube.com/@fixture/videos",
            "https://пример.рф/@fixture/videos", "https://www.youtube.com/@fixture/playlists")
        invalid.forEach { assertFalse(it, RecentScanParser.isNewestYouTubeTab(it)) }
        val known = setOf(ScanFixtures.watch(1))
        assertTrue(RecentScanParser.eligible(ScanFixtures.YOUTUBE, true, known))
        assertTrue(RecentScanParser.eligible(ScanFixtures.PORNHUB, true, known))
        assertFalse(RecentScanParser.eligible(ScanFixtures.YOUTUBE, false, known))
        assertFalse(RecentScanParser.eligible(ScanFixtures.YOUTUBE, true, emptySet()))
        assertFalse(RecentScanParser.eligible(ScanFixtures.YOUTUBE, true, known, true))
    }

    @Test fun validatedFlatRoutesProduceCanonicalNewAddressesWithoutLosingOrder() {
        val original = ScanFixtures.recent((1..4).toList())
        val rows = (original.getValue("entries") as JsonArray).mapIndexed { index, raw ->
            val item = raw as JsonObject
            when (index) {
                0 -> item.changed("url", JsonPrimitive("https://www.youtube.com/shorts/${ScanFixtures.id(1)}"))
                1 -> item.changed("url", JsonPrimitive("https://www.youtube.com/live/${ScanFixtures.id(2)}"))
                2 -> item.changed("url", JsonPrimitive(ScanFixtures.id(3)))
                else -> JsonObject(item - "url")
            }
        }
        val channel = "UC" + "a".repeat(22)
        val json = original.changed("entries", JsonArray(rows)).changed("channel_id", JsonPrimitive(channel))
            .changed("channel", JsonPrimitive("A channel"))
        val parsed = RecentScanParser.parse(json.toString(), ScanFixtures.YOUTUBE)
        assertEquals(ScanScope.RecentWindow, parsed.scope)
        assertEquals((1..4).map(ScanFixtures::watch), parsed.items.map { it.url })
        assertEquals("A channel", parsed.suggestedName)
        assertEquals("https://www.youtube.com/feeds/videos.xml?channel_id=$channel", parsed.feedUrl)
    }

    @Test fun contradictoryCountsSourcesFlagsAndEntriesCannotProveANewestWindow() {
        val root = ScanFixtures.recent((1..3).toList())
        val rows = root.getValue("entries") as JsonArray
        val first = rows.first() as JsonObject
        val invalid = listOf(
            root.changed("extractor_key", JsonPrimitive("Generic")),
            root.changed("webpage_url", JsonPrimitive(ScanFixtures.YOUTUBE + "/")),
            root.changed("incomplete", JsonPrimitive(true)), root.changed("truncated", JsonPrimitive("false")),
            root.changed("playlist_count", JsonPrimitive(2)), root.changed("n_entries", JsonPrimitive("3")),
            root.changed("requested_entries", JsonArray(listOf(JsonPrimitive(1), JsonPrimitive(3), JsonPrimitive(4)))),
            root.changed("entries", JsonArray(listOf(rows[0], rows[0]))),
            root.changed("entries", JsonArray(listOf(JsonNull))),
            root.changed("entries", JsonArray(listOf(first.changed("availability", JsonPrimitive("private"))))),
            root.changed("entries", JsonArray(listOf(first.changed("id", JsonPrimitive("short"))))),
            root.changed("entries", JsonArray(listOf(first.changed("url", JsonPrimitive("https://evil.invalid/watch?v=${ScanFixtures.id(1)}"))))),
            root.changed("entries", JsonArray(listOf(first.changed("entries", JsonArray(emptyList()))))),
            ScanFixtures.recent((1..21).toList()),
        )
        invalid.forEach { value ->
            assertThrows("Must reject $value", RuntimeException::class.java) { RecentScanParser.parse(value.toString(), ScanFixtures.YOUTUBE) }
        }
    }

    @Test fun pornhubRequiresTheVersionedRecentProofAndConsistentPagination() {
        val root = ScanFixtures.porn((1..20).toList(), hasNext = true)
        val parsed = RecentScanParser.parse(root.toString(), ScanFixtures.PORNHUB)
        assertEquals(ScanScope.RecentWindow, parsed.scope)
        assertEquals(20, parsed.items.size)
        assertTrue(RecentScanParser.parse(ScanFixtures.porn(emptyList()).toString(), ScanFixtures.PORNHUB).items.isEmpty())
        val proof = root.getValue("vd_ph_recent") as JsonObject
        val invalid = listOf(
            root.changed("vd_ph_scan", JsonObject(emptyMap())),
            root.changed("truncated", JsonPrimitive(true)),
            root.changed("vd_ph_recent", proof.changed("version", JsonPrimitive("1"))),
            root.changed("vd_ph_recent", proof.changed("complete", JsonPrimitive(true))),
            root.changed("vd_ph_recent", proof.changed("scope", JsonPrimitive("full"))),
            root.changed("vd_ph_recent", proof.changed("order", JsonPrimitive("popular"))),
            root.changed("vd_ph_recent", proof.changed("source_url", JsonPrimitive("https://www.pornhub.com/model/another"))),
            root.changed("vd_ph_recent", proof.changed("listing_url", JsonPrimitive("https://www.pornhub.com/model/fixture"))),
            root.changed("vd_ph_recent", proof.changed("item_count", JsonPrimitive(19))),
            ScanFixtures.porn(listOf(1), hasNext = true),
        )
        invalid.forEach { value -> assertThrows(RuntimeException::class.java) { RecentScanParser.parse(value.toString(), ScanFixtures.PORNHUB) } }
    }

    @Test fun genericIdnAndUnderscoreHostsRemainValidFullSourcesWithoutInventingAFeed() {
        for (source in listOf("https://пример.рф/collection", "https://local_host/collection")) {
            assertTrue(source, Urls.isHttp(source))
            val parsed = ScanParser.parse(ScanFixtures.full(listOf(1)).toString(), source)
            assertEquals(ScanScope.Full, parsed.scope)
            assertEquals(listOf(ScanFixtures.watch(1)), parsed.items.map { it.url })
            assertEquals("", parsed.feedUrl)
        }
    }
}
