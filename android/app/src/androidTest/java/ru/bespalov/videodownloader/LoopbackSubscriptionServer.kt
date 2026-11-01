package ru.bespalov.videodownloader

import android.graphics.Bitmap
import java.io.ByteArrayOutputStream
import java.io.Closeable
import java.io.IOException
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketException
import java.nio.charset.StandardCharsets
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

/** Real generic yt-dlp collections and HTML5 posters; all traffic stays on this device. */
internal class LoopbackSubscriptionServer(private val media: ByteArray) : Closeable {
    private val server = ServerSocket(0, 16, InetAddress.getByName("127.0.0.1"))
    private val closed = AtomicBoolean(false)
    private val workers = Executors.newCachedThreadPool()
    private val namespace = UUID.randomUUID().toString()
    private val counts = ConcurrentHashMap<String, AtomicInteger>()
    private val blocked = ConcurrentHashMap<String, CountDownLatch>()
    val baseUrl = "http://127.0.0.1:${server.localPort}/$namespace"
    private val cover = Bitmap.createBitmap(4, 4, Bitmap.Config.ARGB_8888).let { bitmap ->
        bitmap.eraseColor(0xff674fa3.toInt())
        try { ByteArrayOutputStream().use { output -> bitmap.compress(Bitmap.CompressFormat.PNG, 100, output); output.toByteArray() } }
        finally { bitmap.recycle() }
    }

    init {
        workers.execute {
            while (!closed.get()) {
                try {
                    val client = server.accept()
                    workers.execute {
                        try { client.use(::respond) } catch (_: IOException) { /* yt-dlp may stop a metadata probe early. */ }
                    }
                } catch (_: SocketException) {
                    if (!closed.get()) throw IllegalStateException("Subscription HTTP listener stopped unexpectedly")
                }
            }
        }
    }

    fun sourceUrl(name: String) = "$baseUrl/collections/$name.rss"
    fun videoUrl(name: String) = "$baseUrl/videos/$name.html"
    fun requests(path: String): Int = counts[path]?.get() ?: 0
    fun blockSource(name: String) { blocked[name] = CountDownLatch(1) }
    fun releaseSource(name: String) { blocked.remove(name)?.countDown() }

    private fun respond(socket: Socket) {
        socket.soTimeout = 10_000
        val input = socket.getInputStream().bufferedReader(StandardCharsets.US_ASCII)
        val request = input.readLine()?.split(' ') ?: return
        if (request.size < 2) return
        var headerBytes = 0
        while (true) {
            val line = input.readLine() ?: return
            if (line.isEmpty()) break
            headerBytes += line.length
            if (headerBytes > 32_768) return
        }
        val path = request[1].substringBefore('?').removePrefix("/$namespace")
        counts.computeIfAbsent(path) { AtomicInteger() }.incrementAndGet()
        var status = "200 OK"
        val (type, bytes) = when {
            path.startsWith("/collections/forbidden") -> { status = "403 Forbidden"; "text/plain" to ByteArray(0) }
            path.startsWith("/collections/") -> {
                val name = path.substringAfterLast('/').removeSuffix(".rss")
                blocked[name]?.await(30, TimeUnit.SECONDS)
                val items = listOf("${name}1", "${name}2").joinToString("") { video ->
                    "<item><title>Video $video</title><link>${videoUrl(video)}</link></item>"
                }
                "application/rss+xml; charset=utf-8" to
                    "<rss version=\"2.0\"><channel><title>Collection $name</title>$items</channel></rss>".toByteArray()
            }
            path.endsWith(".html") -> {
                val name = path.substringAfterLast('/').removeSuffix(".html")
                "text/html; charset=utf-8" to ("<!doctype html><html><head><title>Video $name</title>" +
                    "<meta property=\"og:image\" content=\"$baseUrl/covers/$name.png\"></head><body>" +
                    "<video controls src=\"$baseUrl/media/$name.mp4\" poster=\"$baseUrl/covers/$name.png\"></video>" +
                    "</body></html>").toByteArray()
            }
            path.endsWith(".png") -> "image/png" to cover
            path.endsWith(".mp4") -> "video/mp4" to media
            else -> { status = "404 Not Found"; "text/plain" to ByteArray(0) }
        }
        val output = socket.getOutputStream()
        output.write(("HTTP/1.1 $status\r\nContent-Type: $type\r\nContent-Length: ${bytes.size}\r\n" +
            "Connection: close\r\n\r\n").toByteArray(StandardCharsets.US_ASCII))
        if (request[0] != "HEAD") output.write(bytes)
        output.flush()
    }

    override fun close() {
        closed.set(true)
        blocked.values.forEach(CountDownLatch::countDown)
        blocked.clear()
        server.close()
        workers.shutdownNow()
    }
}
