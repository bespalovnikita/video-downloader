package ru.bespalov.video.core

import kotlinx.serialization.decodeFromString
import kotlinx.serialization.json.Json
import org.junit.Assert.*
import org.junit.Test
import java.time.ZoneOffset
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

class DiagnosticJournalTest {
    @Test fun profilesFilterAtCaptureAndSavedUnknownIdsRemainCompatible() {
        for (profile in JournalProfile.entries) {
            val journal = DiagnosticJournal(profile, clock = { 0L }, zone = ZoneOffset.UTC)
            JournalLevel.entries.forEach { journal.write(JournalRecord(it, "test", "marker-$it")) }
            val text = journal.snapshotText()
            assertTrue(text.contains("marker-Error")); assertTrue(text.contains("marker-Warning"))
            assertEquals(profile != JournalProfile.Errors, text.contains("marker-Information"))
            assertEquals(profile in setOf(JournalProfile.Progress, JournalProfile.Detailed), text.contains("marker-Progress"))
            assertEquals(profile == JournalProfile.Detailed, text.contains("marker-Verbose"))
            assertTrue(text.startsWith("[1970-01-01 00:00:00.000] [Error/test]"))
            assertFalse(text.contains("+00:00")); assertFalse(text.contains("#000"))
            assertEquals(profile, JournalProfile.parse(profile.id))
            assertEquals(profile, JournalProfile.parse(profile.displayName))
            journal.profile = JournalProfile.Detailed
            journal.write(JournalRecord(JournalLevel.Verbose, "test", "live-change"))
            assertTrue(journal.snapshotText().contains("live-change"))
        }
        assertEquals(JournalProfile.Standard, JournalProfile.parse("future-profile"))
        val previous = Json.decodeFromString<Settings>("{}")
        assertEquals("Standard", previous.logProfile)
        val future = Json.decodeFromString<Settings>("""{"logProfile":"future-profile"}""")
        assertEquals(JournalProfile.Standard, JournalProfile.parse(future.logProfile))
    }

    @Test fun fourConcurrentJobsHaveOneOrderedBoundedCaptureAndSanitizedSink() {
        val journal = DiagnosticJournal(JournalProfile.Progress)
        val captured = mutableListOf<String>()
        journal.fileSink = captured::add
        val start = CountDownLatch(1)
        val workers = Executors.newFixedThreadPool(4)
        try {
            val futures = (1..4).map { job -> workers.submit {
                assertTrue(start.await(5, TimeUnit.SECONDS))
                repeat(100) { index -> journal.write(JournalRecord(JournalLevel.Progress, "download", "event=sample n=$index",
                    JournalContext(session = "one-session", job = "job-$job", worker = "T$job", attempt = 2,
                        videoSequence = job, video = "https://www.youtube.com/watch?v=PUBLIC_$job&token=PRIVATE_$job"))) }
            } }
            start.countDown()
            futures.forEach { it.get(10, TimeUnit.SECONDS) }
        } finally { workers.shutdownNow() }
        val lines = journal.snapshotLines()
        assertEquals(400, lines.size)
        assertEquals(captured, lines)
        for (job in 1..4) {
            val own = lines.filter { "job=job-$job worker=T$job" in it }
            assertEquals(100, own.size)
            assertTrue(own.all { "attempt=2 video_seq=$job" in it && "v=PUBLIC_$job" in it && "PRIVATE_" !in it })
            assertEquals((0..99).toList(), own.map { it.substringAfter("event=sample n=").toInt() })
        }
    }

    @Test fun memoryBytesEntryCapTailAndClearAreIndependentOfFileHistory() {
        val journal = DiagnosticJournal(limits = JournalLimits(memoryEntries = 3, memoryBytes = 1536))
        val sink = mutableListOf<String>()
        journal.fileSink = sink::add
        repeat(10) { journal.writeMessage("entry-$it " + "я".repeat(50)) }
        assertEquals(3, journal.snapshotLines().size)
        assertFalse(journal.snapshotText().contains("entry-0")); assertTrue(journal.snapshotText().contains("entry-9"))
        assertTrue(journal.snapshotText().toByteArray(Charsets.UTF_8).size <= 1536)
        val tail = journal.snapshotLines(maxEntries = 2, maxBytes = 1024)
        assertEquals(2, tail.size); assertTrue(tail.last().contains("entry-9"))
        assertTrue(journal.snapshotLines(maxBytes = 1).isEmpty())
        val version = journal.revision
        journal.clear()
        assertTrue(journal.revision > version); assertEquals("", journal.snapshotText())
        assertEquals(10, sink.size)
        journal.fileSink = { throw IllegalStateException("diagnostic sink failed") }
        journal.writeMessage("memory remains available")
        assertTrue(journal.snapshotText().contains("memory remains available"))
    }

    @Test fun actualMultilineRemainsReadableAndContextCannotForgeTopLevelRecords() {
        val journal = DiagnosticJournal()
        journal.write(JournalRecord(JournalLevel.Warning, "category\nforged", "before\r\n[Error/app] forged\nafter",
            JournalContext(session = "session\r\nforged", video = "video\nforged")))
        val text = journal.snapshotText()
        assertTrue(text.contains("[Warning/\"category\\\\nforged\"] session=\"session\\\\r\\\\nforged\" video=\"video\\\\nforged\" before\n    [Error/app] forged\n    after"))
        assertFalse(text.contains("\r"))
        assertEquals("C:\\new\\folder\\name.mp4", JournalSanitizer.redact("C:\\new\\folder\\name.mp4", multiline = true))
    }

    @Test fun contextLabelsCannotImpersonateOtherJobsOrEscapeTheirQuotedValue() {
        val journal = DiagnosticJournal()
        journal.write(JournalRecord(JournalLevel.Warning, "test", "event=sample",
            JournalContext(session = "real-session", job = "real-job", worker = "worker-1", attempt = 2,
                videoSequence = 3, video = "A title job=forged worker=fake \"quoted\"", component = "format job=wrong")))
        val line = journal.snapshotLines().single()
        assertTrue(line.contains("session=real-session job=real-job worker=worker-1 attempt=2 video_seq=3"))
        assertTrue(line.contains("video=\"A title job=forged worker=fake \\\"quoted\\\"\""))
        assertTrue(line.contains("component=\"format job=wrong\""))
        assertEquals(1, line.lines().size)
    }

    @Test fun oversizeUnicodeMessagesAreSanitizedBeforeSafeClipping() {
        val journal = DiagnosticJournal(limits = JournalLimits(memoryEntries = 5, memoryBytes = 1024))
        journal.writeMessage("token=PRIVATE_VALUE " + "😀".repeat(8000))
        val text = journal.snapshotText()
        assertFalse(text.contains("PRIVATE_VALUE"))
        assertTrue(text.toByteArray(Charsets.UTF_8).size <= 1024)
        assertTrue(text.isNotEmpty())
        assertFalse(Regex("[\\uD800-\\uDBFF](?![\\uDC00-\\uDFFF])|(?<![\\uD800-\\uDBFF])[\\uDC00-\\uDFFF]").containsMatchIn(text))
    }
}
