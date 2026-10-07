package com.bespalovnikita.videodownloader.data

import android.content.Context
import android.util.AtomicFile
import com.bespalovnikita.videodownloader.model.PreviewData
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

class PreviewCache(context: Context) {
    private val file = AtomicFile(File(context.filesDir, "preview-cache.json"))
    private val mutex = Mutex()
    private val ttlMs = 72L * 60L * 60L * 1000L
    private var entries: MutableMap<String, PreviewData> = load().toMutableMap()

    val thumbnailDir = File(context.filesDir, "preview-thumbnails").apply { mkdirs() }

    suspend fun get(url: String): PreviewData? = mutex.withLock {
        val value = entries[url] ?: return@withLock null
        if (System.currentTimeMillis() - value.savedAtEpochMs >= ttlMs) {
            removeInternal(url)
            persist()
            return@withLock null
        }
        value
    }

    suspend fun put(value: PreviewData) = mutex.withLock {
        entries[value.url] = value
        persist()
    }

    suspend fun prune() = mutex.withLock {
        val cutoff = System.currentTimeMillis() - ttlMs
        val expired = entries.values.filter { it.savedAtEpochMs < cutoff }.map { it.url }
        expired.forEach { removeInternal(it) }
        if (expired.isNotEmpty()) persist()
    }

    private fun removeInternal(url: String) {
        val previous = entries.remove(url)
        if (previous != null && previous.localThumbnailPath.isNotBlank()) {
            runCatching { File(previous.localThumbnailPath).delete() }
        }
    }

    private fun persist() {
        val root = JSONObject()
        root.put("entries", JSONArray().apply {
            entries.values.forEach { p ->
                put(JSONObject().apply {
                    put("url", p.url)
                    put("title", p.title)
                    put("uploader", p.uploader)
                    put("thumbnailUrl", p.thumbnailUrl)
                    put("localThumbnailPath", p.localThumbnailPath)
                    put("durationSeconds", p.durationSeconds)
                    put("maxHeight", p.maxHeight)
                    put("maxFps", p.maxFps)
                    put("codecs", p.codecs)
                    put("hdr", p.hdr)
                    put("savedAtEpochMs", p.savedAtEpochMs)
                })
            }
        })

        val out = file.startWrite()
        try {
            out.bufferedWriter(Charsets.UTF_8).use { it.write(root.toString()) }
            file.finishWrite(out)
        } catch (t: Throwable) {
            file.failWrite(out)
            throw t
        }
    }

    private fun load(): Map<String, PreviewData> {
        if (!file.baseFile.exists()) return emptyMap()
        return runCatching {
            val root = file.openRead().bufferedReader(Charsets.UTF_8).use { JSONObject(it.readText()) }
            val array = root.optJSONArray("entries") ?: JSONArray()
            buildMap {
                for (i in 0 until array.length()) {
                    val o = array.getJSONObject(i)
                    val p = PreviewData(
                        url = o.optString("url"),
                        title = o.optString("title"),
                        uploader = o.optString("uploader"),
                        thumbnailUrl = o.optString("thumbnailUrl"),
                        localThumbnailPath = o.optString("localThumbnailPath"),
                        durationSeconds = o.optLong("durationSeconds"),
                        maxHeight = o.optInt("maxHeight"),
                        maxFps = o.optDouble("maxFps"),
                        codecs = o.optString("codecs"),
                        hdr = o.optString("hdr"),
                        savedAtEpochMs = o.optLong("savedAtEpochMs")
                    )
                    if (p.url.isNotBlank()) put(p.url, p)
                }
            }
        }.getOrElse { emptyMap() }
    }
}
