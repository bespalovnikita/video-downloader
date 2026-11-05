package ru.bespalov.videodownloader.engine

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import org.json.JSONArray
import org.json.JSONObject
import ru.bespalov.video.core.Urls
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import ru.bespalov.video.core.JournalSanitizer
import java.nio.file.Files
import java.security.MessageDigest
import kotlin.math.ceil
import kotlin.math.max

/** Persistent image files avoid holding fifteen base64 strings/bitmaps per URL in memory. */
internal class PreviewGallery(context: Context, private val clock: () -> Long = System::currentTimeMillis) {
    private val root = File(context.cacheDir.canonicalFile, "preview-cache").apply {
        check(!Files.isSymbolicLink(toPath())) { "Preview cache root is a symbolic link" }
        check(mkdirs() || isDirectory) { "Cannot create preview cache" }
    }
    private var lastPrune = 0L

    fun get(url: String, allowStale: Boolean = false): EngineMetadata? = runCatching {
        val folder = File(root, sha256(url))
        val file = File(folder, "metadata.json")
        if (Files.isSymbolicLink(folder.toPath()) || Files.isSymbolicLink(file.toPath()) || !file.isFile || file.length() > 256 * 1024) return null
        val json = JSONObject(file.readText())
        if (json.optInt("version") !in 1..2 || json.optString("url") != url) return null
        val images = json.getJSONArray("images")
        if (images.length() > 15) return null
        val files = (0 until images.length()).map { index ->
            val name = images.getString(index)
            if (!Regex("[a-f0-9]{64}\\.jpg").matches(name)) return null
            File(folder, name).also { if (Files.isSymbolicLink(it.toPath()) || !it.isFile || it.length() !in 1..2_000_000) return null }
        }
        val result = EngineMetadata(url, json.optString("title"), json.optString("uploader"),
            json.optDouble("duration", 0.0).takeIf { it.isFinite() && it >= 0 } ?: 0.0,
            files, json.optString("extractor"), json.optInt("maxHeight"),
            json.optString("previewError"), json.getLong("savedAt"))
        // Version 1 did not distinguish a genuine lack of candidates from a
        // failed image request. Preserve its title, but recheck empty entries.
        if (!allowStale && ((json.optInt("version") == 1 && files.isEmpty()) || !PreviewCachePolicy.isFresh(result, clock()))) return null
        folder.setLastModified(clock())
        result
    }.getOrNull()

    suspend fun fetch(url: String, json: JSONObject, onUpdate: (EngineMetadata) -> Unit,
                      onDiagnostic: (PreviewDiagnostic) -> Unit = {}): EngineMetadata {
        val folder = File(root, sha256(url))
        PrivatePaths.child(root, folder)
        check(folder.mkdirs() || folder.isDirectory) { "Cannot create preview cache entry" }
        val previous = get(url, allowStale = true)
        val previousImages = previous?.images.orEmpty()
        val formats = json.optJSONArray("formats") ?: JSONArray()
        val metadata = EngineMetadata(url, json.text("title").ifBlank { previous?.title.orEmpty() },
            json.text("uploader").ifBlank { previous?.uploader.orEmpty() },
            json.optDouble("duration", 0.0).takeIf { it.isFinite() && it > 0 } ?: previous?.durationSeconds ?: 0.0,
            extractor = json.text("extractor").ifBlank { previous?.extractor.orEmpty() },
            maxHeight = max((0 until formats.length()).maxOfOrNull { formats.optJSONObject(it)?.optInt("height") ?: 0 } ?: 0,
                previous?.maxHeight ?: 0))
        runCatching { onUpdate(metadata.copy(images = previousImages)) }
        val candidates = mutableListOf<Pair<String, Long>>()
        val thumbnails = json.optJSONArray("thumbnails") ?: JSONArray()
        for (index in 0 until thumbnails.length()) thumbnails.optJSONObject(index)?.let {
            val width = it.optLong("width").coerceIn(0, 100_000)
            val height = it.optLong("height").coerceIn(0, 100_000)
            candidates += it.text("url") to width * height
        }
        candidates += json.text("thumbnail") to 0L
        val ordered = candidates.filter { Urls.isHttp(it.first) }.distinctBy { it.first }
            .sortedWith(compareByDescending<Pair<String, Long>> { Regex("\\.(?:jpe?g|png)(?:\\?|$)", RegexOption.IGNORE_CASE).containsMatchIn(it.first) }
                .thenByDescending { it.second }).take(60)
        val images = mutableListOf<File>()
        val contentHashes = mutableSetOf<String>()
        val failures = mutableListOf<String>()
        fun failure(stage: String, address: String, index: Int, status: Int?, bytes: Int?, reason: String) {
            if (failures.size < 4) failures += JournalSanitizer.redact(reason).take(512)
            runCatching { onDiagnostic(PreviewDiagnostic(stage, url, address, index, status, bytes, reason)) }
        }
        val imageDeadline = System.nanoTime() + 12_000_000_000L
        var total = 0
        for ((offset, candidate) in ordered.withIndex()) {
            currentCoroutineContext().ensureActive()
            if (images.size >= 15) break
            val address = candidate.first
            val remaining = ((imageDeadline - System.nanoTime()) / 1_000_000L).coerceAtMost(12_000).toInt()
            if (remaining <= 0 || total >= 12_000_000) {
                failure("budget", address, offset + 1, null, total, "Image request budget was exhausted")
                break
            }
            var status: Int? = null
            var received: Int? = null
            var stage = "http"
            var accounted = false
            try {
                val bytes = httpBytes(address, minOf(2_000_000, 12_000_000 - total), remaining) { code, size ->
                    status = code.takeIf { it > 0 }; received = size
                }
                total += bytes.size
                accounted = true
                stage = "decode"
                val hash = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
                if (!contentHashes.add(hash)) continue
                val bitmap = decode(bytes) ?: throw IOException("The candidate is not a supported image or exceeds pixel limits")
                val encoded = try {
                    ByteArrayOutputStream().use { stream ->
                        check(bitmap.compress(Bitmap.CompressFormat.JPEG, 85, stream)) { "Cannot encode preview image" }
                        stream.toByteArray()
                    }
                } finally { bitmap.recycle() }
                if (encoded.isEmpty() || encoded.size > 2_000_000) throw IOException("Encoded image exceeds its size limit")
                stage = "cache"
                val image = File(folder, "$hash.jpg")
                atomicWrite(image, encoded)
                images += image
                runCatching { onUpdate(metadata.copy(images = images.toList())) }
            } catch (cancel: CancellationException) { throw cancel }
            catch (error: Exception) {
                failure(stage, address, offset + 1, status, received, error.message ?: error.javaClass.simpleName)
            } finally {
                // Failed/oversized bodies also consume the gallery's budget.
                if (!accounted) total = (total.toLong() + (received ?: 0).coerceAtLeast(0)).coerceAtMost(12_000_000L).toInt()
            }
        }
        // Real absence is cached for 72 hours; a failed empty gallery is retriable
        // by a later request after one minute. Usable earlier files remain ready.
        val result = metadata.copy(images = images.takeIf { it.isNotEmpty() }?.toList() ?: previousImages,
            previewError = failures.takeIf { it.isNotEmpty() }?.joinToString("; ").orEmpty(), savedAtMillis = clock())
        val persisted = JSONObject().put("version", 2).put("url", url).put("savedAt", result.savedAtMillis)
            .put("title", result.title).put("uploader", result.uploader).put("duration", result.durationSeconds)
            .put("extractor", result.extractor).put("maxHeight", result.maxHeight).put("previewError", result.previewError)
            .put("images", JSONArray(result.images.map(File::getName)))
        try { atomicWrite(File(folder, "metadata.json"), persisted.toString().toByteArray()) }
        catch (error: Exception) {
            failure("cache_metadata", "", 0, null, null, error.message ?: error.javaClass.simpleName)
        }
        prune(folder)
        return result
    }

