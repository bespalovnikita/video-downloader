package ru.bespalov.videodownloader.engine

import org.junit.Assert.*
import org.junit.Test
import java.io.File
import java.util.Collections
import java.util.concurrent.CountDownLatch
import java.util.concurrent.ExecutionException
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicBoolean

class BoundedProcessRunnerTest {
    @Test fun unterminatedStdoutAndNoisyStderrAreStoppedAtTheByteBudget() {
        for (mode in listOf("stdout", "stderr", "both")) {
            val failure = runCatching {
                BoundedProcessRunner.run(ProcessBuilder(ProcessFixture.command(mode)), ProcessSession(), 64 * 1024)
            }.exceptionOrNull()
            assertTrue("$mode must fail before retaining its entire output: $failure", failure is ProcessOutputLimitException)
        }
    }

    @Test fun aLineCannotGrowUntilTheLargerTotalBudget() {
        val failure = runCatching {
            BoundedProcessRunner.run(ProcessBuilder(ProcessFixture.command("stdout")), ProcessSession(),
                1024 * 1024, maxLineBytes = 4096, onLine = {})
        }.exceptionOrNull()
        assertTrue(failure is ProcessOutputLimitException)
        assertTrue(failure!!.message.orEmpty().contains("line"))
    }

    @Test fun byteChunksPreserveUtf8CrLfAndTheFinalUnterminatedLine() {
        val lines = Collections.synchronizedList(mutableListOf<String>())
        val result = BoundedProcessRunner.run(ProcessBuilder(ProcessFixture.command("unicode")), ProcessSession(),
            32 * 1024, maxLineBytes = 16 * 1024, onLine = lines::add)
        assertEquals(0, result.exitCode)
        assertEquals(ProcessFixture.text, result.out)
        assertEquals(listOf("x".repeat(8191) + "😀", "Русский", "tail"), lines)
    }

    @Test fun parallelStreamsNeverCallTheObserverConcurrently() {
        val active = AtomicInteger()
        val maximum = AtomicInteger()
        val lines = AtomicInteger()
        BoundedProcessRunner.run(ProcessBuilder(ProcessFixture.command("parallel-lines")), ProcessSession(),
            64 * 1024, onLine = {
                val current = active.incrementAndGet()
                maximum.updateAndGet { maxOf(it, current) }
                try { Thread.sleep(2); lines.incrementAndGet() }
                finally { active.decrementAndGet() }
            })
        assertEquals(100, lines.get())
        assertEquals("Both pipes drain independently, but the observer has one owner", 1, maximum.get())
    }

    @Test fun cancellationJoinsTheRealProcessAndReaders() {
        val session = ProcessSession()
        val ready = CountDownLatch(1)
        val executor = Executors.newSingleThreadExecutor()
        val pidFile = File.createTempFile("native-reader-", ".pid").apply { delete() }
        try {
            val future = executor.submit<NativeProcessResult> {
                BoundedProcessRunner.run(ProcessBuilder(ProcessFixture.command("wait", pidFile.path)), session,
                    64 * 1024, onLine = { if (it == "READY") ready.countDown() })
            }
            assertTrue(ready.await(10, TimeUnit.SECONDS))
            val pid = pidFile.readText().toLong()
            session.cancel()
            val error = runCatching { future.get(5, TimeUnit.SECONDS) }.exceptionOrNull()
            assertTrue(error is ExecutionException && error.cause is java.util.concurrent.CancellationException)
            assertFalse("The root must finish before cancellation returns", ProcessFixture.isAlive(pid))
        } finally {
            session.cancel(); executor.shutdownNow(); pidFile.delete()
        }
    }

    @Test fun cancelledOrReusedSessionsCannotLaunchAnotherProcess() {
        val marker = File.createTempFile("native-launch-", ".marker").apply { delete() }
        try {
            val cancelled = ProcessSession().also(ProcessSession::cancel)
            assertTrue(runCatching {
                BoundedProcessRunner.run(ProcessBuilder(ProcessFixture.command("marker", marker.path)), cancelled, 1024)
            }.exceptionOrNull() is java.util.concurrent.CancellationException)
            assertFalse(marker.exists())
            val once = ProcessSession()
            BoundedProcessRunner.run(ProcessBuilder(ProcessFixture.command("unicode")), once, 32 * 1024)
            assertTrue(runCatching {
                BoundedProcessRunner.run(ProcessBuilder(ProcessFixture.command("marker", marker.path)), once, 1024)
            }.isFailure)
            assertFalse(marker.exists())
        } finally { marker.delete() }
    }

    @Test fun cancellationBeforeTheLaunchHandshakeOnlyStopsItsRoot() {
        var groupSignals = 0
        val session = ProcessSession("unique-control:") { groupSignals++ }
        val executor = Executors.newSingleThreadExecutor()
        val pidFile = File.createTempFile("native-control-", ".pid").apply { delete() }
        try {
            val future = executor.submit<NativeProcessResult> {
                BoundedProcessRunner.run(ProcessBuilder(ProcessFixture.command("before-control", pidFile.path)), session, 4096)
            }
            val until = System.nanoTime() + TimeUnit.SECONDS.toNanos(10)
            while (!pidFile.isFile) {
                check(System.nanoTime() < until) { "Child did not start" }
                Thread.sleep(10)
            }
            val pid = pidFile.readText().toLong()
            session.cancel()
            val failure = runCatching { future.get(5, TimeUnit.SECONDS) }.exceptionOrNull()
            assertTrue(failure is ExecutionException && failure.cause is java.util.concurrent.CancellationException)
            assertEquals("An unconfirmed group must never receive a signal", 0, groupSignals)
            assertFalse(ProcessFixture.isAlive(pid))
        } finally { session.cancel(); executor.shutdownNow(); pidFile.delete() }
    }

