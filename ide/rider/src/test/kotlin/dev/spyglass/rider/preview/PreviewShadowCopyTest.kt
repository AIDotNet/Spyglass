// The copy of the build output the previewer runs from. The real build with a running previewer is in RealHostTest.
package dev.spyglass.rider.preview

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotEquals
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.BeforeEach
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.io.TempDir
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.CsvSource
import java.io.IOException
import java.nio.file.Files
import java.nio.file.Path

class PreviewShadowCopyTest {
    @TempDir lateinit var temp: Path
    private lateinit var bin: Path
    private lateinit var project: PreviewProject
    private lateinit var shadow: PreviewShadowCopy

    private fun write(relative: String, text: String = "x"): Path {
        val file = bin.resolve(relative)
        Files.createDirectories(file.parent)
        Files.writeString(file, text)
        return file
    }

    @BeforeEach
    fun setUp() {
        bin = temp.resolve("app/bin")
        project = PreviewProject(temp.resolve("app/App.csproj"), temp.resolve("nuget/HostApp.dll"),
            write("App.dll"), write("App.runtimeconfig.json"), write("App.deps.json"))
        shadow = PreviewShadowCopy(temp.resolve("copies"))
    }

    @Test
    fun `paths move to the copy except those outside the output`() {
        val copy = shadow.sync(project)
        assertNotEquals(project.targetPath, copy.targetPath)
        assertTrue(Files.exists(copy.targetPath))
        assertTrue(Files.exists(copy.runtimeConfig))
        assertTrue(Files.exists(copy.depsFile))
        assertEquals(project.hostApp, copy.hostApp)
        assertEquals(project.projectFile, copy.projectFile)
    }

    @Test
    fun `subfolders are copied and only this platforms top level runtimes`() {
        write("zh-Hans/App.resources.dll")
        write("runtimes/other-os-x64/native/lib.bin")
        write("content/runtimes/other-os-x64/data.txt")

        val root = shadow.sync(project).targetPath.parent

        assertTrue(Files.exists(root.resolve("zh-Hans/App.resources.dll")))
        assertFalse(Files.exists(root.resolve("runtimes/other-os-x64")))
        assertTrue(Files.exists(root.resolve("content/runtimes/other-os-x64/data.txt")))
    }

    @Test
    fun `a second sync copies only changes and removes what is gone`() {
        val unchanged = write("Lib.dll", "lib")
        val removed = write("Old.dll")
        val copy = shadow.sync(project)
        val root = copy.targetPath.parent

        // A marker of the same size and time as the source: it survives only if the file is not copied again.
        val copied = root.resolve("Lib.dll")
        Files.writeString(copied, "LIB")
        Files.setLastModifiedTime(copied, Files.getLastModifiedTime(unchanged))
        Files.writeString(project.targetPath, "rebuilt")
        Files.delete(removed)

        shadow.sync(project)

        assertEquals("LIB", Files.readString(copied))
        assertEquals("rebuilt", Files.readString(copy.targetPath))
        assertFalse(Files.exists(root.resolve("Old.dll")))
    }

    @Test
    fun `a missing output fails at once and delete removes the copy`() {
        val started = System.currentTimeMillis()
        assertThrows(IOException::class.java) { shadow.sync(project.copy(targetPath = temp.resolve("nowhere/App.dll"))) }
        assertTrue(System.currentTimeMillis() - started < 2000, "not retried")

        val root = shadow.sync(project).targetPath.parent
        shadow.delete()
        assertFalse(Files.exists(root))
    }

    @ParameterizedTest
    @CsvSource(
        "win-x64,win,x64,true", "win,win,x64,true", "win10-x64,win,x64,true", "win-arm64,win,x64,false", "win-arm64,win,arm64,true",
        "linux-x64,win,x64,false", "osx,win,x64,false", "unix,win,x64,false", "unix,linux,x64,true", "linux-musl-x64,linux,x64,true",
        "osx.10.14-x64,osx,arm64,false", "osx-arm64,osx,arm64,true",
    )
    fun `runtime folders match the os and architecture`(rid: String, os: String, architecture: String, expected: Boolean) {
        assertEquals(expected, PreviewShadowCopy.isRuntimeFor(rid, os, architecture))
    }
}
