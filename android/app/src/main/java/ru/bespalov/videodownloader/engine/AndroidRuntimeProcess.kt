package ru.bespalov.videodownloader.engine

import android.content.Context
import android.system.Os
import android.system.OsConstants
import android.system.ErrnoException
import com.yausername.youtubedl_android.YoutubeDLRequest
import java.io.File
import java.io.IOException
import java.util.concurrent.TimeUnit
import java.util.UUID

/** Public APK/private-runtime paths used by the pinned youtubedl-android 0.18.1 initializer. */
internal class AndroidRuntimeProcess(private val context: Context) {
    private val binaries get() = File(context.applicationInfo.nativeLibraryDir)
    private val packages get() = File(context.noBackupFilesDir, "youtubedl-android/packages")
    private val python get() = File(binaries, "libpython.so")
    private val pythonHome get() = File(packages, "python/usr")
    private val archive get() = File(context.noBackupFilesDir, "youtubedl-android/yt-dlp/yt-dlp")

    fun session(): ProcessSession = ProcessSession("\u001eVD_PROCESS_${UUID.randomUUID()}:") { group ->
        // The nonce-confirmed group was created by our launcher with setsid().
        // A negative PID addresses that group; never signal the app's own group.
        terminateGroup(group)
    }

    fun builder(request: YoutubeDLRequest, session: ProcessSession): ProcessBuilder {
        val arguments = request.buildCommand().toMutableList()
        if (!request.hasOption("--cache-dir")) arguments.addAll(0, listOf("--no-cache-dir"))
        arguments.addAll(0, listOf("--js-runtimes", "quickjs:${File(binaries, "libqjs.so").path}",
            "--ffmpeg-location", File(binaries, "libffmpeg.so").path))
        return pythonBuilder(session, listOf(archive.path) + arguments)
    }

    internal fun pythonBuilder(session: ProcessSession, arguments: List<String>): ProcessBuilder {
        require(session.controlPrefix != null)
        check(python.isFile) { "Bundled Python is unavailable for this device" }
        val builder = ProcessBuilder(listOf(python.path, "-c", LAUNCHER, session.controlPrefix, python.path) + arguments)
        builder.environment().putAll(NativeMedia.environment(context))
        builder.environment()["LD_LIBRARY_PATH"] = listOf("python", "ffmpeg", "aria2c")
            .joinToString(":") { File(packages, "$it/usr/lib").path }
        builder.environment()["PYTHONHOME"] = pythonHome.path
        builder.environment()["HOME"] = pythonHome.path
        return builder
    }

    companion object {
        private fun terminateGroup(group: Int) {
            try { Os.kill(-group, OsConstants.SIGKILL) }
            catch (error: ErrnoException) { if (error.errno == OsConstants.ESRCH) return else throw error }
            val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(2)
            while (true) {
                // A reaped owner does not imply its ordinary children have exited.
                // Zombies cannot hold files or write staging; do not wait for an
                // unrelated OS reaper to collect those already terminated tasks.
                val states = File("/proc").listFiles().orEmpty().asSequence()
                    .filter { it.name.isNotEmpty() && it.name.all(Char::isDigit) }
                    .mapNotNull { directory -> runCatching {
                        val stat = File(directory, "stat").bufferedReader().use { it.readLine() }.orEmpty()
                        val fields = stat.substring(stat.lastIndexOf(") ") + 2).split(' ')
                        if (fields.size >= 4 && fields[2].toIntOrNull() == group && fields[3].toIntOrNull() == group)
                            fields[0] else null
                    }.getOrNull() }.toList()
                if (states.isNotEmpty() && states.none { it !in setOf("Z", "X") }) return
                try { Os.kill(-group, 0) }
                catch (error: ErrnoException) { if (error.errno == OsConstants.ESRCH) return else throw error }
                if (System.nanoTime() >= deadline) throw IOException("Cannot confirm native process group termination")
                Thread.sleep(10)
            }
        }

        // No shell command strings or user arguments are interpolated into code.
        // The launcher's only control message precedes exec and untrusted output.
        private val LAUNCHER = """
            import os, sys
            os.setsid()
            os.write(1, (sys.argv[1] + str(os.getpid()) + '\n').encode('ascii'))
            os.execv(sys.argv[2], [sys.argv[2]] + sys.argv[3:])
        """.trimIndent()
    }
}
