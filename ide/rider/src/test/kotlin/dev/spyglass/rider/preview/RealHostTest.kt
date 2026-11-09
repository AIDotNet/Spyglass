// End-to-end test against the real Avalonia.Designer.HostApp shipped in the user's Avalonia package, using the built
// samples/Spyglass.Sample. Runs when the sample is built (SPYGLASS_REPO, or the checkout two levels above ide/rider):
//   ./gradlew test --tests '*RealHostTest*'
package dev.spyglass.rider.preview

import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import org.junit.jupiter.api.condition.EnabledIf
import java.awt.image.BufferedImage
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

@EnabledIf("enabled")
@Timeout(180)
class RealHostTest {
    companion object {
        private val root: Path = (System.getenv("SPYGLASS_REPO")?.takeIf { it.isNotBlank() }?.let { Paths.get(it) } ?: Paths.get("../..")).toAbsolutePath().normalize()
        private val csproj = root.resolve("samples/Spyglass.Sample/Spyglass.Sample.csproj")
        private val xamlFile = root.resolve("samples/Spyglass.Sample/MainWindow.axaml")

        @JvmStatic
        fun enabled() = Files.exists(root.resolve("samples/Spyglass.Sample/bin/Debug/net10.0/Spyglass.Sample.dll")) && Files.exists(xamlFile)
    }

    private val results = LinkedBlockingQueue<XamlResult>()
    private val frames = LinkedBlockingQueue<BufferedImage>()
    private val ready = LinkedBlockingQueue<Boolean>()
    private val exited = LinkedBlockingQueue<String>()
    private val hostLog = StringBuffer()
    private var session: PreviewSession? = null

    private val listener = object : PreviewSessionListener {
        override fun ready() { ready.add(true) }
        override fun frame(image: BufferedImage, dpi: Double) { frames.add(image) }
        override fun xamlResult(result: XamlResult) { results.add(result) }
        override fun exited(reason: String) { exited.add(reason) }
    }

    @AfterEach
    fun cleanup() { session?.stop() }

    private fun start(): PreviewSession {
        val dotnet = DotnetLocator.find()
        val project = PreviewProjects.load(csproj, dotnet)
        val s = PreviewSession(project, dotnet, listener, log = { hostLog.appendLine(it); println("[RealHostTest] $it") })
        session = s
        s.start(640.0, 480.0, 96.0)
        assertNotNull(ready.poll(60, TimeUnit.SECONDS), "host did not announce the session; log:\n$hostLog")
        return s
    }

    @Test
    fun `a real dotnet build succeeds while the previewer runs from the copy`() {
        val dotnet = DotnetLocator.find()
        val project = PreviewProjects.load(csproj, dotnet)
        val shadow = PreviewShadowCopy()
        try {
            val s = PreviewSession(shadow.sync(project), dotnet, listener, log = { hostLog.appendLine(it) })
            session = s
            s.start(640.0, 480.0, 96.0)
            assertNotNull(ready.poll(60, TimeUnit.SECONDS), "host did not announce the session; log:\n$hostLog")
            s.updateXaml(Files.readString(xamlFile), "MainWindow.axaml")
            val rendered = results.poll(60, TimeUnit.SECONDS)
            assertNotNull(rendered, "no UpdateXamlResult; log:\n$hostLog")
            assertNull(rendered!!.error, "unexpected error: ${rendered.error}")

            val before = Files.getLastModifiedTime(project.targetPath)
            // Touching a source file makes the build compile and copy the assembly again.
            Files.setLastModifiedTime(csproj.resolveSibling("Program.cs"), java.nio.file.attribute.FileTime.fromMillis(System.currentTimeMillis()))
            val build = ProcessBuilder(dotnet, "build", csproj.toString(), "-nologo", "-v:q", "-p:UseSharedCompilation=false")
                .redirectErrorStream(true)
            build.environment()["MSBUILDDISABLENODEREUSE"] = "1"
            val process = build.start()
            val output = process.inputStream.bufferedReader().readText()
            assertEquals(0, process.waitFor(), "dotnet build failed:\n$output")
            assertTrue(Files.getLastModifiedTime(project.targetPath) > before, "the build did not rewrite bin/:\n$output")
        } finally {
            session?.stop()
            session = null
            shadow.delete()
        }
    }

