package ru.bespalov.videodownloader.engine

import org.junit.Assert.*
import org.junit.Assume.assumeNoException
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.nio.file.Files
import java.security.MessageDigest
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

class RuntimeArchiveTest {
    private val bundled = "2026.10.26"

    @Test fun migratesAnExistingOldRuntimeAndReadsTheActualZipVersion() = inDirectory { directory ->
        val target = File(directory, "yt-dlp").apply { writeBytes(archive("2025.11.12")) }
        val bytes = archive(bundled, shebang = true)
        assertEquals(bundled, RuntimeArchive.installIfOlder(target, bundled, sha(bytes)) { ByteArrayInputStream(bytes) })
        assertArrayEquals(bytes, target.readBytes())
        assertEquals(bundled, RuntimeArchive.readVersion(target))
        assertNoScratch(directory)
    }

    @Test fun doesNotDowngradeAManuallyUpdatedNightlyOrReadTheAsset() = inDirectory { directory ->
        val bytes = archive("2026.10.28.232804")
        val target = File(directory, "yt-dlp").apply { writeBytes(bytes) }
        val version = RuntimeArchive.installIfOlder(target, bundled, "0".repeat(64)) {
            error("A newer installed runtime must not read the older bundled asset")
        }
        assertEquals("2026.10.28.232804", version)
        assertArrayEquals(bytes, target.readBytes())
        assertNoScratch(directory)
    }

    @Test fun equalVersionPreservesTheInstalledFileWithoutOpeningAsset() = inDirectory { directory ->
        val bytes = archive(bundled)
        val target = File(directory, "yt-dlp").apply { writeBytes(bytes) }
        assertEquals(bundled, RuntimeArchive.installIfOlder(target, bundled, "0".repeat(64)) {
            error("The equal installed runtime must remain untouched")
        })
        assertArrayEquals(bytes, target.readBytes())
        assertNoScratch(directory)
    }

    @Test fun checksumFailureKeepsTheOldExecutableAndCleansScratch() = inDirectory { directory ->
        val old = archive("2025.11.12")
        val target = File(directory, "yt-dlp").apply { writeBytes(old) }
        val bytes = archive(bundled)
        assertThrows(IllegalStateException::class.java) {
            RuntimeArchive.installIfOlder(target, bundled, "0".repeat(64)) { ByteArrayInputStream(bytes) }
        }
        assertArrayEquals(old, target.readBytes())
        assertNoScratch(directory)
    }

    @Test fun matchingChecksumCannotMakeAnInvalidZipReplaceTheOldRuntime() = inDirectory { directory ->
        val old = archive("2025.11.12")
        val target = File(directory, "yt-dlp").apply { writeBytes(old) }
        val invalid = "this is not a zipimport executable".toByteArray()
        assertThrows(IllegalStateException::class.java) {
            RuntimeArchive.installIfOlder(target, bundled, sha(invalid)) { ByteArrayInputStream(invalid) }
        }
        assertArrayEquals(old, target.readBytes())
        assertNoScratch(directory)
    }

    @Test fun assetWithUnexpectedVersionCannotReplaceTheOldRuntime() = inDirectory { directory ->
        val old = archive("2025.11.12")
        val target = File(directory, "yt-dlp").apply { writeBytes(old) }
        val different = archive("2026.09.01")
        assertThrows(IllegalStateException::class.java) {
            RuntimeArchive.installIfOlder(target, bundled, sha(different)) { ByteArrayInputStream(different) }
        }
        assertArrayEquals(old, target.readBytes())
        assertNoScratch(directory)
    }

    @Test fun missingAndCorruptRuntimesAreReplacedByTheVerifiedBundle() = inDirectory { directory ->
        val bytes = archive(bundled)
        val target = File(directory, "new-parent/yt-dlp")
        assertEquals(bundled, RuntimeArchive.installIfOlder(target, bundled, sha(bytes)) { ByteArrayInputStream(bytes) })
        target.writeText("damaged prior installation")
        assertNull(RuntimeArchive.readVersion(target))
        assertEquals(bundled, RuntimeArchive.installIfOlder(target, bundled, sha(bytes)) { ByteArrayInputStream(bytes) })
        assertArrayEquals(bytes, target.readBytes())
        assertNoScratch(target.parentFile!!)
    }

    @Test fun failedAssetReadDoesNotReplaceThePreviousRuntimeOrLeaveScratch() = inDirectory { directory ->
        val old = archive("2025.11.12")
        val target = File(directory, "yt-dlp").apply { writeBytes(old) }
        assertThrows(java.io.IOException::class.java) {
            RuntimeArchive.installIfOlder(target, bundled, "0".repeat(64)) {
                throw java.io.IOException("Read failed")
            }
        }
        assertArrayEquals(old, target.readBytes())
        assertNoScratch(directory)
    }

    @Test fun versionMetadataIsBoundedAndMustContainOneRealCalendarVersion() = inDirectory { directory ->
        val target = File(directory, "yt-dlp")
        target.writeBytes(archive("2026.02.30"))
        assertNull(RuntimeArchive.readVersion(target))
        target.writeBytes(archive("2026.10.26", padding = " ".repeat(64 * 1024)))
        assertNull(RuntimeArchive.readVersion(target))
        target.writeBytes(archive("2026.10.26", padding = "\n__version__ = '2026.10.27'\n"))
        assertNull(RuntimeArchive.readVersion(target))
    }

