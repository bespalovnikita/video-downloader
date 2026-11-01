package ru.bespalov.videodownloader.engine

import org.junit.Assert.*
import org.junit.Test

class DownloadProgressPlanTest {
    private fun plan(json: String) = ProgressMarkers.parse("__VD_PLAN__:$json") as MarkerEvent.Plan
    private fun sample(component: String, percent: String, video: String = "none", audio: String = "mp4a") =
        ProgressMarkers.parse("__VD_PROGRESS__|$percent|1KiB/s|10|50|100|$component|$video|$audio") as MarkerEvent.Progress

    @Test fun selectedVideoAndAudioContributeToOneTaskWithoutAnAudioReset() {
        val aggregate = DownloadProgressPlan()
        var current = aggregate.begin(plan("""{"id":"test","format_id":"v+a","requested_formats.:.format_id":["v","a"]}"""),
            EngineProgress(title = "Название", attempt = 2, quality = "1080", invocation = 3))
        current = aggregate.observe(sample("test:v", "100", "avc1", "none"), current)
        assertEquals(49.5f, current.percent, 0.001f)
        assertEquals(100f, current.componentPercent!!, 0f)
        assertEquals(1, current.componentIndex)
        assertEquals(2, current.componentCount)
        assertTrue(current.planKnown)
        assertEquals(EngineStage.DOWNLOADING, current.stage)
        assertEquals("Название", current.title)
        assertEquals(2, current.attempt)
        assertEquals(3, current.invocation)
        assertEquals("1080", current.quality)
        current = aggregate.observe(sample("test:a", "0"), current)
        assertEquals(49.5f, current.percent, 0.001f)
        assertEquals(2, current.componentIndex)
        assertTrue(current.detail.contains("Аудио 2/2"))
        current = aggregate.observe(sample("test:a", "50"), current)
        assertEquals(74.25f, current.percent, 0.001f)
        for (unknown in listOf("N/A", "NaN", "Infinity", "-Infinity")) {
            current = aggregate.observe(sample("test:a", unknown), current)
            assertEquals(74.25f, current.percent, 0.001f)
            assertNull(current.componentPercent)
            assertTrue(current.detail.endsWith("?"))
        }
        current = aggregate.observe(sample("test:a", "20"), current)
        assertEquals("A real stream regression must remain visible", 59.4f, current.percent, 0.001f)
        current = aggregate.observe(sample("test:a", "100"), current)
        assertEquals("Publication alone may set task progress to 100", 99f, current.percent, 0.001f)
    }

    @Test fun knownPlaylistPositionIncludesEarlierMaterialsButNotFutureOnes() {
        val aggregate = DownloadProgressPlan()
        var current = aggregate.begin(plan("""{"id":"second","format_id":"single","playlist_autonumber":2,"n_entries":4}"""))
        assertEquals(24.75f, current.percent, 0.001f)
        current = aggregate.observe(sample("second:single", "0"), current)
        assertEquals(24.75f, current.percent, 0.001f)
        assertEquals(2, current.playlistIndex)
        assertEquals(4, current.playlistCount)
        assertTrue(current.detail.startsWith("Материал 2/4"))
        current = aggregate.observe(sample("second:single", "100"), current)
        assertEquals(49.5f, current.percent, 0.001f)
        current = aggregate.begin(plan("""{"id":"third","format_id":"single","playlist_autonumber":3,"n_entries":4}"""), current)
        assertEquals(49.5f, current.percent, 0.001f)
        current = aggregate.observe(sample("third:single", "100"), current)
        assertEquals(74.25f, current.percent, 0.001f)
    }

    @Test fun absentPlanAndNewInvocationUseAnHonestFallbackAndRealReset() {
        val aggregate = DownloadProgressPlan()
        var current = aggregate.observe(sample("old:v", "100"), EngineProgress())
        assertEquals(99f, current.percent, 0f)
        assertFalse(current.planKnown)
        assertNull(current.componentCount)
        assertTrue(current.detail.contains("1/?"))
        aggregate.reset()
        current = aggregate.observe(sample("new:v", "20"), EngineProgress(attempt = 2, invocation = 2))
        assertEquals(20f, current.percent, 0f)
        assertEquals(2, current.attempt)
        current = aggregate.observe(sample("new:v", "N/A"), current)
        assertEquals(20f, current.percent, 0f)
    }

    @Test fun planKeepsKnownComponentsWhenOptionalPlaylistCountsAreUnknown() {
        val aggregate = DownloadProgressPlan()
        val parsed = plan("""{"id":"one","requested_formats.:.format_id":["v","a"],"playlist_autonumber":null,"n_entries":null}""")
        var current = aggregate.begin(parsed)
        assertNull(current.playlistCount)
        assertNull(current.playlistIndex)
        current = aggregate.observe(sample("one:v", "100"), current)
        assertEquals(49.5f, current.percent, 0.001f)
        assertTrue(current.planKnown)
    }

