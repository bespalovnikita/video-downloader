package ru.bespalov.videodownloader.engine

import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.io.PushbackInputStream
import java.nio.file.AtomicMoveNotSupportedException
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.security.MessageDigest
import java.time.LocalDate
import java.util.zip.ZipInputStream

/** Inspects the actual zipimport executable, independently of the wrapper's preferences. */
internal object RuntimeArchive {
    private const val VERSION_ENTRY = "yt_dlp/version.py"
    private const val MAX_VERSION_BYTES = 64 * 1024
    private const val MAX_ASSET_BYTES = 32 * 1024 * 1024
    private const val MAX_STUB_BYTES = 4 * 1024
    private const val MAX_SCANNED_BYTES = 64L * 1024 * 1024
    private const val MAX_ENTRIES = 20_000
    private val versionLine = Regex("(?m)^__version__[ \\t]*=[ \\t]*(['\"])([0-9.]+)\\1[ \\t]*\\r?$")
    private val versionShape = Regex("^[0-9]{4}\\.[0-9]{2}\\.[0-9]{2}(?:\\.[0-9]{1,12}){0,4}$")

    fun readVersion(file: File): String? = try {
        if (!file.isFile || Files.isSymbolicLink(file.toPath())) null
        else file.inputStream().buffered().use { source ->
            val input = PushbackInputStream(source, 4)
            if (!positionAtZip(input)) null
            else ZipInputStream(input).use(::readVersionEntry)
        }
    } catch (_: Exception) { null }

    /** A nightly timestamp sorts after the stable version from the same calendar day. */
    fun compareVersions(left: String, right: String): Int {
        val first = requireNotNull(components(left)) { "Invalid yt-dlp version: $left" }
        val second = requireNotNull(components(right)) { "Invalid yt-dlp version: $right" }
        for (index in 0 until maxOf(first.size, second.size)) {
            val difference = (first.getOrNull(index) ?: 0).compareTo(second.getOrNull(index) ?: 0)
            if (difference != 0) return difference
        }
        return 0
    }

    /** A failed copy, checksum, ZIP or version check leaves the previous executable untouched. */
    @Synchronized
    fun installIfOlder(
        target: File,
        bundledVersion: String,
        expectedSha256: String,
        openAsset: () -> InputStream,
    ): String {
        require(components(bundledVersion) != null) { "Invalid bundled yt-dlp version" }
        require(Regex("^[a-fA-F0-9]{64}$").matches(expectedSha256)) { "Invalid yt-dlp checksum" }
        require(target.isAbsolute && target.toPath().none { it.toString() in setOf(".", "..") }) {
            "Runtime path must be an absolute private path without traversal"
        }
        val parent = requireNotNull(target.parentFile)
        rejectLinks(parent, target)
        val current = readVersion(target)
        if (current != null && compareVersions(current, bundledVersion) >= 0) return current
        check(parent.isDirectory || parent.mkdirs()) { "Cannot create the private yt-dlp directory" }
        rejectLinks(parent, target)
        val temporary = File.createTempFile(".runtime-", ".tmp", parent)
        try {
            val digest = MessageDigest.getInstance("SHA-256")
            openAsset().use { input ->
                temporary.outputStream().use { output ->
                    val buffer = ByteArray(8192)
                    var total = 0L
                    while (true) {
                        val count = input.read(buffer)
                        if (count < 0) break
                        total += count
                        check(total <= MAX_ASSET_BYTES) { "Bundled yt-dlp exceeds its size limit" }
                        digest.update(buffer, 0, count)
                        output.write(buffer, 0, count)
                    }
                    output.fd.sync()
                }
            }
            val actualSha256 = digest.digest().joinToString("") { "%02x".format(it) }
            check(actualSha256.equals(expectedSha256, ignoreCase = true)) { "Bundled yt-dlp checksum mismatch" }
            check(readVersion(temporary) == bundledVersion) { "Bundled yt-dlp ZIP/version is invalid" }
            rejectLinks(parent, target)
            try {
                Files.move(temporary.toPath(), target.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING)
            } catch (_: AtomicMoveNotSupportedException) {
                // Same-directory rename is atomic on Android. Never delete an existing target
                // first to make a platform's unsupported replacement appear successful.
                if (!temporary.renameTo(target)) throw IOException("Cannot atomically install yt-dlp")
            }
            return bundledVersion
        } finally { temporary.delete() }
    }

    private fun rejectLinks(parent: File, target: File) {
        require(!Files.isSymbolicLink(parent.toPath()) && !Files.isSymbolicLink(target.toPath())) {
            "The private yt-dlp executable or directory is a symbolic link"
        }
        require(!target.exists() || target.isFile) { "The yt-dlp executable path is occupied by a directory" }
    }

    private fun components(value: String): List<Long>? = try {
        if (!versionShape.matches(value)) null else value.split('.').map(String::toLong).also {
            LocalDate.of(it[0].toInt(), it[1].toInt(), it[2].toInt())
        }
    } catch (_: Exception) { null }

    /** Android's ZIP readers do not consistently accept the Unix executable's shebang prefix. */
    private fun positionAtZip(input: PushbackInputStream): Boolean {
        val signature = byteArrayOf(0x50, 0x4b, 0x03, 0x04)
        val first = ByteArray(4)
        for (index in first.indices) {
            val value = input.read()
            if (value < 0) return false
            first[index] = value.toByte()
        }
        if (first.contentEquals(signature)) {
            input.unread(signature)
            return true
        }
        // Only skip the known Unix zipimport launcher, not arbitrary leading data.
        if (first[0] != '#'.code.toByte() || first[1] != '!'.code.toByte()) return false
        var window = 0
        for (value in first) window = (window shl 8) or (value.toInt() and 0xff)
        var consumed = first.size
        while (consumed < MAX_STUB_BYTES + signature.size) {
            val value = input.read()
            if (value < 0) return false
            window = (window shl 8) or value
            consumed++
            if (window == 0x504b0304) {
                input.unread(signature)
                return true
            }
        }
        return false
    }

    private fun readVersionEntry(archive: ZipInputStream): String? {
        val buffer = ByteArray(8192)
        var total = 0L
        repeat(MAX_ENTRIES) {
            val entry = archive.nextEntry ?: return null
            val isVersion = entry.name == VERSION_ENTRY && !entry.isDirectory
            if (entry.size > MAX_SCANNED_BYTES - total || (isVersion && entry.size > MAX_VERSION_BYTES)) return null
            val output = if (isVersion) ByteArrayOutputStream() else null
            while (true) {
                val count = archive.read(buffer)
                if (count < 0) break
                total += count
                if (total > MAX_SCANNED_BYTES) return null
                if (output != null) {
                    if (output.size() + count > MAX_VERSION_BYTES) return null
                    output.write(buffer, 0, count)
                }
            }
            if (output != null) {
                val matches = versionLine.findAll(output.toString(Charsets.UTF_8.name())).toList()
                if (matches.size != 1) return null
                return matches.single().groupValues[2].takeIf { components(it) != null }
            }
            archive.closeEntry()
        }
        return null
    }
}
