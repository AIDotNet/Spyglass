// Rider/IntelliJ front end for Spyglass XAML hot reload (design/hot-reload/README.md §8), one hub connection per project
// (like one per VS Code window, so the hub sends "reveal" to the project whose folder holds the file). Sends unsaved
// .axaml buffers to the hub (debounced), shows XAML diagnostics and reload status, reveals files for "Open in editor".
package dev.spyglass.rider

import com.intellij.ide.impl.ProjectUtil
import com.intellij.openapi.Disposable
import com.intellij.openapi.application.ApplicationInfo
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.ModalityState
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.service
import com.intellij.openapi.editor.Document
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.editor.event.DocumentEvent
import com.intellij.openapi.editor.event.DocumentListener
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.fileEditor.OpenFileDescriptor
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.util.EnvironmentUtil
import com.intellij.util.concurrency.AppExecutorUtil
import dev.spyglass.rider.hub.ConnectionState
import dev.spyglass.rider.hub.HotReloadStatus
import dev.spyglass.rider.hub.HubClient
import dev.spyglass.rider.hub.HubClientListener
import dev.spyglass.rider.hub.HubClientOptions
import dev.spyglass.rider.hub.XamlDiagnostics
import java.nio.file.Paths
import java.time.LocalTime
import java.time.format.DateTimeFormatter
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit

@Service(Service.Level.PROJECT)
class SpyglassProjectService(val project: Project) : Disposable {
    private val settings get() = SpyglassSettings.getInstance()
    private val scheduler = AppExecutorUtil.createBoundedScheduledExecutorService("Spyglass edits", 1)
    private val timers = ConcurrentHashMap<String, ScheduledFuture<*>>()
    /** Files announced with didOpen in the current connection (OS path form). */
    private val opened: MutableSet<String> = ConcurrentHashMap.newKeySet()
    private val changeListeners = CopyOnWriteArrayList<() -> Unit>()
    private val logLines = ArrayList<String>()
    private val logListeners = CopyOnWriteArrayList<(String) -> Unit>()
    private var documentListenerInstalled = false
    val diagnostics = DiagnosticsPresenter(project, this)

    @Volatile var client: HubClient? = null
        private set
    @Volatile var lastSummary: String? = null
        private set
    @Volatile var lastOk = true
        private set

    /** True once this project has Avalonia XAML (the status bar item and connection exist only then). */
    val isActive: Boolean get() = client != null

    // ---------- lifecycle ----------

    /** Connects to the hub (starting `spyglass serve` if allowed). Idempotent. */
    @Synchronized
    fun ensureStarted() {
        if (client != null || project.isDisposed) return
        if (!documentListenerInstalled) {
            documentListenerInstalled = true
            EditorFactory.getInstance().eventMulticaster.addDocumentListener(object : DocumentListener {
                override fun documentChanged(event: DocumentEvent) = onDocumentChanged(event.document)
            }, this)
        }
        val c = HubClient(HubClientOptions(
            port = settings.port,
            ide = "rider",
            version = ApplicationInfo.getInstance().fullVersion,
            workspaceFolders = { listOfNotNull(project.basePath).map { Paths.get(it).toString() } },
            command = settings.command,
            autoStart = settings.autoStart,
            environment = EnvironmentUtil.getEnvironmentMap(),
            log = ::log,
        ))
        c.revealHandler = ::reveal
        c.addListener(object : HubClientListener {
            override fun stateChanged(state: ConnectionState) = fireChanged()
            override fun connected() {
                opened.clear()
                announceOpenFiles()
            }
            override fun statusChanged(status: HotReloadStatus?) {
                if (status != null) announceOpenFiles()   // new apps may make more open files reloadable
                else {
                    opened.clear()
                    diagnostics.clearAll()
                }
                fireChanged()
            }
            override fun diagnostics(diagnostics: XamlDiagnostics) {
                this@SpyglassProjectService.diagnostics.show(diagnostics)
                log(diagnostics.summary)
                lastSummary = diagnostics.summary
                lastOk = diagnostics.items.none { it.isError }
                fireChanged()
            }
        })
        client = c
        c.start()
        fireChanged()
    }

    /** Applies new port/command/autoStart settings. */
    @Synchronized
    fun restartClient() {
        val old = client ?: return
        client = null
        old.stop()
        opened.clear()
        diagnostics.clearAll()
        ensureStarted()
    }

    fun reconnect() {
        val c = client
        if (c == null) ensureStarted() else c.reconnect()
    }

    fun enabledChanged() {
        if (settings.hotReloadEnabled) {
            opened.clear()
            announceOpenFiles()
        } else {
            for (f in opened) client?.didClose(f)
            opened.clear()
            diagnostics.clearAll()
        }
        fireChanged()
    }

    override fun dispose() {
        client?.stop()
        client = null
        timers.values.forEach { it.cancel(false) }
        scheduler.shutdownNow()
    }

    // ---------- buffers → hub ----------

