package dev.spyglass.rider.hub

import java.io.IOException
import java.nio.file.FileVisitResult
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.SimpleFileVisitor
import java.nio.file.attribute.BasicFileAttributes
import java.util.Locale

/** Finds project files on disk (independent of IDE indexes, which Rider's frontend does not fill for every file). */
object ProjectFiles {
    private val skipped = setOf("bin", "obj", "node_modules", ".git", ".idea", ".vs", ".vscode", "packages", "artifacts")

    /** Files under [root] whose extension is in [extensions] (lower case, no dot), skipping build output; at most [limit]. */
    fun find(root: Path, extensions: Set<String>, limit: Int = 50, maxDepth: Int = 8): List<Path> {
        val found = ArrayList<Path>()
        if (!Files.isDirectory(root)) return found
        try {
            Files.walkFileTree(root, emptySet(), maxDepth, object : SimpleFileVisitor<Path>() {
                override fun preVisitDirectory(dir: Path, attrs: BasicFileAttributes): FileVisitResult =
                    if (dir != root && dir.fileName.toString().let { it in skipped || it.startsWith(".") }) FileVisitResult.SKIP_SUBTREE
                    else FileVisitResult.CONTINUE

                override fun visitFile(file: Path, attrs: BasicFileAttributes): FileVisitResult {
                    val name = file.fileName.toString()
                    val ext = name.substringAfterLast('.', "").lowercase(Locale.ROOT)
                    if (attrs.isRegularFile && ext in extensions) found.add(file)
                    return if (found.size >= limit) FileVisitResult.TERMINATE else FileVisitResult.CONTINUE
                }

                override fun visitFileFailed(file: Path, exc: IOException): FileVisitResult = FileVisitResult.CONTINUE
            })
        } catch (_: IOException) {
        }
        return found.sorted()
    }
}
