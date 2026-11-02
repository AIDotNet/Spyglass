// Design-time XAML preview for a project (design/preview/README.md): owns one previewer host (PreviewSession) for the
// project of the active .axaml file, feeds it the unsaved editor buffer (debounced) and publishes frames/errors for the
// tool window. All slow work (MSBuild evaluation, process start, build) runs on one background thread.
package dev.spyglass.rider.preview

import com.intellij.openapi.Disposable
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.ModalityState
import com.intellij.openapi.application.ReadAction
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.service
import com.intellij.openapi.editor.Document
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.editor.event.DocumentEvent
import com.intellij.openapi.editor.event.DocumentListener
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.fileEditor.OpenFileDescriptor
import com.intellij.ui.EditorNotifications
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.util.EnvironmentUtil
import com.intellij.util.concurrency.AppExecutorUtil
import dev.spyglass.rider.SpyglassProjectService
import dev.spyglass.rider.SpyglassSettings
import dev.spyglass.rider.DiagnosticsPresenter
import dev.spyglass.rider.hub.XamlDiagnostic
import dev.spyglass.rider.hub.XamlDiagnostics
import java.awt.image.BufferedImage
import java.nio.file.Files
import java.nio.file.Path
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit

enum class PreviewPhase { IDLE, DISABLED, LOADING, STARTING, READY, UNAVAILABLE, EXITED }

@Service(Service.Level.PROJECT)
class SpyglassPreviewService(val project: Project) : Disposable {
    private val settings get() = SpyglassSettings.getInstance()
    private val scheduler = AppExecutorUtil.createBoundedScheduledExecutorService("Spyglass preview", 1)
    private val listeners = CopyOnWriteArrayList<() -> Unit>()
    private var documentListenerInstalled = false
    private var updateTimer: ScheduledFuture<*>? = null
    private var viewportTimer: ScheduledFuture<*>? = null
    private var watcher: ScheduledFuture<*>? = null
    val diagnostics = DiagnosticsPresenter(project, this, "Spyglass Preview")

    // Session state; touched only on the scheduler thread except the @Volatile fields the UI reads.
    private var session: PreviewSession? = null
    private var sessionProject: PreviewProject? = null
    private var targetStamp = 0L

    @Volatile var active = false; private set
    @Volatile var file: VirtualFile? = null; private set
    @Volatile var phase = PreviewPhase.IDLE; private set
    /** Status line shown when there is no preview (reason, "Starting…"); null otherwise. */
    @Volatile var message: String? = null; private set
    @Volatile var needsBuild = false; private set
    @Volatile var buildTarget: Path? = null; private set
    @Volatile var frame: BufferedImage? = null; private set
    @Volatile var frameScale = 1.0; private set
    @Volatile var error: XamlResult? = null; private set
    @Volatile var zoom: PreviewZoom = settings.zoom; private set
    @Volatile var theme: PreviewTheme = settings.theme; private set
    /** True between sending a buffer and the host's answer. */
    @Volatile var updating = false; private set
    /** Milliseconds the last update took (send → host result); null before the first one. */
    @Volatile var lastUpdateMs: Long? = null; private set
    /** The pane's size in DIPs and its scale (see [viewportChanged]). */
    @Volatile private var pane = Triple(800.0, 600.0, 1.0)
    @Volatile private var sentFile: VirtualFile? = null
    @Volatile private var sentEdit = PreviewXamlEdit.NONE
    @Volatile private var sentAt = 0L

    // ---------- UI hooks ----------

    fun addListener(parent: Disposable, l: () -> Unit) {
        listeners += l
        Disposer.register(parent) { listeners -= l }
    }

    private fun changed() {
        ApplicationManager.getApplication().invokeLater({ if (!project.isDisposed) listeners.forEach { it() } }, ModalityState.any())
    }

    /** The preview tool window became visible. */
    fun onShown() {
        active = true
        zoom = settings.zoom; theme = settings.theme
        EditorNotifications.getInstance(project).updateAllNotifications()
        installDocumentListener()
        if (!settings.previewEnabled) { setState(PreviewPhase.DISABLED, "The preview is turned off in Settings › Tools › Spyglass."); return }
        val f = file?.takeIf { it.isValid && SpyglassProjectService.isXaml(it) } ?: currentXamlFile()
        if (f == null) { setState(PreviewPhase.IDLE, "Open an .axaml file to preview it."); return }
        switchTo(f)
        if (watcher == null) watcher = scheduler.scheduleWithFixedDelay({ safely(::watchBuild) }, 3, 3, TimeUnit.SECONDS)
    }

