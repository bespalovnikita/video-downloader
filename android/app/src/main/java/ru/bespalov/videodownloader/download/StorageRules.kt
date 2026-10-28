package ru.bespalov.videodownloader.download

import java.io.File
import java.nio.file.Files
import java.util.UUID

/** Paths and names are checked independently of a storage provider. */
internal object StorageRules {
    private val mediaExtensions = setOf("mp4", "mkv", "webm", "mp3", "m4a", "mov", "aac", "flac", "ogg", "opus", "wav", "avi", "m4v")

    fun id(value: String): String = UUID.fromString(value).toString().also {
        require(it.equals(value, ignoreCase = true)) { "Invalid task identifier" }
    }

    fun components(value: String): List<String> = value.replace('\\', '/').split('/').filter(String::isNotBlank).also { parts ->
        require(parts.none { it == "." || it == ".." || it.contains('\u0000') || it.contains(':') }) { "Unsafe output path" }
    }

    fun stage(root: File, taskId: String, runId: String, previous: String): File {
        val task = id(taskId)
        val selected = if (previous.isBlank()) File(root, "${id(runId)}/$task") else File(previous)
        val relative = root.toPath().toAbsolutePath().normalize().relativize(selected.toPath().toAbsolutePath().normalize())
        require(relative.nameCount == 2 && relative.getName(1).toString().equals(task, true)) { "Unknown staging directory" }
        id(relative.getName(0).toString())
        child(root, selected)
        return selected
    }

    fun child(root: File, file: File): File {
        val rootPath = root.toPath().toAbsolutePath().normalize()
        val filePath = file.toPath().toAbsolutePath().normalize()
        require(filePath != rootPath && filePath.startsWith(rootPath)) { "Path leaves the owned staging directory" }
        var current = filePath
        while (current.startsWith(rootPath)) {
            require(!Files.isSymbolicLink(current)) { "Symbolic links are not allowed in staging" }
            if (current == rootPath) break
            current = current.parent ?: break
        }
        require(file.canonicalFile.toPath().startsWith(root.canonicalFile.toPath())) { "Staging path was redirected" }
        return file
    }

    fun batchNames(relativeNames: List<String>, primary: String, copy: Int): List<String> {
        require(copy in 1..10_000)
        val normalized = relativeNames.map { components(it).joinToString("/") }
        require(normalized.distinct().size == normalized.size && normalized.none(String::isEmpty)) { "Duplicate or empty output name" }
        if (copy == 1) return normalized
        val anchors = normalized.filter { it.substringAfterLast('.').lowercase() in mediaExtensions }
            .map { it.substringBeforeLast('.') }.plus(primary.substringBeforeLast('.')).sortedByDescending(String::length)
        return normalized.map { name ->
            val anchor = anchors.firstOrNull { name.startsWith("$it.") }
            if (anchor != null) "$anchor ($copy)" + name.removePrefix(anchor)
            else {
                val slash = name.lastIndexOf('/')
                val dot = name.lastIndexOf('.').takeIf { it > slash + 1 } ?: name.length
                name.substring(0, dot) + " ($copy)" + name.substring(dot)
            }
        }
    }
}
