package ru.bespalov.videodownloader.engine

import org.junit.Assert.*
import org.junit.Test
import ru.bespalov.video.core.ScanScope
import ru.bespalov.video.core.SubscriptionScanLimits

class SubscriptionScanArgumentsTest {
    private fun List<String>.option(name: String) = get(indexOf(name).also { assertTrue("Missing $name", it >= 0) } + 1)

    @Test fun genericFullArchiveUnwrapsEntriesAndFetchesOneExtraItemToDetectTruncation() {
        val args = SubscriptionScanArguments.build("https://example.com/collection", "/private/plugins", ScanScope.Full,
            SubscriptionScanLimits(maxItems = 42))
        assertTrue(args.contains("--ignore-config"))
        assertTrue(args.contains("--abort-on-error"))
        assertTrue(args.contains("--no-flat-playlist"))
        assertEquals("43", args.option("--playlist-end"))
        assertFalse(args.contains("--flat-playlist"))
        assertFalse(args.contains("--impersonate"))
        assertFalse(args.contains("--plugin-dirs"))
    }

    @Test fun aRecentYoutubeProbeRequestsOnlyTheNewestTwentyFlatEntries() {
        val args = SubscriptionScanArguments.build("https://www.youtube.com/@fixture/videos", "/private/plugins", ScanScope.RecentWindow)
        assertTrue(args.contains("--flat-playlist"))
        assertFalse(args.contains("--no-flat-playlist"))
        assertEquals("20", args.option("--playlist-end"))
        assertFalse(args.contains("--impersonate"))
    }

    @Test fun pornhubUsesTheBundledCollectorAndOnlyTheRecentRequestEnablesItsWindow() {
        val full = SubscriptionScanArguments.build("https://rt.pornhub.com/model/fixture", "/private/plugins", ScanScope.Full)
        val recent = SubscriptionScanArguments.build("https://rt.pornhub.com/model/fixture", "/private/plugins", ScanScope.RecentWindow)
        for (args in listOf(full, recent)) {
            assertEquals("/private/plugins", args.option("--plugin-dirs"))
            assertEquals("videodownloader:pornhub:channel", args.option("--use-extractors"))
            assertTrue(args.contains("--no-plugin-dirs"))
            assertTrue(args.contains("--flat-playlist"))
            assertFalse(args.contains("--impersonate"))
        }
        assertEquals("VideoDownloaderPornHubChannel:max_items=10000;max_pages=500", full.option("--extractor-args"))
        assertEquals("VideoDownloaderPornHubChannel:max_items=10000;max_pages=500;recent_window=20", recent.option("--extractor-args"))
    }
}