    /** The tool window was hidden or closed: end the host process. */
    fun onHidden() {
        active = false
        watcher?.cancel(false); watcher = null
        updateTimer?.cancel(false)
        scheduler.execute { stopSession(); }
        frame = null; error = null
        diagnostics.clearAll()
        updating = false
        setState(PreviewPhase.IDLE, null)
        EditorNotifications.getInstance(project).updateAllNotifications()
    }

    /** The user selected another editor. */
    fun fileSelected(f: VirtualFile) {
        if (!active || !settings.previewEnabled || !SpyglassProjectService.isXaml(f) || f == file) return
        switchTo(f)
    }

    /** "Open Preview" on a file: shows it now if the window is open, otherwise when it opens. */
    fun requestFile(f: VirtualFile) {
        if (active) fileSelected(f) else if (SpyglassProjectService.isXaml(f)) file = f
    }

    fun settingsChanged() {
        if (!active) return
        if (!settings.previewEnabled) {
            scheduler.execute { stopSession() }
            frame = null
            diagnostics.clearAll()
            setState(PreviewPhase.DISABLED, "The preview is turned off in Settings › Tools › Spyglass.")
        } else if (phase == PreviewPhase.DISABLED) onShown()
    }

    fun restart() {
        if (!active) return
        scheduler.execute { stopSession(); safely { ensureSession(); sendCurrent() } }
    }

    /** `dotnet build` of the project that is not built yet, then start. */
    fun build() {
        val csproj = buildTarget ?: return
        val log = SpyglassProjectService.getInstance(project)
        setState(PreviewPhase.LOADING, "Building ${csproj.fileName}…")
        scheduler.execute {
            safely {
                val dotnet = DotnetLocator.find(EnvironmentUtil.getEnvironmentMap())
                val pb = ProcessBuilder(dotnet, "build", csproj.toString(), "-nologo", "-v:m").directory(csproj.parent.toFile()).redirectErrorStream(true)
                pb.environment().putAll(EnvironmentUtil.getEnvironmentMap())
                val p = pb.start()
                p.inputStream.bufferedReader().forEachLine { log.log("preview build: $it") }
                val code = p.waitFor()
                if (code != 0) { needsBuild = true; setState(PreviewPhase.UNAVAILABLE, "dotnet build failed (exit $code); see the Spyglass log.", true) }
                else { stopSession(); ensureSession(); sendCurrent() }
            }
        }
    }

    /** The panel's size in DIPs and its scale; the host is told after 100 ms without changes. */
    fun viewportChanged(width: Double, height: Double, scale: Double) {
        val old = pane
        pane = Triple(width, height, scale)
        if (old == pane) return
        pushViewport()
    }

    private fun pushViewport() {
        viewportTimer?.cancel(false)
        viewportTimer = scheduler.schedule({
            val (w, h, scale) = pane
            val (vw, vh) = zoom.viewport(w, h)
            session?.resize(vw, vh, PreviewInput.dpi(scale))
        }, 100, TimeUnit.MILLISECONDS)
    }

    /** Sets the zoom (remembered in the settings); a non-fit zoom changes the viewport the host renders for. */
    fun setZoom(z: PreviewZoom) {
        if (z == zoom) return
        zoom = z
        settings.zoom = z
        pushViewport()
        changed()
    }

    fun setTheme(t: PreviewTheme) {
        if (t == theme) return
        theme = t
        settings.theme = t
        scheduler.execute { safely(::sendCurrent) }
        changed()
    }

    fun send(message: Pair<MessageType, Map<String, Any?>>) { session?.send(message) }

    // ---------- buffers → host ----------

    private fun installDocumentListener() {
        if (documentListenerInstalled) return
        documentListenerInstalled = true
        EditorFactory.getInstance().eventMulticaster.addDocumentListener(object : DocumentListener {
            override fun documentChanged(event: DocumentEvent) = onDocumentChanged(event.document)
        }, this)
    }

