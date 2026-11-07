package ru.bespalov.videodownloader.ui

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.CancellationSignal
import android.provider.DocumentsContract
import android.provider.OpenableColumns
import android.webkit.MimeTypeMap
import androidx.core.content.FileProvider
import java.io.File
import java.util.Locale
import java.util.concurrent.Semaphore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withTimeoutOrNull
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.resume

/** FileProvider controls the sharing boundary; a stored file URI alone grants no access. */
internal object OutputViewing {
    // A provider may ignore cancellation. Retain each slot until the actual Binder call returns.
    private val metadataSlots = Semaphore(2)
    data class OutputFile(val uri: String, val name: String, val mime: String) {
        val media: Boolean get() = mime.startsWith("video/") || mime.startsWith("audio/")
    }

    /** Never infer a file type from an opaque content URI or add a URI to the owned manifest. */
    suspend fun files(context: Context, uris: List<String>): List<OutputFile> {
        val fallback = uris.distinct().mapIndexed { index, uri -> OutputFile(uri, "Файл ${index + 1}", "application/octet-stream") }
        return withTimeoutOrNull(5_000) {
            fallback.map { file -> metadata(context, file) }
        } ?: fallback
    }

    suspend fun primary(context: Context, primaryUri: String, uris: List<String>): String? {
        if (primaryUri.isNotBlank() && primaryUri in uris) return primaryUri
        if (uris.size == 1) return uris.single()
        // Old snapshots have no primary marker. Ambiguous results need an explicit choice.
        return files(context, uris).filter { it.media }.singleOrNull()?.uri
    }

    suspend fun publishedIntent(context: Context, value: String): Intent {
        val file = files(context, listOf(value)).single()
        return intent(context, value, localFile = true, mimeType = file.mime)
    }

    private suspend fun metadata(context: Context, fallback: OutputFile): OutputFile = suspendCancellableCoroutine { continuation ->
        if (!metadataSlots.tryAcquire()) {
            continuation.resume(fallback)
            return@suspendCancellableCoroutine
        }
        val app = context.applicationContext
        val cancellation = CancellationSignal()
        continuation.invokeOnCancellation { cancellation.cancel() }
        Dispatchers.IO.dispatch(EmptyCoroutineContext, Runnable {
            try {
                if (!continuation.isActive) return@Runnable
                val result = try {
                    val original = Uri.parse(fallback.uri)
                    val uri = if (original.scheme == "file") {
                        require(original.authority.isNullOrEmpty())
                        FileProvider.getUriForFile(app, "${app.packageName}.files", File(requireNotNull(original.path)))
                    } else {
                        require(original.scheme == "content")
                        original
                    }
                    var name = ""
                    var mime = ""
                    app.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, DocumentsContract.Document.COLUMN_MIME_TYPE),
                        null, null, null, cancellation)?.use { cursor ->
                        if (cursor.moveToFirst()) {
                            cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME).takeIf { it >= 0 }?.let { name = cursor.getString(it).orEmpty() }
                            cursor.getColumnIndex(DocumentsContract.Document.COLUMN_MIME_TYPE).takeIf { it >= 0 }?.let { mime = cursor.getString(it).orEmpty() }
                        }
                    }
                    if (mime.isBlank()) mime = app.contentResolver.getType(uri).orEmpty()
                    if (mime.isBlank() || mime == "application/octet-stream") {
                        mime = MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substringAfterLast('.', "").lowercase(Locale.ROOT)).orEmpty()
                    }
                    fallback.copy(name = name.ifBlank { fallback.name }, mime = mime.ifBlank { fallback.mime })
                } catch (_: Exception) { fallback }
                continuation.resume(result)
            } finally { metadataSlots.release() }
        })
    }

    fun intent(context: Context, value: String, localFile: Boolean, mimeType: String? = null): Intent {
        val original = Uri.parse(value)
        val scheme = original.scheme?.lowercase(Locale.ROOT)
        if (!localFile) {
            require(scheme in setOf("http", "https")) { "Expected an HTTP(S) link" }
            return Intent(Intent.ACTION_VIEW, original)
        }
        val shared = when (scheme) {
            "file" -> {
                require(original.authority.isNullOrEmpty()) { "A local file URI cannot have a host" }
                val file = File(requireNotNull(original.path) { "File path is missing" })
                FileProvider.getUriForFile(context, "${context.packageName}.files", file)
            }
            "content" -> original
            else -> throw IllegalArgumentException("Expected a published file or content URI")
        }
        val extension = original.lastPathSegment.orEmpty().substringAfterLast('.', "").lowercase(Locale.ROOT)
        val mime = mimeType ?: context.contentResolver.getType(shared)
            ?: MimeTypeMap.getSingleton().getMimeTypeFromExtension(extension)
            ?: "application/octet-stream"
        return Intent(Intent.ACTION_VIEW).setDataAndType(shared, mime)
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
    }
}
