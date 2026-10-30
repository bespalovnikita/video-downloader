package ru.bespalov.videodownloader.engine

import android.content.Context
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/** Native binaries are APK assets extracted by Android, not downloaded executables. */
object NativeMedia {
    private val deadlines = Executors.newScheduledThreadPool(1) { task -> Thread(task, "native-media-deadline").apply { isDaemon = true } }

    fun environment(context: Context): Map<String, String> {
        val packages = File(context.noBackupFilesDir, "youtubedl-android/packages")
        return mapOf(
            "LD_LIBRARY_PATH" to listOf(File(packages, "ffmpeg/usr/lib").path, File(packages, "python/usr/lib").path,
                System.getenv("LD_LIBRARY_PATH").orEmpty()).filter(String::isNotBlank).joinToString(":"),
            "SSL_CERT_FILE" to File(packages, "python/usr/etc/tls/cert.pem").path,
            "PATH" to System.getenv("PATH").orEmpty() + ":" + context.applicationInfo.nativeLibraryDir,
            "TMPDIR" to context.cacheDir.path,
        )
    }

    fun version(context: Context, tool: String): String = run(context, tool, listOf("-version")).toString(Charsets.UTF_8)

    fun verify(context: Context, file: File) {
        require(file.isFile && file.length() > 0) { "Downloaded media file is absent or empty" }
        val result = JSONObject(run(context, "ffprobe", listOf(
            "-v", "error", "-show_entries", "stream=codec_type:format=format_name", "-of", "json", file.path,
        )).toString(Charsets.UTF_8))
        val streams = result.optJSONArray("streams")
        val media = streams != null && (0 until streams.length()).any {
            streams.optJSONObject(it)?.optString("codec_type") in setOf("video", "audio")
        }
        val format = result.optJSONObject("format")?.optString("format_name").orEmpty()
        if (!media || format.isBlank() || format.split(',').any { it.endsWith("_pipe") || it in setOf("image2", "image2pipe", "gif", "apng") })
            throw EngineException("The reported result is not a valid video or audio container")
    }

    fun run(context: Context, tool: String, arguments: List<String>, maxBytes: Int = 256 * 1024, timeoutMs: Long = 15_000): ByteArray {
        require(tool in setOf("ffmpeg", "ffprobe"))
        val executable = File(context.applicationInfo.nativeLibraryDir, "lib$tool.so")
        if (!executable.isFile) throw EngineException("Bundled $tool is unavailable for this device")
        val builder = ProcessBuilder(listOf(executable.path) + arguments).redirectErrorStream(true)
        builder.environment().putAll(environment(context))
        val process = builder.start()
        val timedOut = AtomicBoolean(false)
        val deadline = deadlines.schedule({ timedOut.set(true); process.destroyForcibly() }, timeoutMs, TimeUnit.MILLISECONDS)
        try {
            val output = process.inputStream.use { input ->
                val bytes = ByteArrayOutputStream()
                val buffer = ByteArray(8192)
                while (true) {
                    val count = input.read(buffer)
                    if (count < 0) break
                    if (bytes.size().toLong() + count > maxBytes) throw IOException("$tool output exceeds its limit")
                    bytes.write(buffer, 0, count)
                }
                bytes.toByteArray()
            }
            val exit = process.waitFor()
            if (timedOut.get()) throw EngineException("$tool timed out")
            if (exit != 0) throw EngineException("$tool: " + output.toString(Charsets.UTF_8).take(2000))
            return output
        } finally {
            deadline.cancel(false)
            if (process.isAlive) { process.destroyForcibly(); process.waitFor(2, TimeUnit.SECONDS) }
        }
    }
}
