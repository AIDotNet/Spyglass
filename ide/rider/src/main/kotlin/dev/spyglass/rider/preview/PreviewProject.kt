// Finds the project of an .axaml file and the MSBuild properties the previewer host needs (design/preview/README.md §2).
package dev.spyglass.rider.preview

import com.google.gson.JsonParser
import java.io.File
import java.nio.file.Files
import java.nio.file.Path
import java.util.concurrent.TimeUnit

/** Everything needed to launch `Avalonia.Designer.HostApp` for a project. */
data class PreviewProject(
    val projectFile: Path,
    val hostApp: Path,
    val targetPath: Path,
    val runtimeConfig: Path,
    val depsFile: Path,
) {
    val projectDir: Path get() = projectFile.parent
}

/** Why a preview cannot be shown; [needsBuild] adds the "Build project" button. */
class PreviewUnavailable(val reason: String, val needsBuild: Boolean = false, val projectFile: Path? = null) : Exception(reason)

object PreviewProjects {
    /** Nearest `.csproj` in the file's directory or above. */
    fun findProjectFile(xaml: Path): Path? {
        var dir: Path? = xaml.toAbsolutePath().parent
        while (dir != null) {
            val csproj = runCatching { Files.list(dir).use { s -> s.filter { it.fileName.toString().endsWith(".csproj", true) }.sorted().findFirst().orElse(null) } }.getOrNull()
            if (csproj != null) return csproj
            dir = dir.parent
        }
        return null
    }

    /** The XAML's path relative to the project directory, with forward slashes (UpdateXamlMessage.XamlFileProjectPath). */
    fun projectRelativePath(project: PreviewProject, xaml: Path): String =
        project.projectDir.toAbsolutePath().relativize(xaml.toAbsolutePath()).toString().replace(File.separatorChar, '/')

    /** Parses the JSON printed by `dotnet msbuild -getProperty:…` (unit-testable without running dotnet). */
    fun parseProperties(json: String, projectFile: Path): PreviewProject {
        val props = try {
            JsonParser.parseString(json).asJsonObject.getAsJsonObject("Properties")
        } catch (e: Exception) {
            throw PreviewUnavailable("MSBuild returned no properties for ${projectFile.fileName}.", projectFile = projectFile)
        }
        fun prop(name: String): String? = props.get(name)?.takeIf { !it.isJsonNull }?.asString?.trim()?.takeIf { it.isNotEmpty() }
        val hostApp = prop("AvaloniaPreviewerNetCoreToolPath")
            ?: throw PreviewUnavailable("${projectFile.fileName} does not reference Avalonia (no previewer host found).", projectFile = projectFile)
        val target = prop("TargetPath") ?: throw PreviewUnavailable("${projectFile.fileName} has no output assembly.", projectFile = projectFile)
        val runtimeConfig = prop("ProjectRuntimeConfigFilePath")
        val deps = prop("ProjectDepsFilePath")
        if (runtimeConfig == null || deps == null)
            throw PreviewUnavailable("${projectFile.fileName} is not an executable project (needs an entry point to preview).", projectFile = projectFile)
        val project = PreviewProject(projectFile, normalize(hostApp), Path.of(target), Path.of(runtimeConfig), Path.of(deps))
        if (!Files.isRegularFile(project.hostApp))
            throw PreviewUnavailable("The previewer host was not found at ${project.hostApp}. Restore the project's packages.", needsBuild = true, projectFile = projectFile)
        if (!Files.isRegularFile(project.targetPath) || !Files.isRegularFile(project.runtimeConfig) || !Files.isRegularFile(project.depsFile))
            throw PreviewUnavailable("${projectFile.fileName} is not built yet.", needsBuild = true, projectFile = projectFile)
        return project
    }

    /** The host path from MSBuild can contain `buildTransitive//../tools`; resolve it. */
    private fun normalize(path: String): Path = Path.of(path).normalize()

    /** Runs `dotnet msbuild` (a few seconds) and parses the result. Blocking: call from a pooled thread. */
    fun load(projectFile: Path, dotnet: String, environment: Map<String, String>? = null): PreviewProject {
        val cmd = listOf(dotnet, "msbuild", projectFile.toString(), "-getProperty:AvaloniaPreviewerNetCoreToolPath",
            "-getProperty:TargetPath", "-getProperty:ProjectRuntimeConfigFilePath", "-getProperty:ProjectDepsFilePath", "-nologo")
        val pb = ProcessBuilder(cmd).directory(projectFile.parent.toFile()).redirectErrorStream(false)
        environment?.let { pb.environment().putAll(it) }
        val process = try { pb.start() } catch (e: Exception) {
            throw PreviewUnavailable("Cannot run dotnet (${e.message}). Install the .NET SDK or put it on PATH.")
        }
        // Drain stderr so it cannot fill the pipe.
        val err = StringBuilder()
        val t = Thread { process.errorStream.bufferedReader().forEachLine { err.appendLine(it) } }.apply { isDaemon = true; start() }
        val out = process.inputStream.bufferedReader().readText()
        if (!process.waitFor(120, TimeUnit.SECONDS)) {
            process.destroyForcibly()
            throw PreviewUnavailable("dotnet msbuild timed out.")
        }
        t.join(1000)
        if (process.exitValue() != 0 && !out.trimStart().startsWith("{"))
            throw PreviewUnavailable("dotnet msbuild failed: ${(out + err).lineSequence().firstOrNull { it.isNotBlank() } ?: "exit ${process.exitValue()}"}", projectFile = projectFile)
        return parseProperties(out, projectFile)
    }
}

/** Finds the `dotnet` executable: a GUI-launched IDE often lacks the shell's PATH. */
object DotnetLocator {
    fun find(environment: Map<String, String>? = null): String {
        val env = environment ?: System.getenv()
        val isWindows = System.getProperty("os.name").startsWith("Windows")
        val exe = if (isWindows) "dotnet.exe" else "dotnet"
        val dirs = ArrayList<String>()
        env["DOTNET_ROOT"]?.let { dirs += it }
        (env["PATH"] ?: env["Path"])?.split(File.pathSeparatorChar)?.let { dirs += it }
        val home = System.getProperty("user.home")
        dirs += listOf("$home/.dotnet", "$home/.local/bin", "/usr/local/share/dotnet", "/usr/local/bin", "/opt/homebrew/bin", "/usr/share/dotnet", "/usr/lib/dotnet", "C:\\Program Files\\dotnet")
        for (d in dirs) {
            if (d.isBlank()) continue
            val f = File(d, exe)
            if (f.isFile && f.canExecute()) return f.absolutePath
        }
        return "dotnet"
    }
}