    @Test
    fun `renders the sample, reports xaml errors with line and column, and recovers`() {
        val projectInfo = PreviewProjects.load(csproj, DotnetLocator.find())
        assertEquals("MainWindow.axaml", PreviewProjects.projectRelativePath(projectInfo, xamlFile))
        val s = start()
        val xaml = Files.readString(xamlFile)

        // 1. A valid buffer renders: no error, and a frame arrives with real pixels.
        s.updateXaml(xaml, "MainWindow.axaml")
        val ok = results.poll(60, TimeUnit.SECONDS)
        assertNotNull(ok, "no UpdateXamlResult; log:\n$hostLog")
        assertNull(ok!!.error, "unexpected error: ${ok.error}")
        val frame = frames.poll(30, TimeUnit.SECONDS)
        assertNotNull(frame, "no frame; log:\n$hostLog")
        assertTrue(frame!!.width > 1 && frame.height > 1, "frame ${frame.width}x${frame.height}")
        val distinct = (0 until frame.height step 4).flatMap { y -> (0 until frame.width step 4).map { x -> frame.getRGB(x, y) } }.toSet()
        assertTrue(distinct.size > 1, "frame is one flat colour")

        // 2. An unsaved, malformed buffer yields an error with line/column; the session survives.
        results.clear()
        val broken = xaml.replace("</Window>", "</Windo>")
        s.updateXaml(broken, "MainWindow.axaml")
        val bad = results.poll(30, TimeUnit.SECONDS)
        assertNotNull(bad, "no result for the broken buffer")
        assertNotNull(bad!!.error)
        assertNotNull(bad.line, "no line number in: ${bad.error}")
        assertTrue(bad.line!! >= 1 && bad.column!! >= 1)
        println("[RealHostTest] error at ${bad.line}:${bad.column}: ${bad.error!!.lineSequence().first()}")

        // 3. Fixing the buffer renders again, and input is accepted without breaking the session.
        results.clear(); frames.clear()
        s.updateXaml(xaml, "MainWindow.axaml")
        assertNull(results.poll(30, TimeUnit.SECONDS)!!.error)
        assertNotNull(frames.poll(30, TimeUnit.SECONDS))
        s.send(Messages.pointerMoved(20.0, 20.0, emptyList()))
        s.send(Messages.pointerPressed(20.0, 20.0, MouseButtons.LEFT, listOf(Modifiers.LEFT_BUTTON)))
        s.send(Messages.pointerReleased(20.0, 20.0, MouseButtons.LEFT, emptyList()))
        s.send(Messages.scroll(20.0, 20.0, 0.0, -1.0, emptyList()))
        s.send(Messages.key(true, 44, "a", emptyList())); s.send(Messages.text("a", emptyList())); s.send(Messages.key(false, 44, "a", emptyList()))
        // A viewport change renders a new frame at the new size.
        frames.clear()
        s.resize(900.0, 700.0, 96.0)
        assertNotNull(frames.poll(30, TimeUnit.SECONDS), "no frame after resize")
        assertTrue(exited.isEmpty(), "host exited: ${exited.peek()}")
    }

    @Test
    fun `stopping ends the host process`() {
        val s = start()
        val pid = listOfNotNull(s.hostProcess?.toHandle())
        assertTrue(pid.isNotEmpty(), "host process not found")
        s.stop()
        val deadline = System.currentTimeMillis() + 10_000
        while (System.currentTimeMillis() < deadline && pid.any { it.isAlive }) Thread.sleep(100)
        assertFalse(pid.any { it.isAlive }, "host still running")
    }

    @Test
    fun `a crashing host is reported as exited`() {
        val s = start()
        s.hostProcess!!.toHandle().destroyForcibly()
        assertNotNull(exited.poll(15, TimeUnit.SECONDS), "no exit notification")
        s.stop()
    }

