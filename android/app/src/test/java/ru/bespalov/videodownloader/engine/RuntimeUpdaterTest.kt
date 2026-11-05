package ru.bespalov.videodownloader.engine

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Dns
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.SocketPolicy
import okio.Buffer
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.net.Proxy
import java.net.InetAddress
import java.security.MessageDigest
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

class RuntimeUpdaterTest {
    @get:Rule val temporary = TemporaryFolder()
    private lateinit var server: MockWebServer
    private lateinit var target: File
    private val old = "2026.10.26"
    private val next = "2026.11.04"

    @Before fun prepare() {
        server = MockWebServer().apply { start() }
        target = File(temporary.newFolder("runtime"), "yt-dlp").apply { writeBytes(archive(old)) }
    }
    @After fun finish() { server.shutdown() }

    @Test fun verifiedUpdateIsAtomicAndSecondCheckDoesNotDownloadAgain() = runBlocking {
        val binary = archive(next)
        enqueueUpdate(binary)
        val updater = updater()
        assertEquals(next, updater.update())
        assertArrayEquals(binary, target.readBytes())
        assertEquals(next, RuntimeArchive.readVersion(target))
        server.enqueue(metadata())
        assertEquals(next, updater.update())
        assertEquals(4, server.requestCount)
        assertOnlyRuntimeRemains()
    }

    @Test fun manuallyInstalledNewerVersionIsNotDowngraded() = runBlocking {
        val newer = "$next.1"
        target.writeBytes(archive(newer))
        val before = target.readBytes()
        server.enqueue(metadata())
        assertEquals(newer, updater().update())
        assertArrayEquals(before, target.readBytes())
        assertEquals(1, server.requestCount)
    }

    @Test fun wrongChecksumPreservesPreviousRuntimeAndRemovesDownloads() = runBlocking {
        val before = target.readBytes()
        enqueueUpdate(archive(next), checksum = "0".repeat(64))
        val failure = runCatching { updater().update() }.exceptionOrNull()
        assertNotNull(failure)
        assertTrue(failure!!.message.orEmpty().contains("checksum"))
        assertArrayEquals(before, target.readBytes())
        assertOnlyRuntimeRemains()
    }

    @Test fun checksumDoesNotPermitInstallingArchiveWithWrongVersion() = runBlocking {
        val before = target.readBytes()
        enqueueUpdate(archive(old))
        val failure = runCatching { updater().update() }.exceptionOrNull()
        assertNotNull(failure)
        assertTrue(failure!!.message.orEmpty().contains("ZIP/version"))
        assertArrayEquals(before, target.readBytes())
        assertOnlyRuntimeRemains()
    }

    @Test fun ambiguousChecksumStopsBeforeDownloadingExecutable() = runBlocking {
        val before = target.readBytes()
        server.enqueue(metadata())
        server.enqueue(MockResponse().setBody("${"0".repeat(64)}  yt-dlp\n${"1".repeat(64)}  yt-dlp\n"))
        assertNotNull(runCatching { updater().update() }.exceptionOrNull())
        assertArrayEquals(before, target.readBytes())
        assertEquals(2, server.requestCount)
        assertOnlyRuntimeRemains()
    }