    @Test fun invalidOptionalPlaylistPositionsKeepTheKnownVideoAudioPlan() {
        for (position in listOf("null", "\"NA\"", "2.5", "{}", "-1", "2147483648")) {
            val aggregate = DownloadProgressPlan()
            val parsed = plan("""{"id":"one","requested_formats.:.format_id":["v","a"],"playlist_autonumber":1,"n_entries":$position}""")
            var current = aggregate.begin(parsed)
            assertEquals(0, parsed.mediaIndex)
            assertEquals(0, parsed.mediaCount)
            assertNull(current.playlistIndex)
            assertNull(current.playlistCount)
            current = aggregate.observe(sample("one:v", "100"), current)
            assertEquals(position, 49.5f, current.percent, 0.001f)
            assertTrue(current.planKnown)
        }
    }

    @Test fun optionalPlanParsingIsBoundedAndCannotBreakTheDownload() {
        for (bad in listOf("not-json", "[]", "{",
            "[".repeat(9) + "0" + "]".repeat(9), " ".repeat(32769))) {
            assertNull(bad.take(80), ProgressMarkers.parse("__VD_PLAN__:$bad"))
        }
        val many = (1..40).joinToString(",") { "\"f$it\"" }
        val limited = plan("""{"id":"bounded","requested_formats.:.format_id":[$many]}""")
        assertEquals(32, limited.formatIds.size)
        val duplicated = plan("""{"id":"dupe","format_id":"fallback","requested_formats.:.format_id":["v","v","a",null,5,""]}""")
        assertEquals(listOf("v", "a"), duplicated.formatIds)
        val impossible = plan("""{"format_id":"single","playlist_autonumber":8,"n_entries":4}""")
        assertEquals(0, impossible.mediaIndex)
        assertEquals(0, impossible.mediaCount)
        // Brackets inside ID strings do not consume parser nesting depth.
        assertEquals("[{{{{{{{{{{", plan("""{"id":"[{{{{{{{{{{","format_id":"single"}""").mediaId)
    }

    @Test fun extendedProgressKeepsCodecsAndUnknownBytesSeparateFromZero() {
        val extended = sample("id:137", "12.5%", "avc1", "none")
        assertEquals("avc1", extended.videoCodec)
        assertEquals("none", extended.audioCodec)
        val unknown = ProgressMarkers.parse("__VD_PROGRESS__|N/A|N/A|N/A|N/A|N/A|id:137|avc1|none") as MarkerEvent.Progress
        assertNull(unknown.value.componentPercent)
        assertFalse(unknown.downloadedBytesKnown)
        val zero = ProgressMarkers.parse("__VD_PROGRESS__|0%|N/A|N/A|0|N/A|id:137") as MarkerEvent.Progress
        assertTrue(zero.downloadedBytesKnown)
        assertEquals(0f, zero.value.componentPercent!!, 0f)
    }

    @Test fun knownMediaPlanIgnoresSidecarsAndOtherMaterialsWithoutChangingItsDenominator() {
        val aggregate = DownloadProgressPlan()
        var current = aggregate.begin(plan("""{"id":"media:one","requested_formats.:.format_id":["video:137","audio:140"]}"""))
        current = aggregate.observe(sample("media:one:video:137", "100", "avc1", "none"), current)
        assertEquals(49.5f, current.percent, 0.001f)
        for (component in listOf("media:one:subtitle", "media:one:thumbnail", "another:audio:140")) {
            val auxiliary = sample(component, "100")
            assertFalse(component, aggregate.accepts(auxiliary))
            assertSame("Foreign hooks must not change the selected media snapshot", current, aggregate.observe(auxiliary, current))
        }
        current = aggregate.observe(sample("media:one:audio:140", "50"), current)
        assertEquals(74.25f, current.percent, 0.001f)
        assertEquals(2, current.componentIndex)
        assertEquals(2, current.componentCount)
        assertTrue(aggregate.accepts(sample("media:one:audio:140", "100")))
    }

    @Test fun unknownComponentTrackingIsBoundedAndNewMaterialClearsIt() {
        val aggregate = DownloadProgressPlan()
        var current = EngineProgress()
        for (index in 1..256) {
            current = aggregate.observe(sample("unknown:$index", "20"), current)
            assertEquals(index, current.componentIndex)
        }
        // The next untrusted label is not retained as an ever-growing part list.
        current = aggregate.observe(sample("very-long:" + "x".repeat(100_000), "30"), current)
        assertNull(current.componentIndex)
        assertNull(current.componentCount)
        assertTrue(current.detail.contains("?/?"))
        assertEquals(30f, current.percent, 0f)
        current = aggregate.observe(sample("unknown:17", "40"), current)
        assertEquals(17, current.componentIndex)
        aggregate.reset()
        current = aggregate.observe(sample("another:1", "10"), EngineProgress())
        assertEquals(1, current.componentIndex)
        assertEquals(10f, current.percent, 0f)
    }

    @Test fun missingMediaIdentityUsesAnExplicitUnknownPlanAndBeginClearsOldStreamBytes() {
        val aggregate = DownloadProgressPlan()
        var current = aggregate.begin(plan("""{"requested_formats.:.format_id":["v","a"]}"""),
            EngineProgress(downloadedBytes = 100, totalBytes = 200, transferredBytes = 300))
        assertFalse(current.planKnown)
        assertEquals(0L, current.downloadedBytes)
        assertEquals(0L, current.totalBytes)
        assertEquals(300L, current.transferredBytes)
        current = aggregate.observe(sample("unknown:v", "50"), current)
        assertFalse(current.planKnown)
        assertNull(current.componentCount)
        assertEquals(50f, current.percent, 0f)
    }
}
