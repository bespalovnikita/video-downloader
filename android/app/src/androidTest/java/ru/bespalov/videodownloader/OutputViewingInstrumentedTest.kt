package ru.bespalov.videodownloader

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Environment
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import ru.bespalov.videodownloader.ui.OutputViewing
import java.io.File
import java.util.UUID

@RunWith(AndroidJUnit4::class)
class OutputViewingInstrumentedTest {
    private val context: Context get() = ApplicationProvider.getApplicationContext()

    @Test fun appExternalMovieIsSharedAsReadableContentWithMediaMime() {
        val root = requireNotNull(context.getExternalFilesDir(Environment.DIRECTORY_MOVIES))
        val file = File(root, "viewing-${UUID.randomUUID()}.mp4")
        try {
            check(root.mkdirs() || root.isDirectory)
            file.writeText("test-owned media-sharing sentinel")
            val intent = OutputViewing.intent(context, Uri.fromFile(file).toString(), localFile = true)
            val uri = requireNotNull(intent.data)
            assertEquals(Intent.ACTION_VIEW, intent.action)
            assertEquals("content", uri.scheme)
            assertEquals("${context.packageName}.files", uri.authority)
            assertEquals("video/mp4", intent.type)
            assertTrue(intent.flags and Intent.FLAG_GRANT_READ_URI_PERMISSION != 0)
            assertEquals("test-owned media-sharing sentinel", context.contentResolver.openInputStream(uri)!!.bufferedReader().use { it.readText() })
        } finally { file.delete() }
    }

    @Test fun privateCookieFileCannotBeExposedByAStoredFileUri() {
        val file = File(context.noBackupFilesDir, "viewing-private-${UUID.randomUUID()}.txt")
        try {
            file.writeText("test-owned private sentinel")
            assertThrows(IllegalArgumentException::class.java) {
                OutputViewing.intent(context, Uri.fromFile(file).toString(), localFile = true)
            }
            assertThrows(IllegalArgumentException::class.java) {
                OutputViewing.intent(context, Uri.fromFile(file).toString(), localFile = false)
            }
            assertEquals("test-owned private sentinel", file.readText())
        } finally { file.delete() }
    }
}
