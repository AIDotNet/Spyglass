// A private copy of the app's build output for the previewer to run from. IntelliJ-independent.
// On Windows a running process locks the assemblies it loaded, so a previewer running from bin/ makes the next build fail with
// MSB3021/MSB3027 and can never see the rebuild. One copy per project window; a restart copies only what changed. Other
// platforms' runtimes/ folders are left out: they are most of a typical Avalonia output and the previewer never loads them.
package dev.spyglass.rider.preview

import java.io.IOException
import java.nio.file.Files
import java.nio.file.NoSuchFileException
import java.nio.file.Path
import java.nio.file.StandardCopyOption
import java.util.Comparator
import java.util.Locale
import java.util.UUID
import kotlin.io.path.name

class PreviewShadowCopy(
    private val root: Path = Path.of(System.getProperty("java.io.tmpdir"), "spyglass-preview"),
) {
    private val directory: Path = root.resolve(UUID.randomUUID().toString().replace("-", ""))
    private var purged = false

    /**
     * Brings the copy up to date and returns the project with its paths in the copy. The build may still be writing (and a
     * stopped previewer may still hold its files), so locked files are retried for a few seconds.
     */
    @Synchronized
    fun sync(project: PreviewProject): PreviewProject {
        val source = project.targetPath.toAbsolutePath().normalize().parent
        if (!Files.isDirectory(source)) throw NoSuchFileException(source.toString())
        purgeOrphans()
        var attempt = 1
        while (true) {
            try {
                val expected = HashSet<Path>()
                copy(source, directory, isRoot = true, inRuntimes = false, expected)
                removeOthers(expected)
                break
            } catch (e: IOException) {
                if (attempt++ >= ATTEMPTS || !Files.isDirectory(source)) throw e
                Thread.sleep(RETRY_MS)
            }
        }
        return project.copy(
            targetPath = inCopy(source, project.targetPath),
            runtimeConfig = inCopy(source, project.runtimeConfig),
            depsFile = inCopy(source, project.depsFile),
        )
    }

    /** Removes the copy. Best effort: a previewer that is still exiting may hold files, and a later copy removes the orphan. */
    @Synchronized
    fun delete() {
        var attempt = 1
        while (attempt++ <= ATTEMPTS && Files.exists(directory)) {
            try { deleteTree(directory); return } catch (_: IOException) { Thread.sleep(RETRY_MS) }
        }
    }

    private fun inCopy(source: Path, file: Path): Path {
        val full = file.toAbsolutePath().normalize()
        return if (full.startsWith(source)) directory.resolve(source.relativize(full)) else file
    }

    private fun removeOthers(expected: Set<Path>) {
        Files.walk(directory).use { files ->
            files.filter { Files.isRegularFile(it) && it !in expected }.toList().forEach { Files.deleteIfExists(it) }
        }
    }

    /** Removes copies left by a crash, once per window. A week old: a copy in use by a running preview is not touched. */
    private fun purgeOrphans() {
        if (purged) return
        purged = true
        try {
            if (!Files.isDirectory(root)) return
            val oldest = System.currentTimeMillis() - ORPHAN_AGE_MS
            Files.list(root).use { it.toList() }
                .filter { Files.getLastModifiedTime(it).toMillis() < oldest }
                .forEach { runCatching { deleteTree(it) } }
        } catch (_: IOException) {}
    }

    companion object {
        private const val ATTEMPTS = 10
        private const val RETRY_MS = 300L
        private const val ORPHAN_AGE_MS = 7L * 24 * 60 * 60 * 1000
        private val ARCHITECTURES = setOf("x64", "x86", "arm64", "arm", "armel", "s390x", "ppc64le", "loongarch64", "riscv64")

        /** Whether a runtimes/<rid> folder can be used on this OS family ("win", "osx", "linux") and architecture. */
        fun isRuntimeFor(rid: String, os: String, architecture: String): Boolean {
            val family = rid.split('-', '.').first()
            if (!family.startsWith(os) && !(os != "win" && family == "unix")) return false
            val last = rid.split('-').last()
            return last !in ARCHITECTURES || last == architecture
        }

        private fun isRuntimeForThisProcess(rid: String): Boolean {
            val name = System.getProperty("os.name").lowercase(Locale.ROOT)
            val os = if (name.contains("win")) "win" else if (name.contains("mac")) "osx" else "linux"
            val architecture = when (val arch = System.getProperty("os.arch").lowercase(Locale.ROOT)) {
                "amd64", "x86_64" -> "x64"
                "aarch64" -> "arm64"
                "i386", "i686" -> "x86"
                else -> arch
            }
            return isRuntimeFor(rid, os, architecture)
        }

        private fun copy(from: Path, to: Path, isRoot: Boolean, inRuntimes: Boolean, expected: MutableSet<Path>) {
            Files.createDirectories(to)
            for (entry in Files.list(from).use { it.toList() }) {
                val target = to.resolve(entry.name)
                if (Files.isDirectory(entry)) {
                    if (inRuntimes && !isRuntimeForThisProcess(entry.name)) continue
                    copy(entry, target, false, isRoot && entry.name.equals("runtimes", ignoreCase = true), expected)
                    continue
                }
                expected.add(target)
                val time = Files.getLastModifiedTime(entry)
                if (Files.exists(target) && Files.size(target) == Files.size(entry) && Files.getLastModifiedTime(target).toMillis() == time.toMillis()) continue
                Files.copy(entry, target, StandardCopyOption.REPLACE_EXISTING)
                Files.setLastModifiedTime(target, time)
            }
        }

        private fun deleteTree(dir: Path) {
            Files.walk(dir).use { files ->
                files.sorted(Comparator.reverseOrder()).toList().forEach { Files.deleteIfExists(it) }
            }
        }
    }
}
