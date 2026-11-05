package ru.bespalov.videodownloader.engine

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import java.io.File
import java.io.IOException

/** Fetches the official stable ZIP and checksum without invoking the wrapper's blocking updater. */
internal class RuntimeUpdater(
    private val target: File,
    private val http: CancellableRuntimeHttp = CancellableRuntimeHttp(),
    private val latestUrl: String = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest",
    private val downloadBaseUrl: String = "https://github.com/yt-dlp/yt-dlp/releases/download/",
    private val timeoutMillis: Long = 120_000,
) {
    suspend fun update(): String = withTimeout(timeoutMillis) {
        val metadata = http.bytes(latestUrl, MAX_METADATA_BYTES).toString(Charsets.UTF_8)
        val version = Json.parseToJsonElement(metadata).jsonObject["tag_name"]?.jsonPrimitive?.contentOrNull
            ?: throw IOException("Runtime update metadata has no version")
        // Validate the date/version before constructing a release URL. In
        // production asset locations always remain in the official repository.
        RuntimeArchive.compareVersions(version, version)
        val current = withContext(Dispatchers.IO) { RuntimeArchive.readVersion(target) }
        if (current != null && RuntimeArchive.compareVersions(current, version) >= 0) return@withTimeout current
        val checksums = http.bytes("${downloadBaseUrl.trimEnd('/')}/$version/SHA2-256SUMS", MAX_CHECKSUM_BYTES)
            .toString(Charsets.UTF_8)
        val matches = checksums.lineSequence().mapNotNull { CHECKSUM_LINE.matchEntire(it.trim())?.groupValues?.get(1) }.toList()
        if (matches.size != 1) throw IOException("Runtime update checksum is missing or ambiguous")
        withContext(Dispatchers.IO) {
            val parent = requireNotNull(target.parentFile)
            require(parent.isDirectory) { "Initialize the private runtime directory before updating" }
            // Keep creation and cleanup inside one IO scope. Returning a newly
            // created File across a cancellable dispatcher boundary could lose
            // its reference before a caller's finally block is installed.
            val temporary = File.createTempFile(".runtime-download-", ".tmp", parent)
            try {
                val context = currentCoroutineContext()
                http.download("${downloadBaseUrl.trimEnd('/')}/$version/yt-dlp", temporary, MAX_BINARY_BYTES)
                context.ensureActive()
                RuntimeArchive.installIfOlder(target, version, matches.single(), checkCancellation = { context.ensureActive() }) {
                    temporary.inputStream()
                }
            } finally { temporary.delete() }
        }
    }

    companion object {
        private const val MAX_METADATA_BYTES = 1024L * 1024
        private const val MAX_CHECKSUM_BYTES = 256L * 1024
        private const val MAX_BINARY_BYTES = 32L * 1024 * 1024
        private val CHECKSUM_LINE = Regex("^([a-fA-F0-9]{64})[ \\t]+\\*?yt-dlp$")
    }
}
