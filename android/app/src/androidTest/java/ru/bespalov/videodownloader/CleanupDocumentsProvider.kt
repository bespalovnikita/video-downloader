package ru.bespalov.videodownloader

import android.content.ContentProvider
import android.content.ContentValues
import android.content.Intent
import android.content.pm.PackageManager
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.os.Binder
import android.os.Bundle
import android.os.ParcelFileDescriptor
import android.provider.DocumentsContract
import java.io.File
import java.io.FileNotFoundException
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger

/** A real Binder provider whose documents and control API exist only in the test APK. */
class CleanupDocumentsProvider : ContentProvider() {
    private class Document(val file: File) {
        @Volatile var queryMode: String = NORMAL
        @Volatile var deleteFails = false
        val deletes = AtomicInteger()
    }

    private val documents = ConcurrentHashMap<String, Document>()

    override fun onCreate() = true

    private fun id(uri: Uri): String = requireNotNull(uri.lastPathSegment).also { UUID.fromString(it) }

    private fun document(uri: Uri): Document = documents[id(uri)] ?: throw FileNotFoundException("Unknown test document")

    private fun requireGrant(uri: Uri, flags: Int) {
        if (requireNotNull(context).checkCallingUriPermission(uri, flags) != PackageManager.PERMISSION_GRANTED) {
            throw SecurityException("The test document's URI grant has been revoked")
        }
    }

    override fun query(uri: Uri, projection: Array<out String>?, selection: String?, selectionArgs: Array<out String>?, sortOrder: String?): Cursor? {
        requireGrant(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION)
        val document = document(uri)
        when (document.queryMode) {
            NULL_CURSOR -> return null
            PROVIDER_ERROR -> throw IllegalStateException("The test storage provider is temporarily unavailable")
            STORAGE_MISSING -> throw FileNotFoundException("The test storage volume is temporarily disconnected")
        }
        val columns = projection ?: arrayOf(DocumentsContract.Document.COLUMN_DOCUMENT_ID)
        return MatrixCursor(columns).apply {
            if (document.file.exists()) addRow(columns.map { column -> when (column) {
                DocumentsContract.Document.COLUMN_DOCUMENT_ID, "_id" -> id(uri)
                DocumentsContract.Document.COLUMN_DISPLAY_NAME -> document.file.name
                DocumentsContract.Document.COLUMN_MIME_TYPE -> "application/octet-stream"
                DocumentsContract.Document.COLUMN_SIZE -> document.file.length()
                DocumentsContract.Document.COLUMN_FLAGS -> DocumentsContract.Document.FLAG_SUPPORTS_DELETE
                else -> null
            } })
        }
    }

    override fun getType(uri: Uri) = "application/octet-stream"

    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor {
        requireGrant(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION)
        require(mode == "r") { "Test documents are read-only outside their control API" }
        return ParcelFileDescriptor.open(document(uri).file, ParcelFileDescriptor.MODE_READ_ONLY)
    }

    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?): Int {
        requireGrant(uri, Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
        val document = document(uri)
        document.deletes.incrementAndGet()
        if (document.deleteFails) throw IllegalStateException("The test provider could not delete the document")
        return if (document.file.delete()) 1 else 0
    }

    override fun insert(uri: Uri, values: ContentValues?): Uri? = throw UnsupportedOperationException()
    override fun update(uri: Uri, values: ContentValues?, selection: String?, selectionArgs: Array<out String>?): Int =
        throw UnsupportedOperationException()

    @Suppress("DEPRECATION")
    override fun call(method: String, arg: String?, extras: Bundle?): Bundle? {
        if (method == "android:deleteDocument") {
            val arguments = requireNotNull(extras) { "Missing document arguments" }
            val uri = arguments.keySet().map { arguments.get(it) }.filterIsInstance<Uri>().singleOrNull()
                ?: throw IllegalArgumentException("Missing document URI")
            if (delete(uri, null, null) != 1) throw FileNotFoundException("The test document is already absent")
            return Bundle.EMPTY
        }

        // Control requests deliberately do not need a document grant: the tests
        // must still inspect, revoke/regrant and remove their own inaccessible fixture.
        val id = requireNotNull(arg).also { UUID.fromString(it) }
        val uri = DocumentsContract.buildDocumentUri(AUTHORITY, id)
        val identity = Binder.clearCallingIdentity()
        try {
            when (method) {
                CREATE -> {
                    require(!documents.containsKey(id)) { "A test document already exists" }
                    val directory = File(requireNotNull(context).cacheDir, "cleanup-provider").apply { mkdirs() }
                    val file = File(directory, id)
                    require(file.createNewFile()) { "A test fixture already exists" }
                    file.writeBytes(requireNotNull(extras?.getByteArray(BYTES)))
                    documents[id] = Document(file)
                }
                GRANT -> requireNotNull(context).grantUriPermission(requireNotNull(extras?.getString(PACKAGE)), uri, GRANTS)
                REVOKE -> requireNotNull(context).revokeUriPermission(uri, GRANTS)
                QUERY_MODE -> document(uri).queryMode = requireNotNull(extras?.getString(MODE))
                DELETE_FAILS -> document(uri).deleteFails = requireNotNull(extras).getBoolean(FAIL)
                FORGET -> {
                    requireNotNull(context).revokeUriPermission(uri, GRANTS)
                    documents.remove(id)?.file?.delete()
                }
                STATUS -> return Bundle().apply {
                    putBoolean(EXISTS, documents[id]?.file?.exists() == true)
                    putInt(DELETES, documents[id]?.deletes?.get() ?: 0)
                }
                else -> throw UnsupportedOperationException(method)
            }
            return Bundle.EMPTY
        } finally { Binder.restoreCallingIdentity(identity) }
    }

    companion object {
        const val AUTHORITY = "ru.bespalov.videodownloader.test.cleanup"
        val CONTROL: Uri = Uri.parse("content://$AUTHORITY/control")
        const val CREATE = "create"
        const val GRANT = "grant"
        const val REVOKE = "revoke"
        const val QUERY_MODE = "query-mode"
        const val DELETE_FAILS = "delete-fails"
        const val FORGET = "forget"
        const val STATUS = "status"
        const val BYTES = "bytes"
        const val PACKAGE = "package"
        const val MODE = "mode"
        const val FAIL = "fail"
        const val EXISTS = "exists"
        const val DELETES = "deletes"
        const val NORMAL = "normal"
        const val NULL_CURSOR = "null"
        const val PROVIDER_ERROR = "error"
        const val STORAGE_MISSING = "storage-missing"
        private const val GRANTS = Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
    }
}
