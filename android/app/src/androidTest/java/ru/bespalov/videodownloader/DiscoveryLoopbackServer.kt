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

/** All collection, metadata, image and media traffic is observable on the device. */
internal class DiscoveryLoopbackServer(private val media: ByteArray) : Closeable {
    private val server = ServerSocket(0, 16, InetAddress.getByName("127.0.0.1"))
    private val workers = Executors.newCachedThreadPool()
    private val closed = AtomicBoolean(false)
    private val namespace = UUID.randomUUID().toString()
    private val collections = ConcurrentHashMap<String, List<String>>()
    private val statuses = ConcurrentHashMap<String, Int>()
    private val blocked = ConcurrentHashMap<String, CountDownLatch>()
    private val counts = ConcurrentHashMap<String, AtomicInteger>()
    private val activeImages = AtomicInteger()
    private val peakImages = AtomicInteger()
    val baseUrl = "http://127.0.0.1:${server.localPort}/$namespace"
    val maximumConcurrentImages: Int get() = peakImages.get()
    private val png = Bitmap.createBitmap(6, 4, Bitmap.Config.ARGB_8888).let { bitmap ->
        bitmap.eraseColor(0xff528756.toInt())
        try { ByteArrayOutputStream().use { stream -> bitmap.compress(Bitmap.CompressFormat.PNG, 100, stream); stream.toByteArray() } }
        finally { bitmap.recycle() }
    }

    init {
        workers.execute {
            while (!closed.get()) {
                try {
                    val client = server.accept()
                    workers.execute { try { client.use(::respond) } catch (_: IOException) { /* Probe/cancel closes the client. */ } }
                } catch (_: SocketException) {
                    if (!closed.get()) throw IllegalStateException("Discovery listener stopped unexpectedly")
                }
            }
        }
    }

    fun collection(name: String, videos: List<String>): String {
        collections[name] = videos
        return "$baseUrl/collections/$name.rss"
    }
    fun video(name: String) = "$baseUrl/videos/$name.html"
    fun cover(name: String) = "$baseUrl/covers/$name.png"
    fun imageStatus(name: String, status: Int) { statuses[name] = status }
    fun blockImage(name: String) { blocked[name] = CountDownLatch(1) }
    fun releaseImage(name: String) { blocked.remove(name)?.countDown() }
    fun requests(path: String, method: String = "GET"): Int = counts["$method $path"]?.get() ?: 0

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
        counts.computeIfAbsent("${request[0]} $path") { AtomicInteger() }.incrementAndGet()
        val image = path.startsWith("/covers/")
        if (image) {
            val active = activeImages.incrementAndGet()
            peakImages.updateAndGet { maxOf(it, active) }
        }
        try {
            val name = path.substringAfterLast('/').substringBeforeLast('.')
            var status = 200
            val (type, bytes) = when {
                path.startsWith("/collections/") -> {
                    val videos = collections[name]
                    if (videos == null) { status = 404; "text/plain" to ByteArray(0) }
                    else {
                        val entries = videos.joinToString("") { "<item><title>Video $it</title><link>${video(it)}</link></item>" }
                        "application/rss+xml; charset=utf-8" to "<rss version=\"2.0\"><channel><title>Collection $name</title>$entries</channel></rss>".toByteArray()
                    }
                }
                path.startsWith("/videos/") -> "text/html; charset=utf-8" to (
                    "<!doctype html><html><head><title>Video $name</title><meta property=\"og:image\" content=\"${cover(name)}\"></head>" +
                        "<body><video controls poster=\"${cover(name)}\"><source src=\"$baseUrl/media/$name.mp4\" type=\"video/mp4\"></video></body></html>"
                    ).toByteArray()
                image -> {
                    blocked[name]?.await(20, TimeUnit.SECONDS)
                    status = statuses[name] ?: 200
                    if (status == 200) "image/png" to png else "text/plain" to "temporarily unavailable".toByteArray()
                }
                path.startsWith("/media/") -> "video/mp4" to media
                else -> { status = 404; "text/plain" to ByteArray(0) }
            }
            val reason = when (status) { 200 -> "OK"; 503 -> "Service Unavailable"; else -> "Not Found" }
            val output = socket.getOutputStream()
            output.write(("HTTP/1.1 $status $reason\r\nContent-Type: $type\r\nContent-Length: ${bytes.size}\r\nConnection: close\r\n\r\n")
                .toByteArray(StandardCharsets.US_ASCII))
            if (request[0] != "HEAD") output.write(bytes)
            output.flush()
        } finally { if (image) activeImages.decrementAndGet() }
    }

    override fun close() {
        closed.set(true)
        blocked.values.forEach(CountDownLatch::countDown)
        blocked.clear()
        server.close()
        workers.shutdownNow()
    }
}