    /** Is [doc] the previewed file, or another .axaml of the same project (App.axaml, styles, resources) that can affect it? */
    private fun affectsPreview(doc: Document): Boolean {
        val current = file ?: return false
        val changed = FileDocumentManager.getInstance().getFile(doc) ?: return false
        if (changed == current) return true
        if (!SpyglassProjectService.isXaml(changed)) return false
        val dir = sessionProject?.projectDir ?: return false
        return Path.of(changed.path).startsWith(dir)
    }

    private fun onDocumentChanged(document: Document) {
        if (!active || !settings.previewEnabled || !affectsPreview(document)) return
        val ms = settings.previewDelayMs
        if (ms <= 0) return   // only on save
        updating = true
        changed()
        updateTimer?.cancel(false)
        updateTimer = scheduler.schedule({ safely(::sendCurrent) }, ms.toLong(), TimeUnit.MILLISECONDS)
    }

    fun onDocumentSaving(document: Document) {
        if (!active || !settings.previewEnabled || settings.previewDelayMs > 0 || !affectsPreview(document)) return
        scheduler.execute { safely(::sendCurrent) }
    }

    private fun switchTo(f: VirtualFile) {
        file = f
        error = null
        scheduler.execute { safely { ensureSession(); sendCurrent() } }
    }

    private fun currentXamlFile(): VirtualFile? {
        val mgr = FileEditorManager.getInstance(project)
        return mgr.selectedFiles.firstOrNull { SpyglassProjectService.isXaml(it) } ?: mgr.openFiles.firstOrNull { SpyglassProjectService.isXaml(it) }
    }

    // ---------- scheduler thread ----------

    private fun safely(action: () -> Unit) {
        try { action() } catch (e: PreviewUnavailable) {
            needsBuild = e.needsBuild
            buildTarget = e.projectFile
            setState(PreviewPhase.UNAVAILABLE, e.reason, e.needsBuild)
        } catch (e: Throwable) {
            if (e is com.intellij.openapi.progress.ProcessCanceledException) throw e
            SpyglassProjectService.getInstance(project).log("preview: $e")
            setState(PreviewPhase.UNAVAILABLE, "Preview failed: ${e.message ?: e::class.simpleName}")
        }
    }

    private fun setState(p: PreviewPhase, text: String?, build: Boolean = false) {
        phase = p; message = text; needsBuild = build
        changed()
    }

    private fun stopSession() {
        session?.stop()
        session = null
        sessionProject = null
    }

    private fun ensureSession() {
        val f = file ?: return
        if (!active || project.isDisposed) return
        val xaml = Path.of(f.path)
        val csproj = PreviewProjects.findProjectFile(xaml)
        if (csproj == null) {
            stopSession()
            throw PreviewUnavailable("No .csproj above ${f.name}.")
        }
        buildTarget = csproj
        val existing = sessionProject
        if (session != null && existing?.projectFile == csproj && !targetChanged(existing)) return
        stopSession()
        setState(PreviewPhase.LOADING, "Evaluating ${csproj.fileName}…")
        val env = EnvironmentUtil.getEnvironmentMap()
        val dotnet = DotnetLocator.find(env)
        val info = PreviewProjects.load(csproj, dotnet, env)
        if (!active) return
        val log = SpyglassProjectService.getInstance(project)
        lateinit var s: PreviewSession
        s = PreviewSession(info, dotnet, object : PreviewSessionListener {
            override fun ready() { if (session === s && phase == PreviewPhase.LOADING) setState(PreviewPhase.STARTING, "Rendering…") }
            override fun frame(image: BufferedImage, dpi: Double) {
                if (session !== s) return
                frame = image; frameScale = dpi / 96.0
                changed()
            }
            override fun xamlResult(result: XamlResult) { if (session === s) onResult(result) }
            override fun exited(reason: String) {
                if (session !== s) return
                log.log("preview: $reason")
                session = null; sessionProject = null
                updating = false
                setState(PreviewPhase.EXITED, reason)
            }
        }, env, log = { log.log(it) })
        session = s
        sessionProject = info
        targetStamp = stamp(info)
        val (w, h, scale) = pane
        val (vw, vh) = zoom.viewport(w, h)
        setState(PreviewPhase.STARTING, "Starting the previewer…")
        s.start(vw, vh, PreviewInput.dpi(scale))
    }

