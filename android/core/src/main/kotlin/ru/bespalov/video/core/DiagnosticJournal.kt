package ru.bespalov.video.core

import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.ArrayDeque
import java.util.Locale

enum class JournalProfile(val id: String, val displayName: String) {
    Errors("Errors", "Только ошибки"),
    Standard("Standard", "Обычный"),
    Progress("Progress", "Прогресс загрузок"),
    Detailed("Detailed", "Подробный yt-dlp");

    companion object {
        fun parse(value: String?): JournalProfile = entries.firstOrNull { it.id == value || it.displayName == value } ?: Standard
    }
}

enum class JournalLevel { Error, Warning, Information, Progress, Verbose }

data class JournalContext(
    val session: String = "",
    val job: String = "",
    val worker: String = "",
    val attempt: Int = 0,
    val videoSequence: Int? = null,
    val video: String = "",
    val component: String = "",
    val quality: String = "",
)

data class JournalRecord(val level: JournalLevel, val category: String, val message: String,
    val context: JournalContext = JournalContext())

data class JournalLimits(val memoryEntries: Int = 5000, val memoryBytes: Int = 2 * 1024 * 1024) {
    init { require(memoryEntries > 0 && memoryBytes >= 256) }
}

/** Capture is bounded and serialized, and never does file I/O or calls Android UI. */
class DiagnosticJournal(
    profile: JournalProfile = JournalProfile.Standard,
    private val limits: JournalLimits = JournalLimits(),
    private val clock: () -> Long = System::currentTimeMillis,
    private val zone: ZoneId = ZoneId.systemDefault(),
) {
    private data class Entry(val sequence: Long, val text: String, val bytes: Int)
    private val gate = Any()
    private val entries = ArrayDeque<Entry>()
    private var memoryBytes = 0
    private var sequence = 0L
    @Volatile private var currentProfile = profile
    @Volatile var revision: Long = 0L
        private set
    /** The sink receives only a bounded, sanitized record. It must enqueue without blocking. */
    @Volatile var fileSink: ((String) -> Unit)? = null

    var profile: JournalProfile
        get() = currentProfile
        set(value) {
            synchronized(gate) {
                if (currentProfile == value) return
                currentProfile = value
                append(JournalRecord(JournalLevel.Information, "journal", "Профиль журнала: ${value.displayName}"), true, force = true)
            }
        }

    fun isEnabled(level: JournalLevel): Boolean = when (level) {
        JournalLevel.Error, JournalLevel.Warning -> true
        JournalLevel.Information -> currentProfile != JournalProfile.Errors
        JournalLevel.Progress -> currentProfile == JournalProfile.Progress || currentProfile == JournalProfile.Detailed
        JournalLevel.Verbose -> currentProfile == JournalProfile.Detailed
    }

    fun write(record: JournalRecord) {
        if (isEnabled(record.level)) append(record, true)
    }

    fun writeMessage(message: String) {
        if (message.isBlank()) return
        val trimmed = message.trimStart()
        val level = when {
            listOf("WARNING", "Transient error on ", "Format unavailable at ", "Preview ", "Thumbnail ").any { trimmed.startsWith(it, true) } -> JournalLevel.Warning
            listOf("ERROR", "Failed:", "Cannot ", "Removal failed", "Initialization:", "Subscription ", "Dependency operation failed", "Shutdown cleanup failed", "Ошибка", "Не удалось", "System.").any { trimmed.startsWith(it, true) } -> JournalLevel.Error
            else -> JournalLevel.Information
        }
        write(JournalRecord(level, "app", message))
    }

    /** File failure/pressure is retained once by the sink without recursively writing it. */
    fun fileWarning(message: String) { append(JournalRecord(JournalLevel.Warning, "journal", message), false) }

    private fun append(record: JournalRecord, toFile: Boolean, force: Boolean = false) {
        // Sanitize each field before clipping/quoting; context cannot forge a new record.
        val message = JournalSanitizer.redact(record.message, multiline = true)
        fun context(value: String, maximum: Int = 128): String {
            val sanitized = JournalSanitizer.safeTake(JournalSanitizer.redact(value), maximum)
            return if (sanitized.any { it.isWhitespace() || it in "\"\\[]" })
                "\"" + sanitized.replace("\\", "\\\\").replace("\"", "\\\"") + "\"" else sanitized
        }
        val category = context(record.category)
        val source = record.context
        val fields = buildList {
            fun addField(name: String, value: String, maximum: Int = 128) {
                if (value.isNotBlank()) add("$name=${context(value, maximum)}")
            }
            addField("session", source.session)
            addField("job", source.job)
            addField("worker", source.worker)
            if (source.attempt > 0) add("attempt=${source.attempt}")
            source.videoSequence?.takeIf { it > 0 }?.let { add("video_seq=$it") }
            addField("video", source.video, 512)
            addField("component", source.component)
            addField("quality", source.quality)
        }.joinToString(" ")
        synchronized(gate) {
            // A profile change can race sanitization. Apply its current filter at
            // the same point that the record becomes visible in memory/files.
            if (!force && !isEnabled(record.level)) return
            val timestamp = formatter.format(Instant.ofEpochMilli(clock()).atZone(zone))
            var text = "[$timestamp] [${record.level}/$category]" + (if (fields.isBlank()) "" else " $fields") + " $message"
            var bytes = text.toByteArray(Charsets.UTF_8).size + 1
            if (bytes > limits.memoryBytes) {
                text = JournalSanitizer.safeTake(text, (limits.memoryBytes - 4) / 4) + "…"
                bytes = text.toByteArray(Charsets.UTF_8).size + 1
            }
            entries.addLast(Entry(++sequence, text, bytes)); memoryBytes += bytes
            while (entries.size > limits.memoryEntries || memoryBytes > limits.memoryBytes)
                memoryBytes -= entries.removeFirst().bytes
            revision++
            if (toFile) {
                // Preserve the exact capture order for concurrent producer callbacks.
                try { fileSink?.invoke(text) } catch (_: Exception) { /* Diagnostics never decide download success. */ }
            }
        }
    }

    fun snapshotLines(maxEntries: Int = limits.memoryEntries, maxBytes: Int = limits.memoryBytes): List<String> = synchronized(gate) {
        require(maxEntries >= 0 && maxBytes >= 0)
        val result = ArrayDeque<String>()
        var bytes = 0
        val iterator = entries.descendingIterator()
        while (iterator.hasNext() && result.size < maxEntries) {
            val entry = iterator.next()
            if (bytes + entry.bytes > maxBytes) break
            result.addFirst(entry.text); bytes += entry.bytes
        }
        result.toList()
    }

    fun snapshotText(): String = snapshotLines().let { if (it.isEmpty()) "" else it.joinToString("\n", postfix = "\n") }

    fun clear() = synchronized(gate) { entries.clear(); memoryBytes = 0; revision++ }

    private companion object {
        val formatter: DateTimeFormatter = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss.SSS", Locale.ROOT)
    }
}
