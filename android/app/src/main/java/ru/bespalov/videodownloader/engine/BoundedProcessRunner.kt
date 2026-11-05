package ru.bespalov.videodownloader.engine

import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.InputStream
import java.util.concurrent.CancellationException
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference

internal class ProcessOutputLimitException(message: String) : IOException(message)

/** One invocation owns its root and, after an authenticated launch handshake, its process group. */
internal class ProcessSession(
    val controlPrefix: String? = null,
    private val signalGroup: (Int) -> Unit = {},
) {
    private val gate = Any()
    private val cleanupGate = Any()
    private var process: Process? = null
    private var group: Int? = null
    private var confirmed = false
    private var claimed = false
    private var ownerExited = false
    private val stopped = AtomicReference<Throwable?>()
    val failure: Throwable? get() = stopped.get()
    internal val hasPendingGroup: Boolean get() = synchronized(gate) { group != null }

    fun cancel() = stop(CancellationException("Request cancelled"))

    fun stop(reason: Throwable) {
        stopped.compareAndSet(null, reason)
        // Public cancellation may run on Main. It only marks the request and
        // wakes waitFor(); group inspection/join belongs to the IO owner.
        val root = synchronized(gate) { process }
        root?.let { runCatching { it.destroyForcibly() } }
    }

    internal fun claim() = synchronized(gate) {
        check(!claimed) { "A process session cannot be reused" }
        claimed = true
        throwIfStopped()
    }

    internal fun attach(value: Process) {
        synchronized(gate) { process = value }
        if (failure != null) runCatching { value.destroyForcibly() }
    }

    internal fun finish() = synchronized(gate) {
        check(group == null) { "A native group cannot be forgotten before termination is confirmed" }
        process = null
    }

    internal fun confirmGroup(value: Int) {
        val stopping = synchronized(gate) {
            require(controlPrefix != null && value > 1 && !confirmed) { "Invalid owned process group" }
            confirmed = true
            group = value
            failure != null || ownerExited
        }
        if (stopping) terminateGroup()
    }

    internal fun rootExited() {
        synchronized(gate) { ownerExited = true }
        terminateGroup()
    }

    internal fun terminateGroup(): Unit = synchronized(cleanupGate) {
        val owned = synchronized(gate) { group } ?: return@synchronized
        try {
            signalGroup(owned)
            // Confirmation cannot outlive termination and risk PID reuse.
            synchronized(gate) { if (group == owned) group = null }
        } catch (error: Throwable) { stopped.compareAndSet(null, error) }
        Unit
    }

    internal fun throwIfStopped() { failure?.let { throw it } }
}

internal data class NativeProcessResult(val exitCode: Int, val out: String, val err: String)

/** Byte budgets are checked before allocation/append, independently of newline and stream origin. */
internal object BoundedProcessRunner {
    fun run(
        builder: ProcessBuilder,
        session: ProcessSession,
        outputLimit: Int,
        maxLineBytes: Int = minOf(outputLimit, 1024 * 1024),
        onLine: ((String) -> Unit)? = null,
    ): NativeProcessResult {
        require(outputLimit > 0 && maxLineBytes > 0)
        session.claim()
        val process = builder.redirectErrorStream(false).start()
        session.attach(process)
        runCatching { process.outputStream.close() }
        val total = AtomicLong()
        val callbackGate = Any()
        val stdout = ByteArrayOutputStream()
        val stderr = ByteArrayOutputStream()
        fun read(stream: InputStream, output: ByteArrayOutputStream, control: Boolean) {
            try {
                stream.use { input ->
                    if (control && session.controlPrefix != null) readControl(input, session)
                    val line = onLine?.let { ByteArrayOutputStream() }
                    val chunk = ByteArray(8192)
                    while (true) {
                        val count = input.read(chunk)
                        if (count < 0) break
                        if (total.addAndGet(count.toLong()) > outputLimit)
                            throw ProcessOutputLimitException("yt-dlp output exceeds the configured limit")
                        output.write(chunk, 0, count)
                        if (line != null) for (index in 0 until count) {
                            val byte = chunk[index].toInt() and 0xff
                            if (byte == 10 || byte == 13) {
                                if (line.size() > 0) {
                                    synchronized(callbackGate) { onLine!!.invoke(line.toString(Charsets.UTF_8.name())) }
                                    line.reset()
                                }
                            } else {
                                if (line.size() >= maxLineBytes)
                                    throw ProcessOutputLimitException("yt-dlp output line exceeds the configured limit")
                                line.write(byte)
                            }
                        }
                    }
                    if (line != null && line.size() > 0)
                        synchronized(callbackGate) { onLine!!.invoke(line.toString(Charsets.UTF_8.name())) }
                }
            } catch (error: Throwable) { session.stop(error) }
        }
        val readers = listOf(
            Thread({ read(process.inputStream, stdout, true) }, "yt-dlp-stdout"),
            Thread({ read(process.errorStream, stderr, false) }, "yt-dlp-stderr"),
        ).onEach { it.isDaemon = true }
        var interrupted = false
        try {
            readers.forEach(Thread::start)
            val exit = process.waitFor()
            // An owner can exit while a child uses separate pipes or DEVNULL.
            // EOF of our two pipes does not prove that staging has no writer.
            session.rootExited()
            // Even when the root exits first, inherited pipe handles remain owned
            // by its group. Cancellation still kills that group before these joins.
            while (readers.any(Thread::isAlive) || session.hasPendingGroup) {
                // A transient platform failure is diagnostic, never permission
                // to forget an owned group and let a caller delete its staging.
                session.rootExited()
                readers.forEach { it.join(20) }
                if (session.hasPendingGroup) Thread.sleep(20)
            }
            session.throwIfStopped()
            return NativeProcessResult(exit, stdout.toString(Charsets.UTF_8.name()), stderr.toString(Charsets.UTF_8.name()))
        } catch (error: InterruptedException) {
            interrupted = true
            session.stop(error)
            throw error
        } finally {
            // A cancelled coroutine must not release a staging directory while a
            // reader or native child can still write to it.
            if (process.isAlive || readers.any(Thread::isAlive) || session.hasPendingGroup)
                session.stop(IOException("Native process did not finish"))
            while (process.isAlive || readers.any(Thread::isAlive) || session.hasPendingGroup) {
                session.stop(IOException("Native process did not finish"))
                session.terminateGroup()
                try {
                    process.waitFor(20, TimeUnit.MILLISECONDS)
                    readers.forEach { it.join(20) }
                    if (session.hasPendingGroup) Thread.sleep(20)
                }
                catch (_: InterruptedException) { interrupted = true }
            }
            session.finish()
            if (interrupted) Thread.currentThread().interrupt()
        }
    }

    private fun readControl(input: InputStream, session: ProcessSession) {
        val line = ByteArrayOutputStream()
        while (true) {
            val byte = input.read()
            if (byte < 0) throw IOException("The native runtime did not confirm its process group")
            if (byte == 10) break
            if (line.size() >= 192) throw IOException("Invalid native runtime launch handshake")
            line.write(byte)
        }
        val text = line.toString(Charsets.US_ASCII.name())
        val prefix = checkNotNull(session.controlPrefix)
        if (!text.startsWith(prefix)) throw IOException("Invalid native runtime launch handshake")
        val group = text.substring(prefix.length).toIntOrNull()
            ?: throw IOException("Invalid native runtime process group")
        session.confirmGroup(group)
    }
}
