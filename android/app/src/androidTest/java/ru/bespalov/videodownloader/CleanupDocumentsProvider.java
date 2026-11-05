package ru.bespalov.videodownloader;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.Binder;
import android.os.Bundle;
import android.os.ParcelFileDescriptor;
import android.provider.DocumentsContract;

import java.io.File;
import java.io.FileNotFoundException;
import java.io.FileOutputStream;
import java.io.IOException;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * Test-APK provider with no dependency on the target APK or Kotlin runtime.
 * Android starts providers in the test APK's own process, outside instrumentation.
 */
public final class CleanupDocumentsProvider extends ContentProvider {
    public static final String AUTHORITY = "ru.bespalov.videodownloader.test.cleanup";
    public static final Uri CONTROL = Uri.parse("content://" + AUTHORITY + "/control");
    public static final String CREATE = "create";
    public static final String GRANT = "grant";
    public static final String REVOKE = "revoke";
    public static final String QUERY_MODE = "query-mode";
    public static final String DELETE_FAILS = "delete-fails";
    public static final String FORGET = "forget";
    public static final String STATUS = "status";
    public static final String BYTES = "bytes";
    public static final String PACKAGE = "package";
    public static final String MODE = "mode";
    public static final String FAIL = "fail";
    public static final String EXISTS = "exists";
    public static final String DELETES = "deletes";
    public static final String NORMAL = "normal";
    public static final String NULL_CURSOR = "null";
    public static final String PROVIDER_ERROR = "error";
    public static final String STORAGE_MISSING = "storage-missing";
    private static final int GRANTS = Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION;

    private static final class Document {
        final File file;
        final AtomicInteger deletes = new AtomicInteger();
        volatile String queryMode = NORMAL;
        volatile boolean deleteFails;

        Document(File file) { this.file = file; }
    }

    private final ConcurrentHashMap<String, Document> documents = new ConcurrentHashMap<>();

    @Override public boolean onCreate() { return true; }

    private Context app() {
        Context value = getContext();
        if (value == null) throw new IllegalStateException("The test provider is not attached");
        return value;
    }

    private static String id(Uri uri) {
        String value = uri.getLastPathSegment();
        if (value == null) throw new IllegalArgumentException("Missing test document ID");
        UUID.fromString(value);
        return value;
    }

    private Document document(Uri uri) {
        Document value = documents.get(id(uri));
        if (value == null) throw new IllegalArgumentException("Unknown test document",
            new FileNotFoundException("Unknown test document"));
        return value;
    }

    private void requireGrant(Uri uri, int flags) {
        if (app().checkCallingUriPermission(uri, flags) != PackageManager.PERMISSION_GRANTED)
            throw new SecurityException("The test document's URI grant has been revoked");
    }

    @Override public Cursor query(Uri uri, String[] projection, String selection, String[] selectionArgs, String sortOrder) {
        requireGrant(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION);
        Document document = document(uri);
        if (NULL_CURSOR.equals(document.queryMode)) return null;
        if (PROVIDER_ERROR.equals(document.queryMode))
            throw new IllegalStateException("The test storage provider is temporarily unavailable");
        if (STORAGE_MISSING.equals(document.queryMode))
            throw new IllegalStateException("The test storage volume is temporarily disconnected",
                new FileNotFoundException("The test storage volume is temporarily disconnected"));
        String[] columns = projection == null ? new String[]{DocumentsContract.Document.COLUMN_DOCUMENT_ID} : projection;
        MatrixCursor cursor = new MatrixCursor(columns);
        if (document.file.exists()) {
            Object[] row = new Object[columns.length];
            for (int index = 0; index < columns.length; index++) {
                switch (columns[index]) {
                    case DocumentsContract.Document.COLUMN_DOCUMENT_ID:
                    case "_id": row[index] = id(uri); break;
                    case DocumentsContract.Document.COLUMN_DISPLAY_NAME: row[index] = document.file.getName(); break;
                    case DocumentsContract.Document.COLUMN_MIME_TYPE: row[index] = "application/octet-stream"; break;
                    case DocumentsContract.Document.COLUMN_SIZE: row[index] = document.file.length(); break;
                    case DocumentsContract.Document.COLUMN_FLAGS: row[index] = DocumentsContract.Document.FLAG_SUPPORTS_DELETE; break;
                    default: row[index] = null;
                }
            }
            cursor.addRow(row);
        }
        return cursor;
    }

