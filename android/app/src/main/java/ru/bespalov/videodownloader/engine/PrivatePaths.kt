package ru.bespalov.videodownloader.engine

import java.io.File
import java.nio.file.Files

/**
 * Android supplies private roots through aliases such as /data/user/0 and
 * /data/data. Trust those platform-provided ancestors, while refusing links or
 * traversal introduced inside a particular owned staging root.
 */
internal object PrivatePaths {
    fun childOfAny(roots: List<File>, file: File, allowRoot: Boolean = false): File {
        require(file.isAbsolute) { "A private path must be absolute" }
        val lexical = file.toPath().toAbsolutePath().normalize()
        val root = roots.firstOrNull { candidate ->
            lexical.startsWith(candidate.toPath().toAbsolutePath().normalize()) ||
                lexical.startsWith(candidate.canonicalFile.toPath())
        } ?: throw IllegalArgumentException("Unknown private staging directory")
        return child(root, file, allowRoot)
    }

    fun child(root: File, file: File, allowRoot: Boolean = false): File {
        require(root.isAbsolute && file.isAbsolute) { "A private path must be absolute" }
        val supplied = file.toPath()
        require(supplied.none { it.toString() == "." || it.toString() == ".." }) { "Private paths must not contain traversal" }
        val lexical = supplied.toAbsolutePath().normalize()
        val rootLexical = root.toPath().toAbsolutePath().normalize()
        require(!Files.isSymbolicLink(rootLexical)) { "The owned staging root is a symbolic link" }
        val rootCanonical = root.canonicalFile.toPath()
        val checkedRoot = when {
            lexical.startsWith(rootLexical) -> rootLexical
            lexical.startsWith(rootCanonical) -> rootCanonical
            else -> throw IllegalArgumentException("Path escaped its owned staging directory")
        }
        require(allowRoot || lexical != checkedRoot) { "The staging root is not an owned child file" }
        var current = lexical
        while (true) {
            require(!Files.isSymbolicLink(current)) { "Staging contains a symbolic link" }
            if (current == checkedRoot) break
            current = current.parent ?: throw IllegalArgumentException("Path escaped its owned staging directory")
        }
        val canonical = file.canonicalFile.toPath()
        require(canonical.startsWith(rootCanonical) && (allowRoot || canonical != rootCanonical)) {
            "Path escaped its owned staging directory"
        }
        return file
    }
}
