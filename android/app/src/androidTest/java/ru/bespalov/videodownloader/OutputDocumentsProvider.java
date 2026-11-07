package ru.bespalov.videodownloader;

import android.content.Intent;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.Bundle;
import android.os.Binder;
import android.os.CancellationSignal;
import android.os.ParcelFileDescriptor;
import android.provider.DocumentsContract;
import android.provider.DocumentsProvider;
import android.webkit.MimeTypeMap;
import java.io.File;
import java.io.FileNotFoundException;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.atomic.AtomicInteger;

/** A real Binder-backed SAF tree containing only instrumentation-owned files. IDs are opaque. */
public final class OutputDocumentsProvider extends DocumentsProvider {
    public static final String AUTHORITY = "ru.bespalov.videodownloader.test.outputs";
    public static final Uri CONTROL = Uri.parse("content://" + AUTHORITY + "/control");
    private volatile CountDownLatch queryGate;
    private final AtomicInteger waitingQueries = new AtomicInteger();
    private final Map<String, File> documents = new ConcurrentHashMap<>();
    @Override public boolean onCreate() { return true; }
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
    @Override public String getDocumentType(String id) throws FileNotFoundException {
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
            case "mime_type": row.add(getDocumentType(id)); break;
            case "_size": row.add(file.length()); break;
            case "flags": row.add(file.isDirectory() ? DocumentsContract.Document.FLAG_DIR_SUPPORTS_CREATE : DocumentsContract.Document.FLAG_SUPPORTS_DELETE | DocumentsContract.Document.FLAG_SUPPORTS_WRITE); break;
            default: row.add(null);
        }
    }
    @Override public Cursor queryRoots(String[] projection) { return new MatrixCursor(new String[]{"root_id", "document_id"}); }
    @Override public Cursor queryDocument(String id, String[] projection) throws FileNotFoundException {
        CountDownLatch gate = queryGate;
        if (gate != null) {
            waitingQueries.incrementAndGet();
            try { gate.await(); }
            catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); }
            finally { waitingQueries.decrementAndGet(); }
        }
        MatrixCursor cursor = cursor(projection); row(cursor, id); return cursor;
    }
    @Override public Cursor queryChildDocuments(String parent, String[] projection, String sort) throws FileNotFoundException {
        MatrixCursor cursor = cursor(projection);
        File[] children = file(parent).listFiles();
        if (children != null) for (File child : children) row(cursor, id(child));
        return cursor;
    }
    @Override public boolean isChildDocument(String parent, String child) {
        try { return file(child).toPath().startsWith(file(parent).toPath()); }
        catch (FileNotFoundException missing) { return false; }
    }
    @Override public String createDocument(String parent, String mime, String name) throws FileNotFoundException {
        if (name.contains("/") || name.equals(".") || name.equals("..")) throw new IllegalArgumentException();
        File child = new File(file(parent), name);
        try {
            boolean created = DocumentsContract.Document.MIME_TYPE_DIR.equals(mime) ? child.mkdir() : child.createNewFile();
            if (!created) throw new IllegalStateException("A fixture name already exists");
            return id(child);
        } catch (java.io.IOException failure) { throw new IllegalStateException(failure); }
    }
    @Override public ParcelFileDescriptor openDocument(String id, String mode, CancellationSignal signal) throws FileNotFoundException {
        return ParcelFileDescriptor.open(file(id), ParcelFileDescriptor.parseMode(mode));
    }
    @Override public void deleteDocument(String id) throws FileNotFoundException {
        if (!file(id).delete()) throw new IllegalStateException("Could not delete a fixture");
        documents.remove(id);
    }
    @Override public Bundle call(String method, String arg, Bundle extras) {
        if ("block".equals(method)) { queryGate = new CountDownLatch(1); return Bundle.EMPTY; }
        if ("release".equals(method)) { CountDownLatch gate = queryGate; queryGate = null; if (gate != null) gate.countDown(); return Bundle.EMPTY; }
        if ("status".equals(method)) { Bundle result = new Bundle(); result.putInt("waiting", waitingQueries.get()); return result; }
        if (!"fixture".equals(method) && !"forget".equals(method)) return super.call(method, arg, extras);
        UUID.fromString(arg);
        long identity = Binder.clearCallingIdentity();
        try {
        File root = new File(getContext().getCacheDir(), "output-tree-" + arg);
        Uri tree = DocumentsContract.buildTreeDocumentUri(AUTHORITY, arg);
        int grants = Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION | Intent.FLAG_GRANT_PREFIX_URI_PERMISSION;
        if ("fixture".equals(method)) {
            if (!root.mkdir()) throw new IllegalStateException("Fixture directory already exists");
            documents.put(arg, root);
            getContext().grantUriPermission(extras.getString("package"), tree, grants);
            Bundle result = new Bundle(); result.putString("tree", tree.toString()); return result;
        }
        getContext().revokeUriPermission(tree, grants);
        File[] children = root.listFiles();
        if (children != null) for (File child : children) { documents.remove(id(child)); child.delete(); }
        documents.remove(arg); root.delete(); return Bundle.EMPTY;
        } finally { Binder.restoreCallingIdentity(identity); }
    }
}
