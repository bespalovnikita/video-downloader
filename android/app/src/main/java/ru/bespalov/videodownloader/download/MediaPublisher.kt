package ru.bespalov.videodownloader.download

import android.content.ContentResolver
import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.webkit.MimeTypeMap
import androidx.documentfile.provider.DocumentFile
import androidx.annotation.RequiresApi
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import ru.bespalov.video.core.Settings
import java.io.File
import java.io.IOException

data class PublishedBatch(val primaryUri: String, val uris: List<String>)

class PublicationFailure(message: String, cause: Throwable?, val retainedUris: List<String>) : IOException(message, cause)

/** Publishes only verified owned staging files; existing destinations are never opened for writing. */
class MediaPublisher(private val context: Context) {
    private val publication = Mutex()
    private val resolver get() = context.contentResolver

    suspend fun publish(
        stage: File,
        files: List<File>,
        primary: File,
        settings: Settings,
        subscriptionName: String,
        archiveDate: String,
        cancelled: () -> Boolean,
        onOwned: suspend (List<String>) -> Unit,
    ): PublishedBatch = withContext(Dispatchers.IO) {
        publication.withLock {
            require(files.isNotEmpty() && files.contains(primary)) { "No verified media to publish" }
            files.forEach {
                StorageRules.child(stage, it)
                require(it.isFile && it.length() > 0) { "A staged file is missing or empty" }
            }
            val relative = files.map { stage.toPath().relativize(it.toPath()).toString().replace('\\', '/') }
            val primaryName = relative[files.indexOf(primary)]
            val folders = buildList {
                if (subscriptionName.isNotBlank()) addAll(StorageRules.components(subscriptionName))
                if (settings.archive && archiveDate.isNotBlank()) addAll(StorageRules.components(archiveDate))
            }
            for (copy in 1..10_000) {
                checkCancelled(cancelled)
                val names = StorageRules.batchNames(relative, primaryName, copy)
                val owned = mutableListOf<String>()
                val pending = mutableListOf<Uri>()
                try {
                    val targets = names.map { destination(folders, it, settings.outputTreeUri) }
                    if (targets.any { it.exists() }) continue
                    files.forEachIndexed { index, input ->
                        checkCancelled(cancelled)
                        val target = targets[index]
                        val uri = target.create()
                        owned += uri.toString()
                        if (target.pending) pending += uri
                        onOwned(owned.toList())
                        if (target.actualName(uri) != names[index].substringAfterLast('/')) throw NameRace()
                        resolver.openOutputStream(uri, "w")?.use { output ->
                            input.inputStream().use { source ->
                                val buffer = ByteArray(64 * 1024)
                                while (true) {
                                    checkCancelled(cancelled)
                                    val count = source.read(buffer)
                                    if (count < 0) break
                                    output.write(buffer, 0, count)
                                }
                            }
                            output.flush()
                        } ?: throw IOException("The destination cannot be opened")
                    }
                    checkCancelled(cancelled)
                    pending.forEach { uri ->
                        checkCancelled(cancelled)
                        val values = ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }
                        if (resolver.update(uri, values, null, null) != 1) throw IOException("Could not finish publishing media")
                    }
                    checkCancelled(cancelled)
                    return@withLock PublishedBatch(owned[files.indexOf(primary)], owned.toList())
                } catch (error: Throwable) {
                    val retained = deleteOwned(owned)
                    onOwned(retained)
                    if (error is NameRace && retained.isEmpty()) continue
                    if (retained.isNotEmpty()) throw PublicationFailure("Some published files could not be rolled back", error, retained)
                    throw error
                }
            }
            throw IOException("No free output name is available")
        }
    }

    /** Return only entries that could not be deleted; callers persist that cleanup manifest. */
    suspend fun deleteOwned(uris: List<String>): List<String> = withContext(Dispatchers.IO) {
        uris.distinct().filter { value ->
            try {
                val uri = Uri.parse(value)
                when (uri.scheme) {
                    "content" -> {
                        if (uri.authority == MediaStore.AUTHORITY) {
                            resolver.delete(uri, null, null)
                            exists(uri)
                        } else {
                            val document = DocumentFile.fromSingleUri(context, uri)
                            document != null && document.exists() && !document.delete()
                        }
                    }
                    "file" -> {
                        val file = File(requireNotNull(uri.path))
                        val root = requireNotNull(context.getExternalFilesDir(Environment.DIRECTORY_MOVIES))
                        StorageRules.child(root, file)
                        file.exists() && !file.delete()
                    }
                    else -> true
                }
            } catch (_: Exception) { true }
        }
    }

    private fun exists(uri: Uri): Boolean = resolver.query(uri, arrayOf("_id"), null, null, null)?.use { it.moveToFirst() } ?: false

    private fun checkCancelled(cancelled: () -> Boolean) {
        if (cancelled()) throw CancellationException("Download cancelled before publication completed")
    }

    private interface Destination {
        val pending: Boolean get() = false
        fun exists(): Boolean
        fun create(): Uri
        fun actualName(uri: Uri): String
    }

    private fun destination(folders: List<String>, relative: String, tree: String): Destination {
        val parts = folders + StorageRules.components(relative)
        val name = parts.last()
        val parents = parts.dropLast(1)
        return when {
            tree.isNotBlank() -> documentDestination(tree, parents, name)
            Build.VERSION.SDK_INT >= 29 -> mediaStoreDestination(parents, name)
            else -> privateExternalDestination(parents, name)
        }
    }

    private fun documentDestination(tree: String, parents: List<String>, name: String): Destination {
        var directory = DocumentFile.fromTreeUri(context, Uri.parse(tree)) ?: throw IOException("Selected folder is unavailable")
        require(directory.isDirectory && directory.canWrite()) { "The selected folder is not writable" }
        parents.forEach { part ->
            directory = directory.findFile(part)?.also { require(it.isDirectory) { "A destination folder is occupied by a file" } }
                ?: directory.createDirectory(part) ?: throw IOException("Cannot create a destination folder")
        }
        val parent = directory
        return object : Destination {
            override fun exists() = parent.findFile(name) != null
            override fun create(): Uri {
                val before = parent.listFiles().map { it.uri }.toSet()
                val document = parent.createFile(mime(name), name) ?: throw IOException("Cannot create a destination file")
                if (document.uri in before) throw IOException("The provider returned an existing file; overwrite was blocked")
                return document.uri
            }
            override fun actualName(uri: Uri) = DocumentFile.fromSingleUri(context, uri)?.name.orEmpty()
        }
    }

    @RequiresApi(29)
    private fun mediaStoreDestination(parents: List<String>, name: String): Destination {
        val collection = MediaStore.Downloads.EXTERNAL_CONTENT_URI
        val relative = (listOf(Environment.DIRECTORY_DOWNLOADS, "VideoDownloader") + parents).joinToString("/", postfix = "/")
        return object : Destination {
            override val pending = true
            override fun exists(): Boolean = resolver.query(collection, arrayOf(MediaStore.MediaColumns._ID),
                "${MediaStore.MediaColumns.RELATIVE_PATH} = ? AND ${MediaStore.MediaColumns.DISPLAY_NAME} = ?",
                arrayOf(relative, name), null)?.use { it.moveToFirst() } ?: false
            override fun create(): Uri = resolver.insert(collection, ContentValues().apply {
                put(MediaStore.MediaColumns.DISPLAY_NAME, name)
                put(MediaStore.MediaColumns.MIME_TYPE, mime(name))
                put(MediaStore.MediaColumns.RELATIVE_PATH, relative)
                put(MediaStore.MediaColumns.IS_PENDING, 1)
            }) ?: throw IOException("Cannot create a MediaStore entry")
            override fun actualName(uri: Uri): String = resolver.query(uri, arrayOf(MediaStore.MediaColumns.DISPLAY_NAME),
                null, null, null)?.use { if (it.moveToFirst()) it.getString(0) else "" }.orEmpty()
        }
    }

    private fun privateExternalDestination(parents: List<String>, name: String): Destination {
        val root = context.getExternalFilesDir(Environment.DIRECTORY_MOVIES) ?: throw IOException("External storage is unavailable")
        val parent = parents.fold(root) { directory, child -> File(directory, child) }
        if (parent != root) StorageRules.child(root, parent)
        if (!parent.isDirectory && !parent.mkdirs()) throw IOException("Cannot create a destination folder")
        val file = File(parent, name)
        StorageRules.child(root, file)
        return object : Destination {
            override fun exists() = file.exists()
            override fun create(): Uri {
                if (!file.createNewFile()) throw NameRace()
                return Uri.fromFile(file)
            }
            override fun actualName(uri: Uri) = file.name
        }
    }

    private fun mime(name: String): String = MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substringAfterLast('.', "").lowercase())
        ?: "application/octet-stream"

    private class NameRace : IOException("A destination name changed during publication")
}
