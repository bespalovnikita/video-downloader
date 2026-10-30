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
import java.nio.file.Files
import java.security.MessageDigest
import kotlin.math.ceil
import kotlin.math.max

/** Persistent image files avoid holding fifteen base64 strings/bitmaps per URL in memory. */
internal class PreviewGallery(private val context: Context) {
    private val root = File(context.cacheDir, "preview-cache").apply { mkdirs() }
    private val ttlMs = 72 * 60 * 60_000L
    private var lastPrune = 0L

    fun get(url: String, allowStale: Boolean = false): EngineMetadata? = runCatching {
        val folder = File(root, sha256(url))
        val file = File(folder, "metadata.json")
        if (Files.isSymbolicLink(folder.toPath()) || Files.isSymbolicLink(file.toPath()) || !file.isFile || file.length() > 256 * 1024) return null
        val json = JSONObject(file.readText())
        if (json.optInt("version") != 1 || json.optString("url") != url ||
            (!allowStale && System.currentTimeMillis() - json.getLong("savedAt") !in 0..ttlMs)) return null
        val images = json.getJSONArray("images")
        if (images.length() > 15) return null
        val files = (0 until images.length()).map { index ->
            val name = images.getString(index)
            if (!Regex("[a-f0-9]{64}\\.jpg").matches(name)) return null
            File(folder, name).also { if (Files.isSymbolicLink(it.toPath()) || !it.isFile || it.length() > 2_000_000) return null }
        }
        folder.setLastModified(System.currentTimeMillis())
        EngineMetadata(url, json.optString("title"), json.optString("uploader"),
            json.optDouble("duration", 0.0).takeIf { it.isFinite() && it >= 0 } ?: 0.0,
            files, json.optString("extractor"), json.optInt("maxHeight"))
    }.getOrNull()

    suspend fun fetch(url: String, json: JSONObject, onUpdate: (EngineMetadata) -> Unit): EngineMetadata {
        val folder = File(root, sha256(url)).apply { mkdirs() }
        val previousImages = get(url, allowStale = true)?.images.orEmpty()
        val formats = json.optJSONArray("formats") ?: JSONArray()
        val metadata = EngineMetadata(url, json.text("title"), json.text("uploader"),
            json.optDouble("duration", 0.0).takeIf { it.isFinite() && it >= 0 } ?: 0.0,
            extractor = json.text("extractor"),
            maxHeight = (0 until formats.length()).maxOfOrNull { formats.optJSONObject(it)?.optInt("height") ?: 0 } ?: 0)
        runCatching { onUpdate(metadata.copy(images = previousImages)) }
        val candidates = mutableListOf<Pair<String, Long>>()
        val thumbnails = json.optJSONArray("thumbnails") ?: JSONArray()
        for (index in 0 until thumbnails.length()) thumbnails.optJSONObject(index)?.let {
            candidates += it.text("url") to (it.optLong("width").coerceAtLeast(0) * it.optLong("height").coerceAtLeast(0))
        }
        candidates += json.text("thumbnail") to 0L
        val ordered = candidates.filter { Urls.isHttp(it.first) }.distinctBy { it.first }
            .sortedWith(compareByDescending<Pair<String, Long>> { Regex("\\.(?:jpe?g|png)(?:\\?|$)", RegexOption.IGNORE_CASE).containsMatchIn(it.first) }
                .thenByDescending { it.second }).take(60)
        val images = mutableListOf<File>()
        val contentHashes = mutableSetOf<String>()
        val imageDeadline = System.nanoTime() + 12_000_000_000L
        var total = 0
        for ((address) in ordered) {
            currentCoroutineContext().ensureActive()
            if (images.size >= 15 || total >= 12_000_000) break
            val remaining = ((imageDeadline - System.nanoTime()) / 1_000_000L).coerceAtMost(12_000).toInt()
            if (remaining <= 0) break
            try {
                val bytes = httpBytes(address, minOf(2_000_000, 12_000_000 - total), remaining)
                total += bytes.size
                val hash = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
                if (!contentHashes.add(hash)) continue
                val bitmap = decode(bytes) ?: continue
                val encoded = try {
                    ByteArrayOutputStream().use { stream -> bitmap.compress(Bitmap.CompressFormat.JPEG, 85, stream); stream.toByteArray() }
                } finally { bitmap.recycle() }
                if (encoded.isEmpty() || encoded.size > 2_000_000) continue
                val image = File(folder, "$hash.jpg")
                atomicWrite(image, encoded)
                images += image
                runCatching { onUpdate(metadata.copy(images = images.toList())) }
            } catch (cancel: CancellationException) { throw cancel }
            catch (_: Exception) { /* One bad image never discards the title or earlier pictures. */ }
        }
        // A temporary image failure must not erase a still-usable cached cover.
        val result = metadata.copy(images = images.takeIf { it.isNotEmpty() }?.toList() ?: previousImages)
        val persisted = JSONObject().put("version", 1).put("url", url).put("savedAt", System.currentTimeMillis())
            .put("title", result.title).put("uploader", result.uploader).put("duration", result.durationSeconds)
            .put("extractor", result.extractor).put("maxHeight", result.maxHeight)
            .put("images", JSONArray(result.images.map(File::getName)))
        runCatching { atomicWrite(File(folder, "metadata.json"), persisted.toString().toByteArray()) }
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
        val temporary = File(file.parentFile, file.name + ".tmp")
        temporary.outputStream().use { stream -> stream.write(bytes); stream.fd.sync() }
        check(temporary.renameTo(file)) { "Cannot save preview cache" }
    }

    private fun prune(keep: File) {
        val now = System.currentTimeMillis()
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
