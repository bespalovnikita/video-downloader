package ru.bespalov.videodownloader

import java.io.Closeable
import java.io.IOException
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketException
import java.nio.charset.StandardCharsets
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

/** Real range-capable media responses; the optional barrier exposes four native workers together. */
internal class SessionLoopbackServer(
    private val bodies: Map<String, ByteArray>,
    private val delays: Map<String, Long> = emptyMap(),
    simultaneousTransfers: Int = 0,
) : Closeable {
    private val server = ServerSocket(0, 16, InetAddress.getByName("127.0.0.1"))
    private val workers = Executors.newCachedThreadPool()
    private val closed = AtomicBoolean()
    private val requests = ConcurrentHashMap<String, AtomicInteger>()
    private val firstGets = ConcurrentHashMap.newKeySet<String>()
    private val barrier = CountDownLatch(simultaneousTransfers)
    val baseUrl = "http://127.0.0.1:${server.localPort}"

    init {
        workers.execute {
            while (!closed.get()) {
                try {
                    val socket = server.accept()
                    workers.execute {
                        try { socket.use(::respond) }
                        catch (_: IOException) { /* Pause/cancel closes a genuine native HTTP transfer. */ }
                        catch (_: InterruptedException) { Thread.currentThread().interrupt() }
                    }
                } catch (_: SocketException) {
                    if (!closed.get()) throw IllegalStateException("Session HTTP listener stopped unexpectedly")
                }
            }
        }
    }

    fun getCount(path: String) = requests[path]?.get() ?: 0

    private fun respond(socket: Socket) {
        socket.soTimeout = 10_000
        val input = socket.getInputStream().bufferedReader(StandardCharsets.US_ASCII)
        val request = input.readLine()?.split(' ') ?: return
        if (request.size < 2) return
        val headers = mutableMapOf<String, String>()
        var bytes = 0
        while (true) {
            val line = input.readLine() ?: return
            if (line.isEmpty()) break
            bytes += line.length
            if (bytes > 32_768) return
            val split = line.indexOf(':')
            if (split > 0) headers[line.take(split).lowercase()] = line.substring(split + 1).trim()
        }
        val path = request[1].substringBefore('?')
        val body = bodies[path]
        val output = socket.getOutputStream()
        if (body == null) {
            output.write("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".toByteArray())
            return
        }
        val range = headers["range"]?.let { Regex("bytes=(\\d+)-(\\d*)").matchEntire(it) }
        val start = range?.groupValues?.get(1)?.toIntOrNull() ?: 0
        if (start !in body.indices) {
            output.write("HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */${body.size}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".toByteArray())
            return
        }
        val end = range?.groupValues?.get(2)?.toIntOrNull()?.coerceAtMost(body.lastIndex) ?: body.lastIndex
        val head = request[0] == "HEAD"
        if (!head) {
            requests.computeIfAbsent(path) { AtomicInteger() }.incrementAndGet()
            if (firstGets.add(path)) barrier.countDown()
            check(barrier.await(45, TimeUnit.SECONDS)) { "The expected native workers did not reach the HTTP origin" }
        }
        output.write(buildString {
            append(if (range == null) "HTTP/1.1 200 OK\r\n" else "HTTP/1.1 206 Partial Content\r\n")
            val contentType = if (path.endsWith(".html")) "text/html; charset=utf-8" else "video/mp4"
            append("Content-Type: $contentType\r\nContent-Length: ${end - start + 1}\r\nAccept-Ranges: bytes\r\n")
            if (range != null) append("Content-Range: bytes $start-$end/${body.size}\r\n")
            append("Connection: close\r\n\r\n")
        }.toByteArray(StandardCharsets.US_ASCII))
        if (!head) {
            var cursor = start
            while (cursor <= end && !closed.get()) {
                val length = minOf(8192, end - cursor + 1)
                output.write(body, cursor, length)
                output.flush()
                cursor += length
                val pause = delays[path] ?: 0L
                if (pause > 0 && cursor <= end) Thread.sleep(pause)
            }
        }
        output.flush()
    }

    override fun close() {
        closed.set(true)
        while (barrier.count > 0) barrier.countDown()
        server.close()
        workers.shutdownNow()
    }
}