    @Test
    fun `theme variants render, a UserControl root is not rejected, and positions map back`() {
        val s = start()
        val xaml = Files.readString(xamlFile)
        for (theme in listOf(PreviewTheme.DARK, PreviewTheme.LIGHT)) {
            results.clear()
            val (themed, _) = PreviewXaml.applyTheme(xaml, theme)
            assertTrue(themed.contains("RequestedThemeVariant"))
            s.updateXaml(themed, "MainWindow.axaml")
            val r = results.poll(30, TimeUnit.SECONDS)
            assertNotNull(r); assertNull(r!!.error, "theme $theme: ${r.error}")
        }
        // The dark and the light frame differ (frames are asynchronous: wait for the result, then take the last frame that settles).
        fun settledFrame(themeUnderTest: PreviewTheme): BufferedImage {
            results.clear(); frames.clear()
            s.updateXaml(PreviewXaml.applyTheme(xaml, themeUnderTest).first, "MainWindow.axaml")
            assertNull(results.poll(30, TimeUnit.SECONDS)!!.error)
            var last = frames.poll(30, TimeUnit.SECONDS)!!
            Thread.sleep(1000)   // the sample may animate, so do not wait for quiet: take the newest frame after a second
            while (true) last = frames.poll() ?: break
            return last
        }
        val dark = settledFrame(PreviewTheme.DARK)
        val light = settledFrame(PreviewTheme.LIGHT)
        fun luminance(img: BufferedImage): Double {
            var sum = 0.0; var n = 0
            for (y in 0 until img.height step 3) for (x in 0 until img.width step 3) {
                val p = img.getRGB(x, y)
                sum += 0.299 * ((p shr 16) and 0xFF) + 0.587 * ((p shr 8) and 0xFF) + 0.114 * (p and 0xFF); n++
            }
            return sum / n
        }
        val diff = luminance(light) - luminance(dark)
        println("[RealHostTest] luminance light=${luminance(light)} dark=${luminance(dark)}")
        assertTrue(diff > 40, "light/dark luminance difference $diff should be > 40")

        // A UserControl root is sent as is (no attribute), and renders.
        results.clear()
        val control = """<UserControl xmlns="https://github.com/avaloniaui"><TextBlock Text="hi"/></UserControl>"""
        val (sent, edit) = PreviewXaml.applyTheme(control, PreviewTheme.DARK)
        assertEquals(control, sent); assertTrue(edit.isEmpty)
        s.updateXaml(sent, "Views/Control.axaml")
        assertNull(results.poll(30, TimeUnit.SECONDS)!!.error)

        // An error on the root line is reported at the editor's column, not the shifted one.
        results.clear()
        val rootLine = "<Window xmlns=\"https://github.com/avaloniaui\" Width=\"200\" Height=\"100\" Title=oops>\n</Window>"
        val (themed, e) = PreviewXaml.applyTheme(rootLine, PreviewTheme.DARK)
        s.updateXaml(themed, "MainWindow.axaml")
        val bad = results.poll(30, TimeUnit.SECONDS)!!
        assertNotNull(bad.error); assertNotNull(bad.line)
        val mapped = e.mapBack(bad.line, bad.column)
        println("[RealHostTest] host error ${bad.line}:${bad.column} → editor ${mapped.first}:${mapped.second}")
        assertEquals(1, mapped.first)
        // The unquoted value is at column 61 of the editor text; the host sees it after the 30-character insertion.
        val editorColumn = rootLine.lineSequence().first().indexOf("oops") + 1
        assertTrue(bad.column!! > mapped.second!!, "host column ${bad.column} should be shifted past ${mapped.second}")
        assertTrue(kotlin.math.abs(mapped.second!! - editorColumn) <= 2, "mapped ${mapped.second}, editor column of the error $editorColumn")
    }

    /** Probes what the host does when the pushed document is not a window: a resource dictionary / App.axaml edit cannot be previewed. */
    @Test
    fun `other xaml documents are not hot reloaded into the host (documented limitation)`() {
        val s = start()
        val appXaml = Files.readString(root.resolve("samples/Spyglass.Sample/App.axaml"))
        s.updateXaml(appXaml, "App.axaml")
        val app = results.poll(30, TimeUnit.SECONDS)!!
        println("[RealHostTest] App.axaml pushed → error=${app.error?.lineSequence()?.firstOrNull()}")
        results.clear()
        val styles = """<Styles xmlns="https://github.com/avaloniaui"><Style Selector="TextBlock"><Setter Property="FontSize" Value="40"/></Style></Styles>"""
        s.updateXaml(styles, "Styles.axaml")
        val st = results.poll(30, TimeUnit.SECONDS)!!
        println("[RealHostTest] Styles pushed → error=${st.error?.lineSequence()?.firstOrNull()}")
        // Whatever happens, the session survives and the real window renders again afterwards.
        results.clear()
        s.updateXaml(Files.readString(xamlFile), "MainWindow.axaml")
        assertNull(results.poll(30, TimeUnit.SECONDS)!!.error)
        assertTrue(exited.isEmpty())
    }
}