    private fun announce(file: VirtualFile, document: Document) {
        val c = client ?: return
        if (!settings.hotReloadEnabled || !c.isConnected || !isXaml(file)) return
        val path = pathOf(file)
        if (!c.canReload(path) || !opened.add(path)) return
        // Document snapshots are immutable and safe to read from any thread.
        val version = document.modificationStamp
        val text = document.immutableCharSequence.toString()
        val dirty = FileDocumentManager.getInstance().isDocumentUnsaved(document)
        c.didOpen(path, version, text)
        if (dirty) c.didChange(path, version, text)   // reconnected with unsaved edits
    }

    private fun send(file: VirtualFile, document: Document) {
        val c = client ?: return
        announce(file, document)
        val path = pathOf(file)
        if (!opened.contains(path)) return
        val version = document.modificationStamp
        val text = document.immutableCharSequence.toString()
        c.didChange(path, version, text)
    }

    /** Announces every reloadable file open in this project's editors (on a pooled thread). */
    fun announceOpenFiles() {
        if (project.isDisposed) return
        ApplicationManager.getApplication().executeOnPooledThread {
            if (project.isDisposed) return@executeOnPooledThread
            for (file in FileEditorManager.getInstance(project).openFiles) {
                if (!isXaml(file)) continue
                val doc = FileDocumentManager.getInstance().getCachedDocument(file) ?: continue   // open files have one
                announce(file, doc)
            }
        }
    }

    private fun onDocumentChanged(document: Document) {
        val c = client ?: return
        if (!settings.hotReloadEnabled) return
        val file = FileDocumentManager.getInstance().getFile(document) ?: return
        if (!isXaml(file) || !FileEditorManager.getInstance(project).isFileOpen(file)) return
        val path = pathOf(file)
        if (!c.canReload(path)) return
        val ms = settings.delayMs
        if (ms <= 0) return   // only on save
        timers.remove(path)?.cancel(false)
        timers[path] = scheduler.schedule({
            timers.remove(path)
            if (!project.isDisposed) send(file, document)
        }, ms.toLong(), TimeUnit.MILLISECONDS)
    }

    /** FileDocumentManagerListener.beforeDocumentSaving: with delay 0, edits are sent on save. */
    fun onDocumentSaving(document: Document) {
        val c = client ?: return
        if (!settings.hotReloadEnabled || settings.delayMs > 0) return
        val file = FileDocumentManager.getInstance().getFile(document) ?: return
        if (!isXaml(file) || !FileEditorManager.getInstance(project).isFileOpen(file) || !c.canReload(pathOf(file))) return
        scheduler.execute { if (!project.isDisposed) send(file, document) }
    }

    fun onFileOpened(file: VirtualFile) {
        if (!isXaml(file)) return
        ensureStarted()
        val doc = FileDocumentManager.getInstance().getDocument(file) ?: return
        scheduler.execute { if (!project.isDisposed) announce(file, doc) }
        diagnostics.reapply(file)
    }

    fun onFileClosed(file: VirtualFile) {
        if (!isXaml(file) || FileEditorManager.getInstance(project).isFileOpen(file)) return
        val path = pathOf(file)
        timers.remove(path)?.cancel(false)
        if (opened.remove(path)) client?.didClose(path)
    }

    // ---------- hub → editor ----------

    private fun reveal(r: dev.spyglass.rider.hub.RevealRequest): CompletableFuture<Boolean> {
        val result = CompletableFuture<Boolean>()
        ApplicationManager.getApplication().invokeLater({
            try {
                val nio = Paths.get(r.path)
                val file = LocalFileSystem.getInstance().findFileByNioFile(nio) ?: LocalFileSystem.getInstance().refreshAndFindFileByNioFile(nio)
                if (file == null || project.isDisposed) {
                    result.complete(false)
                } else {
                    OpenFileDescriptor(project, file, maxOf(0, r.line - 1), maxOf(0, r.column - 1)).navigate(true)
                    ProjectUtil.focusProjectWindow(project, true)
                    result.complete(true)
                }
            } catch (e: Exception) {
                log("Cannot open ${r.path}: ${e.message}")
                result.complete(false)
            }
        }, ModalityState.nonModal())
        return result
    }

    // ---------- status & log ----------

    fun addChangeListener(parent: Disposable, l: () -> Unit) {
        changeListeners += l
        Disposer.register(parent) { changeListeners -= l }
    }

    private fun fireChanged() = changeListeners.forEach { it() }

    fun log(line: String) {
        val stamped = "[${LocalTime.now().format(DateTimeFormatter.ofPattern("HH:mm:ss"))}] $line"
        synchronized(logLines) {
            logLines += stamped
            if (logLines.size > 2000) logLines.subList(0, 500).clear()
        }
        logListeners.forEach { it(stamped) }
    }

    /** Current log lines, then every new one, until [parent] is disposed. */
    fun subscribeLog(parent: Disposable, l: (String) -> Unit) {
        synchronized(logLines) {
            logLines.forEach(l)
            logListeners += l
        }
        Disposer.register(parent) { logListeners -= l }
    }

    companion object {
        fun getInstance(project: Project): SpyglassProjectService = project.service()

        fun isXaml(file: VirtualFile): Boolean =
            file.isInLocalFileSystem && (file.extension.equals("axaml", true) || file.extension.equals("xaml", true))

        /** Absolute path in the OS's form (backslashes on Windows), as the hub compares paths. */
        fun pathOf(file: VirtualFile): String = file.toNioPath().toString()
    }
}