    @Test fun executablePrefixIsBoundedAndArbitraryLeadingBytesAreRejected() = inDirectory { directory ->
        val target = File(directory, "yt-dlp")
        val zip = archive(bundled)
        target.writeBytes("#!/usr/bin/env python3\n".toByteArray() + zip)
        assertEquals(bundled, RuntimeArchive.readVersion(target))
        target.writeBytes("not a launcher\n".toByteArray() + zip)
        assertNull(RuntimeArchive.readVersion(target))
        target.writeBytes(("#!" + " ".repeat(4094)).toByteArray() + zip)
        assertEquals("The maximum supported launcher is 4 KiB", bundled, RuntimeArchive.readVersion(target))
        target.writeBytes(("#!" + " ".repeat(4095)).toByteArray() + zip)
        assertNull("A longer executable stub must not be scanned", RuntimeArchive.readVersion(target))
    }

    @Test fun findsVersionAfterOrdinaryZipEntriesAndRejectsExcessiveInflation() = inDirectory { directory ->
        val target = File(directory, "yt-dlp")
        fun withLeadingData(size: Int): ByteArray = ByteArrayOutputStream().also { output ->
            ZipOutputStream(output).use { zip ->
                zip.putNextEntry(ZipEntry("yt_dlp/another.py"))
                val buffer = ByteArray(8192)
                var remaining = size
                while (remaining > 0) {
                    val count = minOf(remaining, buffer.size)
                    zip.write(buffer, 0, count)
                    remaining -= count
                }
                zip.closeEntry()
                zip.putNextEntry(ZipEntry("yt_dlp/version.py"))
                zip.write("__version__ = '$bundled'\n".toByteArray())
                zip.closeEntry()
            }
        }.toByteArray()
        target.writeBytes(withLeadingData(32 * 1024))
        assertEquals(bundled, RuntimeArchive.readVersion(target))
        target.writeBytes(withLeadingData(64 * 1024 * 1024 + 1))
        assertNull("The decompressed scan must stay below 64 MiB", RuntimeArchive.readVersion(target))
    }

    @Test fun comparesCalendarAndNightlyComponentsNumerically() {
        assertTrue(RuntimeArchive.compareVersions("2026.10.26", "2025.11.12") > 0)
        assertTrue(RuntimeArchive.compareVersions("2026.10.26.232804", bundled) > 0)
        assertTrue(RuntimeArchive.compareVersions("2026.10.26.10", "2026.10.26.9") > 0)
        assertTrue(RuntimeArchive.compareVersions("2026.09.30.235959", "2026.10.01") < 0)
        assertEquals(0, RuntimeArchive.compareVersions(bundled, bundled))
        assertThrows(IllegalArgumentException::class.java) { RuntimeArchive.compareVersions("garbage", bundled) }
    }

    @Test fun symbolicLinksCannotRedirectExecutableInstallation() = inDirectory { directory ->
        val realParent = File(directory, "real").apply { mkdirs() }
        val old = archive("2025.11.12")
        val actual = File(realParent, "yt-dlp").apply { writeBytes(old) }
        val link = File(directory, "linked-executable")
        val linkedParent = File(directory, "linked-directory")
        try {
            Files.createSymbolicLink(link.toPath(), actual.toPath())
            Files.createSymbolicLink(linkedParent.toPath(), realParent.toPath())
        } catch (error: UnsupportedOperationException) {
            assumeNoException("This filesystem does not support symbolic links", error)
        } catch (error: java.io.IOException) {
            assumeNoException("This machine does not permit creating symbolic links", error)
        }
        for (target in listOf(link, File(linkedParent, "yt-dlp"))) {
            assertThrows(IllegalArgumentException::class.java) {
                RuntimeArchive.installIfOlder(target, bundled, "0".repeat(64)) { error("Must not read the asset") }
            }
        }
        assertArrayEquals(old, actual.readBytes())
        assertNoScratch(realParent)
    }

    private fun archive(version: String, shebang: Boolean = false, padding: String = ""): ByteArray {
        val output = ByteArrayOutputStream()
        ZipOutputStream(output).use { zip ->
            zip.putNextEntry(ZipEntry("yt_dlp/version.py"))
            zip.write(("# generated fixture\n__version__ = '$version'\n" + padding).toByteArray())
            zip.closeEntry()
        }
        val bytes = output.toByteArray()
        return if (shebang) "#!/usr/bin/env python3\n".toByteArray() + bytes else bytes
    }

    private fun sha(bytes: ByteArray) = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
    private fun assertNoScratch(directory: File) = assertFalse(directory.listFiles().orEmpty().any { it.name.startsWith(".runtime-") })
    private fun inDirectory(block: (File) -> Unit) {
        val directory = Files.createTempDirectory("vd-runtime-archive").toFile()
        try { block(directory) } finally { directory.deleteRecursively() }
    }
}
