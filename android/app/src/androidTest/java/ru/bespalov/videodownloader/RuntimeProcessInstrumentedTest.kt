package ru.bespalov.videodownloader

import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import ru.bespalov.videodownloader.engine.AndroidRuntimeProcess
import ru.bespalov.videodownloader.engine.AndroidYtDlp
import ru.bespalov.videodownloader.engine.BoundedProcessRunner
import ru.bespalov.videodownloader.engine.NativeProcessResult
import ru.bespalov.videodownloader.engine.ProcessOutputLimitException
import ru.bespalov.videodownloader.engine.ProcessSession
import ru.bespalov.videodownloader.engine.RuntimeUpdater
import java.io.Closeable
import java.io.File
import java.net.ServerSocket
import java.net.Socket
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.ExecutionException
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/** Actual APK Python/OS pipes and process groups, not callback-shaped mocks. */
class RuntimeProcessInstrumentedTest {
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext

    @Test fun realNativePipesBoundUnterminatedStdoutAndStderr() = runBlocking<Unit> {
        AndroidYtDlp(context).initialize()
        val runtime = AndroidRuntimeProcess(context)
        val executor = Executors.newSingleThreadExecutor()
        try {
            for (descriptor in listOf(1, 2)) {
                val session = runtime.session()
                try {
                    val script = "import os\nwhile True: os.write($descriptor, b'x' * 8192)"
                    val future = executor.submit<NativeProcessResult> {
                        BoundedProcessRunner.run(runtime.pythonBuilder(session, listOf("-c", script)), session, 32 * 1024)
                    }
                    val error = runCatching { future.get(15, TimeUnit.SECONDS) }.exceptionOrNull()
                    assertTrue("descriptor=$descriptor must be capped: $error",
                        error is ExecutionException && error.cause is ProcessOutputLimitException)
                } finally { session.cancel() }
            }
        } finally { executor.shutdownNow() }
    }

    @Test fun launchControlIsRemovedAndNativeUtf8CrLfIsPreserved() = runBlocking<Unit> {
        AndroidYtDlp(context).initialize()
        val runtime = AndroidRuntimeProcess(context)
        val session = runtime.session()
        val lines = mutableListOf<String>()
        val script = "import os\nos.write(1, ('Привет 😀' + '\\r\\n' + 'tail').encode('utf-8'))"
        val result = BoundedProcessRunner.run(runtime.pythonBuilder(session, listOf("-c", script)), session,
            4096, onLine = { lines.add(it) })
        assertEquals(0, result.exitCode)
        assertEquals("Привет 😀\r\ntail", result.out)
        assertEquals(listOf("Привет 😀", "tail"), lines)
        assertFalse(result.out.contains("VD_PROCESS_"))
    }

    @Test fun cancellationJoinsItsChildAndLeavesAnotherGroupRunning() = runBlocking<Unit> {
        AndroidYtDlp(context).initialize()
        val runtime = AndroidRuntimeProcess(context)
        val executor = Executors.newFixedThreadPool(2)
        val owned = runtime.session()
        val other = runtime.session()
        val folder = File(context.cacheDir, "process-groups-${UUID.randomUUID()}").apply { mkdirs() }
        val first = File(folder, "first")
        val second = File(folder, "second")
        fun heartbeat(file: File) = "import time\nf=open(${pythonString(file.path)},'ab',buffering=0)\nwhile True:\n f.write(b'x')\n time.sleep(.03)"
        try {
            val child = heartbeat(first)
            val rootExit = "import subprocess,sys,time\nsubprocess.Popen([sys.executable,'-c',${pythonString(child)}])\ntime.sleep(60)"
            val ownedFuture = executor.submit<NativeProcessResult> {
                BoundedProcessRunner.run(runtime.pythonBuilder(owned, listOf("-c", rootExit)), owned, 4096)
            }
            val otherFuture = executor.submit<NativeProcessResult> {
                BoundedProcessRunner.run(runtime.pythonBuilder(other, listOf("-c", heartbeat(second))), other, 4096)
            }
            await { first.length() > 0 && second.length() > 0 }
            Thread.sleep(200)
            assertFalse(ownedFuture.isDone)
            owned.cancel()
            val error = runCatching { ownedFuture.get(5, TimeUnit.SECONDS) }.exceptionOrNull()
            assertTrue(error is ExecutionException && error.cause is java.util.concurrent.CancellationException)
            val stopped = first.length()
            val running = second.length()
            Thread.sleep(250)
            assertEquals("A joined cancellation cannot leave a child writing", stopped, first.length())
            assertTrue("Another invocation must retain its own group", second.length() > running)
            assertFalse(otherFuture.isDone)
            other.cancel()
            runCatching { otherFuture.get(5, TimeUnit.SECONDS) }
        } finally {
            owned.cancel(); other.cancel(); executor.shutdownNow()
            executor.awaitTermination(5, TimeUnit.SECONDS)
            folder.deleteRecursively()
        }
    }

