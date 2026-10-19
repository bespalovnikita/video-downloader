package com.bespalovnikita.videodownloader.downloader

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Environment
import android.provider.MediaStore
import android.webkit.MimeTypeMap
import java.io.File

data class PublishedMedia(
    val primaryUri: String,
    val primaryFileName: String,
    val primaryMimeType: String,
    val allUris: List<String>
)

class MediaPublisher(private val context: Context) {
    fun publish(stagingDir: File, finalPath: String): PublishedMedia {
        val files = stagingDir.walkTopDown()
            .filter { it.isFile }
            .filterNot { it.name.endsWith(".part") || it.name.endsWith(".ytdl") }
            .toList()

        require(files.isNotEmpty()) { "yt-dlp finished but staging directory is empty" }

        val finalFile = finalPath.takeIf { it.isNotBlank() }?.let(::File)
        var primary: Triple<String, String, String>? = null
        val all = mutableListOf<String>()

        files.forEach { source ->
            val mime = mimeType(source)
            val values = ContentValues().apply {
                put(MediaStore.Downloads.DISPLAY_NAME, source.name)
                put(MediaStore.Downloads.MIME_TYPE, mime)
                put(
                    MediaStore.Downloads.RELATIVE_PATH,
                    Environment.DIRECTORY_DOWNLOADS + "/VideoDownloader"
                )
                put(MediaStore.Downloads.IS_PENDING, 1)
            }

            val uri = context.contentResolver.insert(
                MediaStore.Downloads.EXTERNAL_CONTENT_URI,
                values
            ) ?: error("MediaStore refused to create " + source.name)

            try {
                context.contentResolver.openOutputStream(uri, "w")!!.use { output ->
                    source.inputStream().use { input -> input.copyTo(output) }
                }
                context.contentResolver.update(
                    uri,
                    ContentValues().apply { put(MediaStore.Downloads.IS_PENDING, 0) },
                    null,
                    null
                )
            } catch (t: Throwable) {
                context.contentResolver.delete(uri, null, null)
                throw t
            }

            val uriText = uri.toString()
            all += uriText
            if (finalFile != null && source.absolutePath == finalFile.absolutePath) {
                primary = Triple(uriText, source.name, mime)
            }
        }

        val selected = primary ?: run {
            val source = files.maxByOrNull { it.length() }!!
            val index = files.indexOf(source)
            Triple(all[index], source.name, mimeType(source))
        }

        stagingDir.deleteRecursively()

        return PublishedMedia(
            primaryUri = selected.first,
            primaryFileName = selected.second,
            primaryMimeType = selected.third,
            allUris = all
        )
    }

    private fun mimeType(file: File): String {
        val ext = file.extension.lowercase()
        return MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext)
            ?: when (ext) {
                "mkv" -> "video/x-matroska"
                "webm" -> "video/webm"
                "mp3" -> "audio/mpeg"
                "m4a" -> "audio/mp4"
                "opus" -> "audio/ogg"
                "vtt" -> "text/vtt"
                "srt" -> "application/x-subrip"
                else -> "application/octet-stream"
            }
    }
}
