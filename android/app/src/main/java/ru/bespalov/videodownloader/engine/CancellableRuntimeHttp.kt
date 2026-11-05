package ru.bespalov.videodownloader.engine

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import okhttp3.Call
import okhttp3.Callback
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import java.io.OutputStream
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

/** Every request has socket and call deadlines, and cancellation closes its body before returning. */
internal class CancellableRuntimeHttp(
    private val client: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(15, TimeUnit.SECONDS)
        .writeTimeout(10, TimeUnit.SECONDS)
        .callTimeout(30, TimeUnit.SECONDS)
        .followSslRedirects(false)
        .build(),
) {
    suspend fun bytes(url: String, maxBytes: Long): ByteArray = transfer(url) { response ->
        ByteArrayOutputStream().use { output ->
            copyBounded(response, output, maxBytes)
            output.toByteArray()
        }
    }

    suspend fun download(url: String, destination: File, maxBytes: Long) = transfer(url) { response ->
        destination.outputStream().use { output -> copyBounded(response, output, maxBytes) }
    }

    private fun copyBounded(response: Response, output: OutputStream, maxBytes: Long) {
        require(maxBytes > 0)
        val body = response.body ?: throw IOException("Runtime update response has no body")
        if (body.contentLength() > maxBytes) throw IOException("Runtime update response exceeds its size limit")
        body.byteStream().use { input ->
            val buffer = ByteArray(8192)
            var total = 0L
            while (true) {
                val count = input.read(buffer)
                if (count < 0) break
                total += count
                if (total > maxBytes) throw IOException("Runtime update response exceeds its size limit")
                output.write(buffer, 0, count)
            }
        }
    }

    private suspend fun <T> transfer(url: String, consume: (Response) -> T): T {
        val request = Request.Builder().url(url).header("User-Agent", "VideoDownloader-Android").build()
        val call = client.newCall(request)
        val finished = CompletableDeferred<Unit>()
        val gate = Any()
        var cancelled = false
        var consuming = false
        fun cancelRequest() {
            synchronized(gate) {
                cancelled = true
                // DNS resolution can be blocking platform code even after a
                // call is cancelled. A future callback must acquire this lease
                // before it can open/write a destination, so there is no file
                // owner to join until a response consumer has actually started.
                if (!consuming) finished.complete(Unit)
            }
            call.cancel()
        }
        try {
            return suspendCancellableCoroutine { continuation ->
                continuation.invokeOnCancellation { cancelRequest() }
                val callback = object : Callback {
                    override fun onFailure(call: Call, error: IOException) {
                        finished.complete(Unit)
                        if (continuation.isActive) continuation.resumeWithException(error)
                    }

                    override fun onResponse(call: Call, response: Response) {
                        val acquired = synchronized(gate) {
                            if (cancelled || !continuation.isActive) false
                            else { consuming = true; true }
                        }
                        if (!acquired) {
                            response.close()
                            finished.complete(Unit)
                            return
                        }
                        val result = runCatching {
                            response.use {
                                if (!it.isSuccessful) throw IOException("Runtime update HTTP ${it.code}")
                                consume(it)
                            }
                        }
                        // No response or destination stream remains open after this point.
                        synchronized(gate) { consuming = false; finished.complete(Unit) }
                        if (continuation.isActive) result.fold(continuation::resume, continuation::resumeWithException)
                    }
                }
                try { call.enqueue(callback) }
                catch (error: Exception) {
                    finished.complete(Unit)
                    if (continuation.isActive) continuation.resumeWithException(error)
                }
            }
        } finally {
            cancelRequest()
            // Do not release engine admission or delete an update file while a
            // cancelled OkHttp callback can still write to that file.
            withContext(NonCancellable) { finished.await() }
        }
    }
}