    private fun stamp(p: PreviewProject): Long = runCatching { Files.getLastModifiedTime(p.targetPath).toMillis() xor Files.size(p.targetPath) }.getOrDefault(0L)

    private fun targetChanged(p: PreviewProject) = stamp(p) != targetStamp

    /** Rebuilt output → restart the host (it never unloads the assembly) and render again; build-needed → retry. */
    private fun watchBuild() {
        if (!active || !settings.previewEnabled || file == null) return
        val p = sessionProject
        if (p != null) {
            if (!targetChanged(p)) return
            Thread.sleep(500)   // let the build finish writing
            stopSession(); ensureSession(); sendCurrent()
        } else if (phase == PreviewPhase.UNAVAILABLE && needsBuild) {
            ensureSession(); sendCurrent()
        }
    }

    private fun sendCurrent() {
        val f = file ?: return
        val s = session ?: return
        val info = sessionProject ?: return
        if (!active || !f.isValid) return
        val text = ReadAction.compute<String?, RuntimeException> {
            FileDocumentManager.getInstance().getDocument(f)?.immutableCharSequence?.toString()
        } ?: runCatching { String(f.contentsToByteArray(), f.charset) }.getOrNull() ?: return
        val (themed, edit) = PreviewXaml.applyTheme(text, theme)
        sentFile = f
        sentEdit = edit
        sentAt = System.nanoTime()
        updating = true
        changed()
        s.updateXaml(themed, PreviewProjects.projectRelativePath(info, Path.of(f.path)))
    }

    private fun onResult(result: XamlResult) {
        // Positions refer to the XAML as sent (theme attribute inserted); map them back to the editor's text.
        val (line, column) = sentEdit.mapBack(result.line, result.column)
        val mapped = XamlResult(result.error, line, column)
        error = mapped.error?.let { mapped }
        lastUpdateMs = TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - sentAt)
        updating = false
        val f = sentFile
        val items = if (mapped.error == null) emptyList() else listOf(XamlDiagnostic("error", errorSummary(mapped.error), line, column, null))
        if (f != null) diagnostics.show(XamlDiagnostics(path = Path.of(f.path).toString(), items = items, summary = mapped.error?.let(::errorSummary) ?: "ok"))
        if (phase != PreviewPhase.READY) { phase = PreviewPhase.READY; message = null }
        changed()
    }

    /** Puts the caret in the editor where the preview's error is. */
    fun navigateToError() {
        val f = sentFile?.takeIf { it.isValid } ?: file ?: return
        val e = error ?: return
        val line = maxOf(0, (e.line ?: 1) - 1)
        val col = maxOf(0, (e.column ?: 1) - 1)
        ApplicationManager.getApplication().invokeLater({
            if (!project.isDisposed) OpenFileDescriptor(project, f, line, col).navigate(true)
        }, ModalityState.nonModal())
    }

    override fun dispose() {
        active = false
        updateTimer?.cancel(false); viewportTimer?.cancel(false); watcher?.cancel(false)
        session?.stop(); session = null
        scheduler.shutdownNow()
    }

    companion object {
        fun getInstance(project: Project): SpyglassPreviewService = project.service()

        /** Exceptions that only wrap the real cause (the previewer loads XAML through reflection). */
        private val WRAPPERS = Regex("^(System\\.Reflection\\.TargetInvocationException|System\\.TypeInitializationException|System\\.AggregateException)\\b")

        /**
         * One line for the host's `ex.ToString()`: the first exception in the chain (outer, then each ` ---> ` inner) that is not
         * a mere wrapper, so "TargetInvocationException: Exception has been thrown by the target of an invocation." shows its cause.
         */
        fun errorSummary(text: String): String {
            val lines = text.lines().map { it.trim() }
            val first = lines.firstOrNull { it.isNotEmpty() }
            val headers = listOfNotNull(first) + lines.filter { it.startsWith("--->") }.map { it.removePrefix("--->").trim() }
            val pick = headers.firstOrNull { !WRAPPERS.containsMatchIn(it) } ?: headers.lastOrNull() ?: text.trim()
            return if (pick.length > 300) pick.take(297) + "…" else pick
        }
    }
}
