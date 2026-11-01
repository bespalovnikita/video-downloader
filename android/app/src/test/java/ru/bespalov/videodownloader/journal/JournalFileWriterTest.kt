package ru.bespalov.videodownloader.journal

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test
import ru.bespalov.video.core.DiagnosticJournal
import ru.bespalov.video.core.JournalLevel
import ru.bespalov.video.core.JournalProfile
import ru.bespalov.video.core.JournalRecord
import java.io.File
import java.nio.file.Files

class JournalFileWriterTest {
    @Test fun realWriterFlushesSanitizedMultilineRecordsInCaptureOrderAndClearKeepsFiles() = runBlocking {
        val directory = Files.createTempDirectory("vd-android-journal").toFile()
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        try {
            val journal = DiagnosticJournal(JournalProfile.Detailed)
            val writer = JournalFileWriter(directory, scope, journal::fileWarning)
            journal.fileSink = { writer.enqueue(it); Unit }
            repeat(20) { journal.write(JournalRecord(JournalLevel.Verbose, "yt-dlp", "sample-$it\nCookie: PRIVATE_$it")) }
            writer.flush()
            assertEquals(journal.snapshotText(), writer.file.readText())
            assertFalse(writer.file.readText().contains("PRIVATE_"))
            val text = writer.file.readText()
            journal.clear()
            assertEquals("", journal.snapshotText())
            assertEquals(text, writer.file.readText())
            writer.closeAndJoin()
            assertEquals(0L, writer.droppedEntries)
            assertEquals("", writer.lastError)
            // The handle is released: the actual file can be moved after shutdown.
            val moved = File(directory, "closed.log")
            assertTrue(writer.file.renameTo(moved)); assertEquals(text, moved.readText())
        } finally { scope.cancel(); directory.deleteRecursively() }
    }

    @Test fun actualRotationKeepsOnlyThreeFilesWithinByteLimitAndNewestRecord() = runBlocking {
        val directory = Files.createTempDirectory("vd-android-journal-rotation").toFile()
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        try {
            val warnings = mutableListOf<String>()
            val writer = JournalFileWriter(directory, scope, { synchronized(warnings) { warnings += it } }, JournalFileLimits(1024, 100))
            repeat(20) { assertTrue(writer.enqueue("record-$it " + "я".repeat(120))) }
            writer.closeAndJoin()
            val files = directory.listFiles()!!.filter { it.name.startsWith("journal") }
            assertEquals(3, files.size)
            assertTrue(files.all { it.length() <= 1024 })
            assertTrue(writer.file.readText().contains("record-19"))
            assertTrue(warnings.isEmpty())
        } finally { scope.cancel(); directory.deleteRecursively() }
    }

    @Test fun blockedStorageOnlyWarnsAndNeverChangesAnUnrelatedFile() = runBlocking {
        val root = Files.createTempDirectory("vd-android-journal-blocked").toFile()
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        try {
            val blocked = File(root, "blocked").apply { writeText("unrelated sentinel") }
            val journal = DiagnosticJournal()
            val writer = JournalFileWriter(blocked, scope, journal::fileWarning)
            journal.fileSink = { writer.enqueue(it); Unit }
            journal.writeMessage("visible despite blocked file")
            writer.closeAndJoin()
            assertTrue(writer.lastError.contains("Не удалось"))
            assertTrue(journal.snapshotText().contains("visible despite blocked file"))
            assertTrue(journal.snapshotText().contains("Журнал в памяти, копирование и экспорт остаются доступны"))
            assertEquals("unrelated sentinel", blocked.readText())
        } finally { scope.cancel(); root.deleteRecursively() }
    }

