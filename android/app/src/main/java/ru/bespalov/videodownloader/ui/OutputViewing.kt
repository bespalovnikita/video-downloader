package ru.bespalov.videodownloader.ui

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.webkit.MimeTypeMap
import androidx.core.content.FileProvider
import java.io.File
import java.util.Locale

/** FileProvider controls the sharing boundary; a stored file URI alone grants no access. */
internal object OutputViewing {
    fun intent(context: Context, value: String, localFile: Boolean): Intent {
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
        val mime = context.contentResolver.getType(shared)
            ?: MimeTypeMap.getSingleton().getMimeTypeFromExtension(extension)
            ?: "application/octet-stream"
        return Intent(Intent.ACTION_VIEW).setDataAndType(shared, mime)
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
    }
}