    @Override public String getType(Uri uri) { return "application/octet-stream"; }

    @Override public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException {
        requireGrant(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION);
        if (!"r".equals(mode)) throw new IllegalArgumentException("Test documents are read-only outside their control API");
        return ParcelFileDescriptor.open(document(uri).file, ParcelFileDescriptor.MODE_READ_ONLY);
    }

    @Override public int delete(Uri uri, String selection, String[] selectionArgs) {
        requireGrant(uri, Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
        Document document = document(uri);
        document.deletes.incrementAndGet();
        if (document.deleteFails) throw new IllegalStateException("The test provider could not delete the document");
        return document.file.delete() ? 1 : 0;
    }

    @Override public Uri insert(Uri uri, ContentValues values) { throw new UnsupportedOperationException(); }
    @Override public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) {
        throw new UnsupportedOperationException();
    }

    @SuppressWarnings("deprecation")
    @Override public Bundle call(String method, String arg, Bundle extras) {
        if ("android:deleteDocument".equals(method)) {
            if (extras == null) throw new IllegalArgumentException("Missing document arguments");
            Uri uri = null;
            for (String key : extras.keySet()) {
                Object value = extras.get(key);
                if (value instanceof Uri) {
                    if (uri != null) throw new IllegalArgumentException("More than one document URI");
                    uri = (Uri) value;
                }
            }
            if (uri == null) throw new IllegalArgumentException("Missing document URI");
            if (delete(uri, null, null) != 1) throw new IllegalStateException("The test document is already absent");
            return Bundle.EMPTY;
        }

        // This control endpoint must remain usable after document access is revoked.
        if (arg == null) throw new IllegalArgumentException("Missing test document ID");
        UUID.fromString(arg);
        Uri uri = DocumentsContract.buildDocumentUri(AUTHORITY, arg);
        long identity = Binder.clearCallingIdentity();
        try {
            switch (method) {
                case CREATE: {
                    if (documents.containsKey(arg)) throw new IllegalArgumentException("A test document already exists");
                    File directory = new File(app().getCacheDir(), "cleanup-provider");
                    if (!directory.isDirectory() && !directory.mkdirs()) throw new IOException("Cannot create the test directory");
                    File file = new File(directory, arg);
                    if (!file.createNewFile()) throw new IOException("A test fixture already exists");
                    byte[] bytes = extras == null ? null : extras.getByteArray(BYTES);
                    if (bytes == null) throw new IllegalArgumentException("Missing test document bytes");
                    try (FileOutputStream output = new FileOutputStream(file)) { output.write(bytes); }
                    documents.put(arg, new Document(file));
                    break;
                }
                case GRANT: {
                    String target = extras == null ? null : extras.getString(PACKAGE);
                    if (target == null) throw new IllegalArgumentException("Missing target package");
                    app().grantUriPermission(target, uri, GRANTS);
                    break;
                }
                case REVOKE: app().revokeUriPermission(uri, GRANTS); break;
                case QUERY_MODE: {
                    String mode = extras == null ? null : extras.getString(MODE);
                    if (mode == null) throw new IllegalArgumentException("Missing query mode");
                    document(uri).queryMode = mode;
                    break;
                }
                case DELETE_FAILS:
                    if (extras == null) throw new IllegalArgumentException("Missing delete mode");
                    document(uri).deleteFails = extras.getBoolean(FAIL);
                    break;
                case FORGET: {
                    app().revokeUriPermission(uri, GRANTS);
                    Document removed = documents.remove(arg);
                    if (removed != null) removed.file.delete();
                    break;
                }
                case STATUS: {
                    Document value = documents.get(arg);
                    Bundle result = new Bundle();
                    result.putBoolean(EXISTS, value != null && value.file.exists());
                    result.putInt(DELETES, value == null ? 0 : value.deletes.get());
                    return result;
                }
                default: throw new UnsupportedOperationException(method);
            }
            return Bundle.EMPTY;
        } catch (IOException error) {
            throw new IllegalStateException("Test document I/O failed", error);
        } finally { Binder.restoreCallingIdentity(identity); }
    }
}