    @Test fun anUnauthenticatedControlLineCannotChooseAProcessGroup() {
        var groupSignals = 0
        val session = ProcessSession("unique-control:") { groupSignals++ }
        assertTrue(runCatching {
            BoundedProcessRunner.run(ProcessBuilder(ProcessFixture.command("invalid-control")), session, 4096)
        }.exceptionOrNull() is java.io.IOException)
        assertEquals(0, groupSignals)
    }

    @Test fun aTransientGroupTerminationFailureCannotReleaseTheInvocation() {
        val attempts = AtomicInteger()
        val retrying = CountDownLatch(1)
        val permitStop = CountDownLatch(1)
        val confirmed = AtomicBoolean()
        val session = ProcessSession("controlled-launch:") {
            if (attempts.incrementAndGet() == 1) throw java.io.IOException("Transient group termination failure")
            retrying.countDown()
            check(permitStop.await(5, TimeUnit.SECONDS))
            confirmed.set(true)
        }
        val executor = Executors.newSingleThreadExecutor()
        try {
            val future = executor.submit<NativeProcessResult> {
                BoundedProcessRunner.run(ProcessBuilder(ProcessFixture.command("control-exit", "controlled-launch:")), session, 4096)
            }
            assertTrue(retrying.await(10, TimeUnit.SECONDS))
            assertFalse("The group has not confirmed its stop", confirmed.get())
            assertFalse("EOF/root exit must not release retained group ownership", future.isDone)
            permitStop.countDown()
            assertNotNull(runCatching { future.get(5, TimeUnit.SECONDS) }.exceptionOrNull())
            assertTrue(confirmed.get())
            assertTrue(attempts.get() >= 2)
        } finally { permitStop.countDown(); session.cancel(); executor.shutdownNow() }
    }

    @Test fun requestingCancellationDoesNotWaitForGroupInspectionOrJoin() {
        val inspecting = CountDownLatch(1)
        val permitStop = CountDownLatch(1)
        val session = ProcessSession("controlled-launch:") {
            inspecting.countDown()
            check(permitStop.await(5, TimeUnit.SECONDS))
        }
        val executor = Executors.newSingleThreadExecutor()
        try {
            val future = executor.submit<NativeProcessResult> {
                BoundedProcessRunner.run(ProcessBuilder(ProcessFixture.command("control-exit", "controlled-launch:")), session, 4096)
            }
            assertTrue(inspecting.await(10, TimeUnit.SECONDS))
            val start = System.nanoTime()
            session.cancel()
            assertTrue("Main only requests stop; it must not await group cleanup",
                TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - start) < 500)
            assertFalse("The IO owner must retain staging ownership until the join completes", future.isDone)
            permitStop.countDown()
            val error = runCatching { future.get(5, TimeUnit.SECONDS) }.exceptionOrNull()
            assertTrue(error is ExecutionException && error.cause is java.util.concurrent.CancellationException)
        } finally { permitStop.countDown(); session.cancel(); executor.shutdownNow() }
    }
}

/** A real JVM child keeps pipe allocation/cancellation tests independent from Android mocks. */
object ProcessFixture {
    val text = "x".repeat(8191) + "😀\r\nРусский\ntail"

    private fun currentPid(): Long = Class.forName("java.lang.ProcessHandle").getMethod("current").invoke(null).let {
        Class.forName("java.lang.ProcessHandle").getMethod("pid").invoke(it) as Long
    }

    fun isAlive(pid: Long): Boolean {
        val handle = Class.forName("java.lang.ProcessHandle").getMethod("of", java.lang.Long.TYPE).invoke(null, pid)
        val value = handle.javaClass.getMethod("orElse", Any::class.java).invoke(handle, null) ?: return false
        return Class.forName("java.lang.ProcessHandle").getMethod("isAlive").invoke(value) as Boolean
    }

    fun command(vararg arguments: String): List<String> {
        val separator = File.pathSeparator
        val classpath = listOf(ProcessFixture::class.java, kotlin.Unit::class.java)
            .map { File(checkNotNull(it.protectionDomain).codeSource.location.toURI()).path }.distinct().joinToString(separator)
        return listOf(File(System.getProperty("java.home"), "bin/java").path, "-cp", classpath,
            ProcessFixture::class.java.name) + arguments
    }

    @JvmStatic fun main(arguments: Array<String>) {
        when (arguments[0]) {
            "stdout", "stderr" -> {
                val output = if (arguments[0] == "stdout") System.out else System.err
                val chunk = ByteArray(8192) { 'x'.code.toByte() }
                while (!output.checkError()) { output.write(chunk); output.flush() }
            }
            "both" -> { System.out.write(ByteArray(40_000)); System.err.write(ByteArray(40_000)) }
            "unicode" -> text.toByteArray().forEach { System.out.write(it.toInt()); System.out.flush() }
            "parallel-lines" -> {
                val ready = CountDownLatch(2)
                val writers = listOf(System.out, System.err).map { output ->
                    Thread {
                        ready.countDown(); ready.await()
                        repeat(50) { output.println("line-$it"); output.flush() }
                    }.apply { start() }
                }
                writers.forEach(Thread::join)
            }
            "wait" -> {
                File(arguments[1]).writeText(currentPid().toString())
                println("READY"); System.out.flush(); Thread.sleep(60_000)
            }
            "before-control" -> {
                File(arguments[1]).writeText(currentPid().toString())
                Thread.sleep(60_000)
            }
            "invalid-control" -> { println("another-session:123"); System.out.flush(); Thread.sleep(60_000) }
            "control-exit" -> { println(arguments[1] + currentPid()); System.out.flush() }
            "marker" -> File(arguments[1]).writeText("started")
        }
    }
}