    @Test fun normalAndFailedOwnersAlsoStopChildrenWithIndependentPipes() = runBlocking<Unit> {
        AndroidYtDlp(context).initialize()
        val runtime = AndroidRuntimeProcess(context)
        val folder = File(context.cacheDir, "exited-process-groups-${UUID.randomUUID()}").apply { mkdirs() }
        try {
            for (exit in listOf(0, 1)) {
                val marker = File(folder, "child-$exit")
                val child = "import time\nf=open(${pythonString(marker.path)},'ab',buffering=0)\nwhile True:\n f.write(b'x')\n time.sleep(.02)"
                val root = "import subprocess,sys,time,os\nsubprocess.Popen([sys.executable,'-c',${pythonString(child)}],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)\n" +
                    "while not os.path.exists(${pythonString(marker.path)}) or os.path.getsize(${pythonString(marker.path)}) == 0: time.sleep(.01)\nsys.exit($exit)"
                val session = runtime.session()
                try {
                    assertEquals(exit, BoundedProcessRunner.run(runtime.pythonBuilder(session, listOf("-c", root)), session, 4096).exitCode)
                    val stopped = marker.length()
                    assertTrue(stopped > 0)
                    Thread.sleep(200)
                    assertEquals("No native writer may outlive a completed owner", stopped, marker.length())
                } finally { session.cancel() }
            }
        } finally { folder.deleteRecursively() }
    }

    @Test fun cancelledUpdateClosesHttpBeforeReleasingAdmissionAndPreservesActualVersion() = runBlocking<Unit> {
        StalledUpdateServer().use { server ->
            val engine = AndroidYtDlp(context) { target ->
                RuntimeUpdater(target, latestUrl = server.url, timeoutMillis = 60_000).update()
            }
            val previousVersion = engine.version()
            val update = async(Dispatchers.IO) { engine.update() }
            try {
                assertTrue(server.requested.await(10, TimeUnit.SECONDS))
                val refused = runCatching { engine.version() }.exceptionOrNull()
                assertTrue("A running updater must hold engine admission", refused?.message.orEmpty().contains("обновляется"))
                update.cancelAndJoin()
                assertTrue("Cancellation must close the real HTTP connection", server.disconnected.await(5, TimeUnit.SECONDS))
                assertEquals(previousVersion, engine.version())
            } finally { update.cancelAndJoin() }
        }
    }

    private fun pythonString(value: String): String = "'" + value.replace("\\", "\\\\").replace("'", "\\'")
        .replace("\n", "\\n").replace("\r", "\\r") + "'"

    private fun await(condition: () -> Boolean) {
        val until = System.nanoTime() + TimeUnit.SECONDS.toNanos(10)
        while (!condition()) {
            check(System.nanoTime() < until) { "Native process fixture did not become ready" }
            Thread.sleep(20)
        }
    }

    private class StalledUpdateServer : Closeable {
        private val listener = ServerSocket(0, 1, java.net.InetAddress.getByName("127.0.0.1"))
        @Volatile private var socket: Socket? = null
        val requested = CountDownLatch(1)
        val disconnected = CountDownLatch(1)
        val url = "http://127.0.0.1:${listener.localPort}/latest"
        private val thread = Thread {
            try {
                listener.accept().use { connection ->
                    socket = connection
                    val input = connection.getInputStream()
                    var suffix = ""
                    while (!suffix.endsWith("\r\n\r\n")) {
                        val next = input.read()
                        if (next < 0) return@Thread
                        suffix = (suffix + next.toChar()).takeLast(4)
                    }
                    requested.countDown()
                    // Deliberately withhold response headers until Call.cancel closes it.
                    while (input.read() >= 0) { }
                }
            } catch (_: Exception) { }
            finally { disconnected.countDown() }
        }.apply { isDaemon = true; start() }
        override fun close() {
            socket?.close(); listener.close(); thread.join(2000)
        }
    }
}
