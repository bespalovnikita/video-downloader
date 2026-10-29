package ru.bespalov.videodownloader

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import ru.bespalov.videodownloader.engine.RuntimeArchive
import java.io.ByteArrayOutputStream
import java.io.File
import java.util.UUID
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

/** The official executable has a shebang before its ZIP, unlike a regular .zip fixture. */
@RunWith(AndroidJUnit4::class)
class RuntimeArchiveInstrumentedTest {
    private val context: Context get() = ApplicationProvider.getApplicationContext()

    @Test fun readsTheActualBundledUnixExecutableOnAndroid() = inDirectory { directory ->
        val bytes = context.resources.openRawResource(R.raw.ytdlp).use { it.readBytes() }
        assertTrue("Exercise the real Unix executable prefix", bytes.take(2).toByteArray().contentEquals("#!".toByteArray()))
        val archive = File(directory, "yt-dlp").apply { writeBytes(bytes) }
        assertEquals("Android must read the actual pinned executable, not only a synthetic ZIP",
            BuildConfig.YTDLP_VERSION, RuntimeArchive.readVersion(archive))
    }

    @Test fun migratesOldPrivateRuntimeUsingTheActualPackagedResource() = inDirectory { directory ->
        val target = File(directory, "yt-dlp").apply { writeBytes(oldArchive()) }
        assertEquals("2025.11.12", RuntimeArchive.readVersion(target))
        val installed = RuntimeArchive.installIfOlder(target, BuildConfig.YTDLP_VERSION, BuildConfig.YTDLP_SHA256) {
            context.resources.openRawResource(R.raw.ytdlp)
        }
        assertEquals(BuildConfig.YTDLP_VERSION, installed)
        assertEquals(BuildConfig.YTDLP_VERSION, RuntimeArchive.readVersion(target))
        assertArrayEquals(context.resources.openRawResource(R.raw.ytdlp).use { it.readBytes() }, target.readBytes())
        assertFalse("Migration must clean its temporary executable", directory.listFiles().orEmpty().any { it.name.startsWith(".runtime-") })
    }

    private fun oldArchive(): ByteArray = ByteArrayOutputStream().also { output ->
        ZipOutputStream(output).use { zip ->
            zip.putNextEntry(ZipEntry("yt_dlp/version.py"))
            zip.write("__version__ = '2025.11.12'\n".toByteArray())
            zip.closeEntry()
        }
    }.toByteArray()

    private fun inDirectory(block: (File) -> Unit) {
        val directory = File(context.cacheDir, "runtime-archive-test-${UUID.randomUUID()}")
        check(directory.mkdirs())
        try { block(directory) } finally { directory.deleteRecursively() }
    }
}
