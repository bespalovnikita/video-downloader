package ru.bespalov.videodownloader.journal

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch
import ru.bespalov.video.core.JournalSanitizer
import java.io.File
import java.io.FileOutputStream
import java.nio.file.Files
import java.nio.file.LinkOption
import java.util.concurrent.atomic.AtomicLong

data class JournalFileLimits(val fileBytes: Int = 5 * 1024 * 1024, val queuedEntries: Int = 512) {
    init { require(fileBytes >= 256 && queuedEntries > 0) }
}

/** Receives already sanitized records. Producers never wait for storage. */
class JournalFileWriter(
    directory: File,
    scope: CoroutineScope,
    private val warning: (String) -> Unit,
    private val limits: JournalFileLimits = JournalFileLimits(),
    dispatcher: CoroutineDispatcher = Dispatchers.IO,
) {
    private sealed interface Work {
        data class Line(val value: String) : Work
        data class Flush(val done: CompletableDeferred<Unit>) : Work
    }
    private val directory = directory.absoluteFile
    val file: File = File(this.directory, "journal.log")
    private val queue = Channel<Work>(limits.queuedEntries)
    private val dropped = AtomicLong()
    val droppedEntries: Long get() = dropped.get()
    @Volatile var lastError: String = ""
        private set
    private var output: FileOutputStream? = null
    private var writtenBytes = 0L
    private val writer = scope.launch(dispatcher) {
        var disabled = false
        var reportedDropped = 0L
        try {
            for (work in queue) {
                if (work is Work.Flush) {
                    try { output?.flush() } catch (error: Exception) { fail(error); disabled = true }
                    work.done.complete(Unit)
                    continue
                }
                if (disabled) continue
                try {
                    writeLine((work as Work.Line).value)
                    val lost = dropped.get()
                    if (lost > reportedDropped) {
                        reportedDropped = lost
                        // Do not enqueue a warning recursively into the same full queue.
                        report("Очередь записи файла журнала переполнена: пропущено $lost записей. Уменьшите подробность журнала.")
                    }
                } catch (error: Exception) {
                    fail(error); disabled = true
                    try { output?.close() } catch (_: Exception) { }
                    output = null
                }
            }
        } finally {
            queue.close()
            try { output?.flush() } catch (error: Exception) { fail(error) }
            try { output?.close() } catch (error: Exception) { fail(error) }
            output = null
            // Cancelled application scopes must not strand a caller waiting on a barrier.
            while (true) {
                val work = queue.tryReceive().getOrNull() ?: break
                if (work is Work.Flush) work.done.complete(Unit)
            }
        }
    }

    init {
        // A cancelled scope can prevent the launch body (and its finally) from
        // running at all. Completion still releases every queued flush barrier.
        writer.invokeOnCompletion {
            queue.close()
            while (true) {
                val work = queue.tryReceive().getOrNull() ?: break
                if (work is Work.Flush) work.done.complete(Unit)
            }
        }
    }

    fun enqueue(sanitizedLine: String): Boolean {
        val sent = queue.trySend(Work.Line(sanitizedLine)).isSuccess
        if (!sent) dropped.incrementAndGet()
        return sent
    }

    suspend fun flush() {
        if (!writer.isActive) return
        val done = CompletableDeferred<Unit>()
        if (queue.trySend(Work.Flush(done)).isFailure) {
            try { queue.send(Work.Flush(done)) } catch (_: Exception) { return }
        }
        done.await()
    }

    suspend fun closeAndJoin() { queue.close(); writer.join() }

    private fun report(message: String) { try { warning(message) } catch (_: Exception) { } }

    private fun fail(error: Exception) {
        if (lastError.isNotEmpty()) return
        // The Journal sanitizes this diagnostic before retaining it.
        lastError = "Не удалось записать файл журнала: ${JournalSanitizer.redact(error.message ?: error.javaClass.simpleName)}"
        report("$lastError. Журнал в памяти, копирование и экспорт остаются доступны.")
    }

    private fun writeLine(line: String) {
        var bytes = (line + "\n").toByteArray(Charsets.UTF_8)
        if (bytes.size > limits.fileBytes) {
            var length = minOf(line.length, (limits.fileBytes - 4) / 4)
            if (length > 0 && line[length - 1].isHighSurrogate()) length--
            bytes = (line.take(length) + "…\n").toByteArray(Charsets.UTF_8)
        }
        if (output != null && writtenBytes + bytes.size > limits.fileBytes) {
            output!!.flush(); output!!.close(); output = null
        }
        if (output == null) {
            refuseLinkedAncestors(directory)
            check(directory.mkdirs() || directory.isDirectory) { "Не удалось создать папку журнала." }
            refuseLinkedAncestors(directory)
            val previous = File(directory, "journal.1.log")
            val oldest = File(directory, "journal.2.log")
            listOf(file, previous, oldest).forEach { target ->
                refuseLink(target)
                check(!Files.exists(target.toPath(), LinkOption.NOFOLLOW_LINKS) ||
                    Files.isRegularFile(target.toPath(), LinkOption.NOFOLLOW_LINKS)) {
                    "Путь файла журнала не является обычным файлом: $target"
                }
            }
            if (file.exists() && file.length() + bytes.size > limits.fileBytes) {
                if (oldest.exists()) check(oldest.delete()) { "Не удалось удалить старый файл журнала." }
                if (previous.exists()) check(previous.renameTo(oldest)) { "Не удалось переместить старый файл журнала." }
                check(file.renameTo(previous)) { "Не удалось сменить файл журнала." }
            }
            output = FileOutputStream(file, true)
            writtenBytes = file.length()
        }
        output!!.write(bytes)
        output!!.flush()
        writtenBytes += bytes.size
    }

    private fun refuseLink(file: File) { check(!Files.isSymbolicLink(file.toPath())) { "Путь журнала является символической ссылкой: $file" } }
    private fun refuseLinkedAncestors(file: File) {
        var current: File? = file
        while (current != null) { refuseLink(current); current = current.parentFile }
    }
}
