package ru.bespalov.videodownloader.engine

import java.io.File
import java.nio.file.Files

/** A killed aria2c can leave a sparse part before it writes its control file. */
internal object Aria2Stage {
    private const val MARKER = ".vd-aria2-incomplete"

    fun prepare(stage: File, useAria2c: Boolean) {
        val marker = PrivatePaths.child(stage, File(stage, MARKER))
        if (marker.isFile) {
            stage.walkTopDown().onEnter { directory ->
                PrivatePaths.child(stage, directory, allowRoot = true)
                true
            }.filter { it.isFile && isIncomplete(it.name) }.forEach { part ->
                Files.deleteIfExists(PrivatePaths.child(stage, part).toPath())
            }
            Files.deleteIfExists(marker.toPath())
        }
        if (useAria2c) Files.createFile(marker.toPath())
    }

    fun complete(stage: File) {
        Files.deleteIfExists(PrivatePaths.child(stage, File(stage, MARKER)).toPath())
    }

    private fun isIncomplete(name: String) = name.endsWith(".part") || name.endsWith(".part.aria2") ||
        name.endsWith(".ytdl") || ".part-Frag" in name
}