    private fun decode(bytes: ByteArray): Bitmap? {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0 || bounds.outWidth.toLong() * bounds.outHeight > 16_777_216) return null
        val scale = max(bounds.outWidth, bounds.outHeight).toDouble() / 720
        val sample = Integer.highestOneBit(ceil(scale).toInt().coerceAtLeast(1)).coerceAtLeast(1)
        val decoded = BitmapFactory.decodeByteArray(bytes, 0, bytes.size, BitmapFactory.Options().apply { inSampleSize = sample }) ?: return null
        val longest = max(decoded.width, decoded.height)
        if (longest <= 720) return decoded
        val result = Bitmap.createScaledBitmap(decoded, (decoded.width * 720.0 / longest).toInt().coerceAtLeast(1),
            (decoded.height * 720.0 / longest).toInt().coerceAtLeast(1), true)
        if (result !== decoded) decoded.recycle()
        return result
    }

    private fun atomicWrite(file: File, bytes: ByteArray) {
        PrivatePaths.child(root, file)
        val temporary = File(file.parentFile, file.name + ".tmp")
        PrivatePaths.child(root, temporary)
        temporary.outputStream().use { stream -> stream.write(bytes); stream.fd.sync() }
        check(temporary.renameTo(file)) { "Cannot save preview cache" }
    }

    private fun prune(keep: File) {
        val now = clock()
        if (now - lastPrune < 5_000) return
        lastPrune = now
        runCatching {
            val folders = root.listFiles().orEmpty().filter { it.isDirectory && !Files.isSymbolicLink(it.toPath()) && Regex("[a-f0-9]{64}").matches(it.name) }
                .sortedBy(File::lastModified)
            fun ownedBytes(folder: File) = folder.walkTopDown().onEnter { !Files.isSymbolicLink(it.toPath()) }
                .filter { it.isFile && !Files.isSymbolicLink(it.toPath()) }.sumOf(File::length)
            var bytes = folders.sumOf(::ownedBytes)
            for (folder in folders) {
                if (bytes <= 512L * 1024 * 1024) break
                if (folder == keep) continue
                val size = ownedBytes(folder)
                // Never traverse a symbolic directory even in our private cache.
                folder.walkBottomUp().onEnter { !Files.isSymbolicLink(it.toPath()) }.forEach { it.delete() }
                if (!folder.exists()) bytes -= size
            }
        }
    }
}

private fun JSONObject.text(name: String): String = if (isNull(name)) "" else optString(name).trim()
