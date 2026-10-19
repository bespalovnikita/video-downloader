package com.bespalovnikita.videodownloader.downloader

import com.bespalovnikita.videodownloader.data.AppStore
import com.bespalovnikita.videodownloader.data.PreviewCache
import com.bespalovnikita.videodownloader.model.PreviewData
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.PriorityBlockingQueue
import java.util.concurrent.atomic.AtomicLong

class PreviewPrefetcher(
    private val store: AppStore,
    private val cache: PreviewCache,
    private val client: YtDlpClient,
    private val scope: CoroutineScope
) {
    private data class Task(val url: String, val priority: Int, val sequence: Long)

    private val sequence = AtomicLong()
    private val queue = PriorityBlockingQueue(
        16,
        compareBy<Task> { it.priority }.thenBy { it.sequence }
    )
    private val queued = ConcurrentHashMap.newKeySet<String>()
    private val signal = Channel<Unit>(Channel.CONFLATED)

    fun start() {
        scope.launch {
            cache.prune()
            while (isActive) {
                val task = queue.poll()
                if (task == null) {
                    signal.receive()
                    continue
                }

                queued.remove(task.url)
                runCatching {
                    val cached = cache.get(task.url)
                    if (cached != null) {
                        store.updatePreview(task.url, cached)
                        return@runCatching
                    }

                    var preview = client.fetchPreview(task.url)
                    if (preview.thumbnailUrl.isNotBlank()) {
                        val local = downloadThumbnail(preview.thumbnailUrl, task.url)
                        if (local.isNotBlank()) preview = preview.copy(localThumbnailPath = local)
                    }

                    cache.put(preview)
                    store.updatePreview(task.url, preview)
                }.onFailure {
                    store.setPreviewError(task.url, it.message ?: it.javaClass.simpleName)
                }
            }
        }
    }

    fun enqueue(url: String, priority: Boolean = false) {
        if (url.isBlank()) return
        if (!queued.add(url)) {
            if (priority) prioritize(url)
            return
        }
        queue += Task(url, if (priority) 0 else 1, sequence.incrementAndGet())
        signal.trySend(Unit)
    }

    fun prioritize(url: String) {
        if (url.isBlank()) return
        queue.removeIf { it.url == url }
        queued.add(url)
        queue += Task(url, 0, sequence.incrementAndGet())
        signal.trySend(Unit)
    }

    private fun downloadThumbnail(remoteUrl: String, cacheKey: String): String {
        val digest = MessageDigest.getInstance("SHA-256")
            .digest(cacheKey.toByteArray(Charsets.UTF_8))
            .joinToString("") { "%02x".format(it) }
        val target = File(cache.thumbnailDir, digest + ".img")
        val temp = File(cache.thumbnailDir, digest + ".tmp")

        var connection: HttpURLConnection? = null
        return try {
            connection = URL(remoteUrl).openConnection() as HttpURLConnection
            connection.instanceFollowRedirects = true
            connection.connectTimeout = 12_000
            connection.readTimeout = 12_000
            connection.setRequestProperty(
                "User-Agent",
                "Mozilla/5.0 (Linux; Android 16) VideoDownloader/0.1"
            )
            connection.inputStream.use { input ->
                temp.outputStream().use { output -> input.copyTo(output) }
            }
            if (target.exists()) target.delete()
            temp.renameTo(target)
            target.absolutePath
        } catch (_: Throwable) {
            temp.delete()
            ""
        } finally {
            connection?.disconnect()
        }
    }
}
