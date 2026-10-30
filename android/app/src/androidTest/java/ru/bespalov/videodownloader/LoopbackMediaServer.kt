package ru.bespalov.videodownloader

import java.io.Closeable
import java.io.IOException
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketException
import java.nio.charset.StandardCharsets
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/** A real, local HTTP origin for native yt-dlp; no public site is contacted. */
internal class LoopbackMediaServer(
    private val media: ByteArray,
    private val chunkBytes: Int = Int.MAX_VALUE,
    private val chunkDelayMillis: Long = 0,
) : Closeable {
    private val server = ServerSocket(0, 16, InetAddress.getByName("127.0.0.1"))
    private val closed = AtomicBoolean(false)
    private val workers = Executors.newCachedThreadPool()
    val baseUrl: String = "http://127.0.0.1:${server.localPort}"

    init {
        workers.execute {
            while (!closed.get()) {
                try {
                    val client = server.accept()
                    workers.execute {
                        try { client.use(::respond) } catch (_: IOException) { /* A native client may close a ranged probe early. */ }
                    }
                } catch (_: SocketException) {
                    if (!closed.get()) throw IllegalStateException("Loopback HTTP listener stopped unexpectedly")
                }
            }
        }
    }

    private fun respond(socket: Socket) {
        socket.soTimeout = 10_000
        val input = socket.getInputStream().bufferedReader(StandardCharsets.US_ASCII)
        val request = input.readLine()?.split(' ') ?: return
        if (request.size < 2) return
        val headers = linkedMapOf<String, String>()
        var headerBytes = 0
        while (true) {
            val line = input.readLine() ?: return
            if (line.isEmpty()) break
            headerBytes += line.length
            if (headerBytes > 32_768) return
            val separator = line.indexOf(':')
            if (separator > 0) headers[line.substring(0, separator).lowercase()] = line.substring(separator + 1).trim()
        }
        val output = socket.getOutputStream()
        if (request[1].substringBefore('?').startsWith("/forbidden")) {
            output.write("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".toByteArray())
            return
        }
        val range = headers["range"]?.let { Regex("bytes=(\\d+)-(\\d*)").matchEntire(it) }
        val from = range?.groupValues?.get(1)?.toIntOrNull() ?: 0
        if (from !in media.indices) {
            output.write("HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */${media.size}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".toByteArray())
            return
        }
        val to = range?.groupValues?.get(2)?.toIntOrNull()?.coerceAtMost(media.lastIndex) ?: media.lastIndex
        val count = (to - from + 1).coerceAtLeast(0)
        val response = buildString {
            append(if (range == null) "HTTP/1.1 200 OK\r\n" else "HTTP/1.1 206 Partial Content\r\n")
            append("Content-Type: video/mp4\r\nContent-Length: $count\r\nAccept-Ranges: bytes\r\n")
            if (range != null) append("Content-Range: bytes $from-$to/${media.size}\r\n")
            append("Connection: close\r\n\r\n")
        }
        output.write(response.toByteArray(StandardCharsets.US_ASCII))
        if (request[0] != "HEAD" && count > 0) {
            var position = from
            while (position <= to && !closed.get()) {
                val length = minOf(chunkBytes, to - position + 1)
                output.write(media, position, length)
                output.flush()
                position += length
                if (position <= to && chunkDelayMillis > 0) Thread.sleep(chunkDelayMillis)
            }
        }
        output.flush()
    }

    override fun close() {
        closed.set(true)
        server.close()
        workers.shutdownNow()
    }
}
