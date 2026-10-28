package ru.bespalov.videodownloader.engine

import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.nio.file.AtomicMoveNotSupportedException
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.security.MessageDigest
import java.time.LocalDate
import java.util.zip.ZipFile

/** Inspects the actual zipimport executable, independently of the wrapper's preferences. */
internal object RuntimeArchive {
    private const val VERSION_ENTRY = "yt_dlp/version.py"
    private const val MAX_VERSION_BYTES = 64 * 1024
    private const val MAX_ASSET_BYTES = 32 * 1024 * 1024
    private val versionLine = Regex("(?m)^__version__[ \\t]*=[ \\t]*(['\"])([0-9.]+)\\1[ \\t]*\\r?$")
    private val versionShape = Regex("^[0-9]{4}\\.[0-9]{2}\\.[0-9]{2}(?:\\.[0-9]{1,12}){0,4}$")

    fun readVersion(file: File): String? = try {
        if (!file.isFile || Files.isSymbolicLink(file.toPath())) null
        else ZipFile(file).use { archive ->
            val entry = archive.getEntry(VERSION_ENTRY) ?: return@use null
            if (entry.isDirectory || entry.size > MAX_VERSION_BYTES) return@use null
            val bytes = archive.getInputStream(entry).use { readBounded(it, MAX_VERSION_BYTES) }
            val matches = versionLine.findAll(bytes.toString(Charsets.UTF_8)).toList()
            if (matches.size != 1) return@use null
            matches.single().groupValues[2].takeIf { components(it) != null }
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

    private fun readBounded(input: InputStream, limit: Int): ByteArray {
        val output = ByteArrayOutputStream()
        val buffer = ByteArray(8192)
        while (true) {
            val count = input.read(buffer)
            if (count < 0) break
            require(output.size() + count <= limit) { "yt-dlp version metadata is too large" }
            output.write(buffer, 0, count)
        }
        return output.toByteArray()
    }
}
