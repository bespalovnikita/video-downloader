package ru.bespalov.videodownloader.engine

import org.junit.Assert.*
import org.junit.Assume.assumeNoException
import org.junit.Test
import java.io.File
import java.io.IOException
import java.nio.file.Files

class PrivatePathsTest {
    private fun symlink(link: File, target: File) {
        try { Files.createSymbolicLink(link.toPath(), target.toPath()) }
        catch (unsupported: UnsupportedOperationException) { assumeNoException("This filesystem does not support symbolic links", unsupported) }
        catch (denied: IOException) { assumeNoException("This machine does not permit creating symbolic links", denied) }
    }

    @Test fun trustedContextAncestorAliasAllowsBothPlatformPathSpellings() {
        val directory = Files.createTempDirectory("vd-private-alias").toFile()
        try {
            val real = File(directory, "application").apply { mkdirs() }
            val alias = File(directory, "context-path")
            symlink(alias, real)
            val root = File(alias, "files/staging").apply { mkdirs() }
            val stage = File(root, "run/task").apply { mkdirs() }
            val media = File(stage, "video.mp4").apply { writeText("owned") }
            assertEquals(stage, PrivatePaths.childOfAny(listOf(root), stage))
            assertEquals(stage.canonicalFile, PrivatePaths.childOfAny(listOf(root), stage.canonicalFile))
            assertEquals(media, PrivatePaths.child(stage, media))
            assertEquals(media.canonicalFile, PrivatePaths.child(stage, media.canonicalFile))
            // Nonexistent children are also valid before yt-dlp creates them.
            assertEquals(File(stage, "new.mp4"), PrivatePaths.child(stage, File(stage, "new.mp4")))
        } finally { directory.deleteRecursively() }
    }

    @Test fun internalSymlinksAreRejectedEvenWhenTheirTargetRemainsInsideOwnedRoot() {
        val directory = Files.createTempDirectory("vd-private-inner-link").toFile()
        try {
            val root = File(directory, "staging").apply { mkdirs() }
            val target = File(root, "real").apply { mkdirs() }
            val media = File(target, "owned.mp4").apply { writeText("owned") }
            val link = File(root, "redirect")
            symlink(link, target)
            assertThrows(IllegalArgumentException::class.java) { PrivatePaths.child(root, File(link, "owned.mp4")) }
            val fileLink = File(root, "linked.mp4")
            symlink(fileLink, media)
            assertThrows(IllegalArgumentException::class.java) { PrivatePaths.child(root, fileLink) }
            assertEquals("owned", media.readText())
        } finally { directory.deleteRecursively() }
    }

    @Test fun traversalRootAndSiblingPrefixCannotBecomeOwnedChildren() {
        val directory = Files.createTempDirectory("vd-private-boundary").toFile()
        try {
            val root = File(directory, "staging").apply { mkdirs() }
            File(root, "inside").mkdirs()
            assertThrows(IllegalArgumentException::class.java) { PrivatePaths.child(root, File(root, "inside/../video.mp4")) }
            assertThrows(IllegalArgumentException::class.java) { PrivatePaths.child(root, File(root, "./video.mp4")) }
            assertThrows(IllegalArgumentException::class.java) { PrivatePaths.child(root, root) }
            assertThrows(IllegalArgumentException::class.java) { PrivatePaths.child(root, File(directory, "staging-other/video.mp4")) }
            assertThrows(IllegalArgumentException::class.java) { PrivatePaths.child(root, File("relative.mp4")) }
            assertEquals(root, PrivatePaths.child(root, root, allowRoot = true))
        } finally { directory.deleteRecursively() }
    }

    @Test fun ownedRootItselfCannotBeRedirected() {
        val directory = Files.createTempDirectory("vd-private-root-link").toFile()
        try {
            val actual = File(directory, "actual").apply { mkdirs() }
            val root = File(directory, "staging")
            symlink(root, actual)
            assertThrows(IllegalArgumentException::class.java) { PrivatePaths.child(root, File(root, "task/video.mp4")) }
            assertThrows(IllegalArgumentException::class.java) { PrivatePaths.childOfAny(listOf(root), File(root, "task/video.mp4")) }
        } finally { directory.deleteRecursively() }
    }
}
