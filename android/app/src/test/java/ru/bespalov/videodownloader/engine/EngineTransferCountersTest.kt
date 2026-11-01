package ru.bespalov.videodownloader.engine

import org.junit.Assert.*
import org.junit.Test

class EngineTransferCountersTest {
    @Test fun streamsRetriesAndPauseResumeShareAbsoluteByteBaselines() {
        val counters = EngineTransferCounters()
        assertEquals(50L, counters.observe("video:v", 50).totalBytes)
        assertEquals(100L, counters.observe("video:v", 100).totalBytes)
        assertEquals(125L, counters.observe("video:a", 25).totalBytes)
        // Resume may report only audio; the completed video contributes once.
        assertEquals(125L, counters.observe("video:a", 25).totalBytes)
        assertEquals(150L, counters.observe("video:a", 50).totalBytes)
        assertEquals(150L, counters.observe("video:v", 100).totalBytes)
        assertEquals(150L, counters.totalBytes)
        // A result reads the same cumulative value; it is not another byte event.
        assertEquals(150L, counters.totalBytes)
        assertEquals(150L, counters.totalBytes)
        assertEquals(0L, EngineTransferCounters().totalBytes)
    }

    @Test fun genuineCounterRegressionCountsTheNewlyTransferredBytes() {
        val counters = EngineTransferCounters()
        counters.observe("id:format", 100)
        val reset = counters.observe("id:format", 10)
        assertTrue(reset.reset)
        assertEquals(110L, reset.totalBytes)
        assertEquals(110L, counters.observe("id:format", 10).totalBytes)
        assertEquals(130L, counters.observe("id:format", 30).totalBytes)
        assertEquals(130L, counters.observe("id:format", -1).totalBytes)
        assertEquals(140L, counters.observe("id:format", 40).totalBytes)
    }

    @Test fun boundedRetentionWarnsOnceAndKeepsCurrentStreamAcrossResume() {
        val counters = EngineTransferCounters(maximumComponents = 2)
        assertFalse(counters.observe("first", 100).newlyLimited)
        assertFalse(counters.observe("second", 100).newlyLimited)
        assertTrue(counters.observe("third", 100).newlyLimited)
        assertFalse(counters.observe("fourth", 100).newlyLimited)
        assertEquals(400L, counters.observe("fourth", 100).totalBytes)
        assertEquals(425L, counters.observe("fourth", 125).totalBytes)
        assertEquals(425L, counters.observe("third", 100).totalBytes)
    }

    @Test fun untrustedStreamIdsAndExtremeCountersStayBoundedAndFinite() {
        val counters = EngineTransferCounters(maximumComponents = 1)
        val longId = "ID".repeat(100_000)
        assertEquals(1L, counters.observe(longId, 1).totalBytes)
        assertEquals(2L, counters.observe(longId, 2).totalBytes)
        assertEquals(Long.MAX_VALUE, counters.observe("other", Long.MAX_VALUE).totalBytes)
        assertEquals(Long.MAX_VALUE, counters.observe("other", Long.MAX_VALUE).totalBytes)
        assertThrows(IllegalArgumentException::class.java) { EngineTransferCounters(0) }
    }
}