    @Test fun linkedDirectoryAndDanglingFileCannotRedirectJournalCreation() = runBlocking {
        val root = Files.createTempDirectory("vd-android-journal-links").toFile()
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        try {
            val outside = File(root, "outside").apply { mkdirs() }
            val sentinel = File(outside, "journal.log").apply { writeText("outside sentinel") }
            val link = File(root, "linked")
            Files.createSymbolicLink(link.toPath(), outside.toPath())
            val journal = DiagnosticJournal()
            val writer = JournalFileWriter(File(link, "child"), scope, journal::fileWarning)
            writer.enqueue("must remain in memory"); writer.closeAndJoin()
            assertTrue(writer.lastError.contains("символической"))
            assertFalse(File(outside, "child").exists()); assertEquals("outside sentinel", sentinel.readText())
            Files.delete(link.toPath())

            val safe = File(root, "safe").apply { mkdirs() }
            val absent = File(outside, "must-not-create")
            val dangling = File(safe, "journal.log")
            Files.createSymbolicLink(dangling.toPath(), absent.toPath())
            val second = JournalFileWriter(safe, scope, journal::fileWarning)
            second.enqueue("must not follow dangling link"); second.closeAndJoin()
            assertTrue(second.lastError.contains("символической")); assertFalse(absent.exists())
            Files.delete(dangling.toPath())
        } finally { scope.cancel(); root.deleteRecursively() }
    }

    @Test fun anUnexpectedDirectoryIsNeverMovedOrDeletedAsARotatedLog() = runBlocking {
        val root = Files.createTempDirectory("vd-android-journal-directory").toFile()
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        try {
            for (name in listOf("journal.log", "journal.1.log", "journal.2.log")) {
                val directory = File(root, name).apply { mkdirs() }
                val sentinel = File(directory, "unrelated").apply { writeText("keep this directory") }
                val writer = JournalFileWriter(root, scope, {}, JournalFileLimits(fileBytes = 256))
                writer.enqueue("a record that must not move a directory")
                writer.closeAndJoin()
                assertTrue(writer.lastError.contains("обычным файлом"))
                assertTrue(directory.isDirectory)
                assertEquals("keep this directory", sentinel.readText())
                assertEquals(listOf(name), root.listFiles()!!.map { it.name })
                directory.deleteRecursively()
            }
        } finally { scope.cancel(); root.deleteRecursively() }
    }

    @Test fun pressureDropsAreBoundedVisibleAndDoNotBlockProducer() = runTest {
        val directory = Files.createTempDirectory("vd-android-journal-pressure").toFile()
        try {
            val journal = DiagnosticJournal()
            val writer = JournalFileWriter(directory, this, journal::fileWarning, JournalFileLimits(1024, 1), StandardTestDispatcher(testScheduler))
            assertTrue(writer.enqueue("accepted record"))
            repeat(10) { assertFalse(writer.enqueue("dropped-$it")) }
            assertEquals(10L, writer.droppedEntries)
            writer.closeAndJoin()
            assertTrue(writer.file.readText().contains("accepted record"))
            assertFalse(writer.file.readText().contains("dropped-"))
            assertTrue(journal.snapshotText().contains("пропущено 10 записей"))
        } finally { directory.deleteRecursively() }
    }

    @Test fun cancellationBeforeWriterStartsReleasesAQueuedFlushBarrier() = runTest {
        val directory = Files.createTempDirectory("vd-android-journal-cancel").toFile()
        val dispatcher = StandardTestDispatcher(testScheduler)
        val owner = CoroutineScope(SupervisorJob() + dispatcher)
        try {
            val writer = JournalFileWriter(directory, owner, {}, dispatcher = dispatcher)
            assertTrue(writer.enqueue("not written after shutdown"))
            val flushing = async(start = CoroutineStart.UNDISPATCHED) { writer.flush() }
            assertFalse(flushing.isCompleted)
            owner.cancel()
            testScheduler.runCurrent()
            assertTrue("A flush must not wait forever when launch never ran", flushing.isCompleted)
            flushing.await()
            writer.closeAndJoin()
            assertFalse(writer.file.exists())
            assertFalse(writer.enqueue("writer already stopped"))
        } finally { owner.cancel(); directory.deleteRecursively() }
    }
}
