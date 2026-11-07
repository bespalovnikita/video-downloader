package ru.bespalov.videodownloader;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.Bundle;
import android.os.Binder;
import android.os.ParcelFileDescriptor;
import android.provider.DocumentsContract;
import android.webkit.MimeTypeMap;
import java.io.File;
import java.io.FileNotFoundException;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * Test-only ContentProvider implementing the SAF calls used by DocumentFile.
 * Plain ContentProvider keeps /control usable without MANAGE_DOCUMENTS; actual
 * document access still requires Android URI grants and valid tree ancestry.
 */
public final class OutputDocumentsProvider extends ContentProvider {
    public static final String AUTHORITY = "ru.bespalov.videodownloader.test.outputs";
    public static final Uri CONTROL = Uri.parse("content://" + AUTHORITY + "/control");
    private volatile CountDownLatch queryGate;
    private final AtomicInteger waitingQueries = new AtomicInteger();
    private final Map<String, File> documents = new ConcurrentHashMap<>();
    @Override public boolean onCreate() { return true; }

    private Context app() {
        Context value = getContext();
        if (value == null) throw new IllegalStateException("The test provider is not attached");
        return value;
    }
    private File file(String id) throws FileNotFoundException {
        File file = documents.get(id);
        if (file == null || !file.exists()) throw new FileNotFoundException(id);
        return file;
    }
    private String id(File file) {
        for (Map.Entry<String, File> entry : documents.entrySet()) if (entry.getValue().equals(file)) return entry.getKey();
        String id = UUID.randomUUID().toString();
        documents.put(id, file);
        return id;
    }
    private String requireDocument(Uri uri, int grants) throws FileNotFoundException {
        if (!AUTHORITY.equals(uri.getAuthority())) throw new IllegalArgumentException("Unknown fixture authority");
        if (app().checkCallingUriPermission(uri, grants) != PackageManager.PERMISSION_GRANTED)
            throw new SecurityException("The fixture document requires an explicit URI grant");
        List<String> path = uri.getPathSegments();
        boolean tree = DocumentsContract.isTreeUri(uri);
        if (tree) {
            if (!(path.size() == 4 || path.size() == 5 && "children".equals(path.get(4))) || !"document".equals(path.get(2)))
                throw new IllegalArgumentException("Expected a fixture tree document or children URI");
        } else if (path.size() != 2 || !"document".equals(path.get(0))) {
            throw new IllegalArgumentException("Expected a fixture document URI");
        }
        String id = DocumentsContract.getDocumentId(uri);
        UUID.fromString(id);
        File document = file(id);
        if (tree) {
            String rootId = DocumentsContract.getTreeDocumentId(uri);
            UUID.fromString(rootId);
            File root = file(rootId);
            if (!root.isDirectory() || !document.toPath().startsWith(root.toPath()))
                throw new SecurityException("The document is outside the granted fixture tree");
        }
        return id;
    }
    private String documentType(String id) throws FileNotFoundException {
        File file = file(id);
        if (file.isDirectory()) return DocumentsContract.Document.MIME_TYPE_DIR;
        String name = file.getName();
        String mime = MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substring(name.lastIndexOf('.') + 1));
        return mime == null ? "application/octet-stream" : mime;
    }
    private MatrixCursor cursor(String[] projection) {
        return new MatrixCursor(projection == null ? new String[]{"document_id", "_display_name", "mime_type", "flags", "_size"} : projection);
    }
    private void row(MatrixCursor cursor, String id) throws FileNotFoundException {
        File file = file(id);
        MatrixCursor.RowBuilder row = cursor.newRow();
        for (String column : cursor.getColumnNames()) switch (column) {
            case "document_id": row.add(id); break;
            case "_display_name": row.add(file.getName()); break;
            case "mime_type": row.add(documentType(id)); break;
            case "_size": row.add(file.length()); break;
            case "flags": row.add(file.isDirectory() ? DocumentsContract.Document.FLAG_DIR_SUPPORTS_CREATE : DocumentsContract.Document.FLAG_SUPPORTS_DELETE | DocumentsContract.Document.FLAG_SUPPORTS_WRITE); break;
            default: row.add(null);
        }
    }
    @Override public Cursor query(Uri uri, String[] projection, String selection, String[] selectionArgs, String sortOrder) {
        try {
            String id = requireDocument(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION);
            MatrixCursor cursor = cursor(projection);
            if ("children".equals(uri.getLastPathSegment())) {
                File[] children = file(id).listFiles();
                if (children != null) for (File child : children) row(cursor, id(child));
            } else {
                CountDownLatch gate = queryGate;
                if (gate != null) {
                    waitingQueries.incrementAndGet();
                    try { gate.await(); }
                    catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); }
                    finally { waitingQueries.decrementAndGet(); }
                }
                row(cursor, id);
            }
            return cursor;
        } catch (FileNotFoundException missing) { throw new IllegalArgumentException("Unknown fixture document", missing); }
    }
    @Override public String getType(Uri uri) {
        try { return documentType(requireDocument(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION)); }
        catch (FileNotFoundException missing) { throw new IllegalArgumentException("Unknown fixture document", missing); }
    }
    @Override public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException {
        int grants = "r".equals(mode) ? Intent.FLAG_GRANT_READ_URI_PERMISSION : Intent.FLAG_GRANT_WRITE_URI_PERMISSION;
        if (mode.startsWith("rw")) grants |= Intent.FLAG_GRANT_READ_URI_PERMISSION;
        return ParcelFileDescriptor.open(file(requireDocument(uri, grants)), ParcelFileDescriptor.parseMode(mode));
    }
    @Override public int delete(Uri uri, String selection, String[] selectionArgs) {
        try {
            String id = requireDocument(uri, Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
            if (!file(id).delete()) throw new IllegalStateException("Could not delete a fixture");
            documents.remove(id);
            return 1;
        } catch (FileNotFoundException missing) { return 0; }
    }
    @Override public Uri insert(Uri uri, ContentValues values) { throw new UnsupportedOperationException(); }
    @Override public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) {
        throw new UnsupportedOperationException();
    }

    @SuppressWarnings("deprecation")
    @Override public Bundle call(String method, String arg, Bundle extras) {
        if ("android:createDocument".equals(method) || "android:deleteDocument".equals(method)) {
            if (extras == null) throw new IllegalArgumentException("Missing SAF document arguments");
            Uri uri = null;
            String uriKey = null;
            for (String key : extras.keySet()) {
                Object value = extras.get(key);
                if (value instanceof Uri) {
                    if (uri != null) throw new IllegalArgumentException("More than one SAF document URI");
                    uri = (Uri) value;
                    uriKey = key;
                }
            }
            if (uri == null) throw new IllegalArgumentException("Missing SAF document URI");
            if ("android:deleteDocument".equals(method)) {
                if (delete(uri, null, null) != 1) throw new IllegalStateException("The fixture document is already absent");
                return Bundle.EMPTY;
            }
            try {
                String parentId = requireDocument(uri, Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
                if (!DocumentsContract.isTreeUri(uri)) throw new IllegalArgumentException("Fixture creation needs a granted tree");
                File parent = file(parentId);
                String name = extras.getString(DocumentsContract.Document.COLUMN_DISPLAY_NAME);
                String mime = extras.getString(DocumentsContract.Document.COLUMN_MIME_TYPE);
                if (!parent.isDirectory() || name == null || name.isEmpty() || name.contains("/") || name.equals(".") || name.equals(".."))
                    throw new IllegalArgumentException("Invalid fixture document name or parent");
                File child = new File(parent, name);
                boolean created = DocumentsContract.Document.MIME_TYPE_DIR.equals(mime) ? child.mkdir() : child.createNewFile();
                if (!created) throw new IllegalStateException("A fixture name already exists");
                Bundle result = new Bundle();
                result.putParcelable(uriKey, DocumentsContract.buildDocumentUriUsingTree(uri, id(child)));
                return result;
            } catch (java.io.IOException failure) { throw new IllegalStateException(failure); }
        }
        if ("block".equals(method)) { queryGate = new CountDownLatch(1); return Bundle.EMPTY; }
        if ("release".equals(method)) { CountDownLatch gate = queryGate; queryGate = null; if (gate != null) gate.countDown(); return Bundle.EMPTY; }
        if ("status".equals(method)) { Bundle result = new Bundle(); result.putInt("waiting", waitingQueries.get()); return result; }
        if (!"fixture".equals(method) && !"forget".equals(method)) throw new UnsupportedOperationException(method);
        UUID.fromString(arg);
        long identity = Binder.clearCallingIdentity();
        try {
            File root = new File(app().getCacheDir(), "output-tree-" + arg);
            Uri tree = DocumentsContract.buildTreeDocumentUri(AUTHORITY, arg);
            int grants = Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION | Intent.FLAG_GRANT_PREFIX_URI_PERMISSION;
            if ("fixture".equals(method)) {
                if (!root.mkdir()) throw new IllegalStateException("Fixture directory already exists");
                documents.put(arg, root);
                app().grantUriPermission(extras.getString("package"), tree, grants);
                Bundle result = new Bundle(); result.putString("tree", tree.toString()); return result;
            }
            app().revokeUriPermission(tree, grants);
            File[] children = root.listFiles();
            if (children != null) for (File child : children) { documents.remove(id(child)); child.delete(); }
            documents.remove(arg); root.delete(); return Bundle.EMPTY;
        } finally { Binder.restoreCallingIdentity(identity); }
    }
}