    @Test fun stalledApiHeadersReachOverallDeadlineAndNextUpdateStillWorks() = runBlocking {
        val before = target.readBytes()
        server.enqueue(MockResponse().setSocketPolicy(SocketPolicy.NO_RESPONSE))
        val start = System.nanoTime()
        val failure = runCatching { updater(timeoutMillis = 250).update() }.exceptionOrNull()
        assertTrue(failure is TimeoutCancellationException)
        assertTrue(TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - start) < 5_000)
        assertArrayEquals(before, target.readBytes())
        assertOnlyRuntimeRemains()
        enqueueUpdate(archive(next))
        assertEquals(next, updater().update())
    }

    @Test fun overallDeadlineAlsoCancelsStalledChecksumRequest() = runBlocking {
        val before = target.readBytes()
        server.enqueue(metadata())
        server.enqueue(MockResponse().setSocketPolicy(SocketPolicy.NO_RESPONSE))
        assertTrue(runCatching { updater(timeoutMillis = 300).update() }.exceptionOrNull() is TimeoutCancellationException)
        assertArrayEquals(before, target.readBytes())
        assertOnlyRuntimeRemains()
    }

    @Test fun blockedDnsCannotHoldEngineAdmissionAfterOverallDeadline() = runBlocking {
        val before = target.readBytes()
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        val client = OkHttpClient.Builder().proxy(Proxy.NO_PROXY).dns(object : Dns {
            override fun lookup(hostname: String): List<InetAddress> {
                entered.countDown()
                release.await(5, TimeUnit.SECONDS)
                return listOf(InetAddress.getByName("127.0.0.1"))
            }
        }).build()
        try {
            val updater = RuntimeUpdater(target, CancellableRuntimeHttp(client),
                "http://runtime-update.invalid:${server.port}/latest", timeoutMillis = 250)
            val start = System.nanoTime()
            val failure = runCatching { updater.update() }.exceptionOrNull()
            assertTrue("The fixture must enter the blocking platform resolver", entered.count == 0L)
            assertTrue(failure is TimeoutCancellationException)
            assertTrue("Cancellation must not wait for the platform DNS call", TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - start) < 2_000)
            assertEquals("The resolver is still blocked when cancellation returns", 1L, release.count)
            assertArrayEquals(before, target.readBytes())
            assertOnlyRuntimeRemains()
        } finally { release.countDown() }
    }

    @Test fun cancelDuringExecutableBodyClosesRequestBeforeRemovingTemporaryFile() = runBlocking {
        val before = target.readBytes()
        val binary = archive(next)
        server.enqueue(metadata())
        server.enqueue(MockResponse().setBody("${sha(binary)}  yt-dlp\n"))
        server.enqueue(MockResponse().setBody(Buffer().write(binary)).throttleBody(1, 1, TimeUnit.SECONDS))
        val operation = async(Dispatchers.Default) { updater().update() }
        repeat(3) { assertNotNull(withContext(Dispatchers.IO) { server.takeRequest(5, TimeUnit.SECONDS) }) }
        operation.cancelAndJoin()
        assertTrue(operation.isCancelled)
        assertArrayEquals(before, target.readBytes())
        assertOnlyRuntimeRemains()
        enqueueUpdate(binary)
        assertEquals(next, updater().update())
    }

    @Test fun cancellationAfterCopyBeforeCommitKeepsOldExecutable() {
        val before = target.readBytes()
        val binary = archive(next)
        var copied = false
        val source = object : ByteArrayInputStream(binary) {
            override fun close() { super.close(); copied = true }
        }
        val failure = runCatching {
            RuntimeArchive.installIfOlder(target, next, sha(binary), checkCancellation = {
                if (copied) throw CancellationException("Cancelled before runtime commit")
            }) { source }
        }.exceptionOrNull()
        assertTrue(failure is CancellationException)
        assertArrayEquals(before, target.readBytes())
        assertOnlyRuntimeRemains()
    }

    @Test fun chunkedResponseCannotWritePastByteLimitAndHttpClientCanBeReused() = runBlocking {
        val http = http()
        val file = File(temporary.root, "bounded-download")
        server.enqueue(MockResponse().setChunkedBody(Buffer().write(ByteArray(1024)), 31))
        assertNotNull(runCatching { http.download(server.url("/large").toString(), file, 128) }.exceptionOrNull())
        assertTrue(file.length() <= 128)
        server.enqueue(MockResponse().setChunkedBody("ok", 1))
        assertEquals("ok", http.bytes(server.url("/small").toString(), 128).toString(Charsets.UTF_8))
    }

    private fun updater(timeoutMillis: Long = 5_000) = RuntimeUpdater(
        target, http(), server.url("/latest").toString(), server.url("/download/").toString(), timeoutMillis,
    )
    private fun http() = CancellableRuntimeHttp(OkHttpClient.Builder()
        .proxy(Proxy.NO_PROXY) // The fixture server is on loopback, never a public destination.
        .connectTimeout(2, TimeUnit.SECONDS).readTimeout(2, TimeUnit.SECONDS)
        .callTimeout(3, TimeUnit.SECONDS).build())
    private fun metadata() = MockResponse().setBody("{\"tag_name\":\"$next\"}")
    private fun enqueueUpdate(binary: ByteArray, checksum: String = sha(binary)) {
        server.enqueue(metadata())
        server.enqueue(MockResponse().setBody("$checksum  yt-dlp\n"))
        server.enqueue(MockResponse().setBody(Buffer().write(binary)))
    }
    private fun assertOnlyRuntimeRemains() {
        assertEquals(listOf("yt-dlp"), target.parentFile!!.list()!!.sorted())
    }
    private fun archive(version: String): ByteArray = ByteArrayOutputStream().also { output ->
        ZipOutputStream(output).use { zip ->
            zip.putNextEntry(ZipEntry("yt_dlp/version.py"))
            zip.write("__version__ = '$version'\n".toByteArray())
            zip.closeEntry()
        }
    }.toByteArray()
    private fun sha(bytes: ByteArray) = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
}
