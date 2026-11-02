// The preview pane to the right of an .axaml editor (the split editor with the "Editor / Editor and Preview / Preview"
// buttons that Rider shows for XAML). Rider's own XAML previewer only knows WPF; for Avalonia files it shows
// "This project does not support preview" unless an extension provides an editor for PreviewPlatformKind.AVALONIA.
// This is that extension: it hosts the same PreviewPanel as the "Spyglass Preview" tool window, fed by the same service.
package dev.spyglass.rider.preview

import com.intellij.icons.AllIcons
import com.intellij.openapi.Disposable
import com.intellij.openapi.actionSystem.ActionManager
import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.DefaultActionGroup
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.ModalityState
import com.intellij.openapi.fileEditor.FileEditor
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.fileEditor.FileEditorState
import com.intellij.openapi.project.DumbAware
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.UserDataHolderBase
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.openapi.wm.ToolWindowManager
import com.intellij.util.concurrency.AppExecutorUtil
import com.jetbrains.rd.util.reactive.IPropertyView
import com.jetbrains.rd.util.reactive.Property
import com.jetbrains.rider.model.PreviewPlatformKind
import com.jetbrains.rider.xaml.core.XamlPreviewEditor
import com.jetbrains.rider.xaml.core.XamlPreviewEditorExtension
import com.jetbrains.rider.xaml.previewEditor.PreviewEditorToolbar
import com.jetbrains.rider.xaml.splitEditor.XamlSplitEditor
import com.jetbrains.rider.xaml.splitEditor.XamlSplitEditorSplitLayout
import dev.spyglass.rider.SpyglassProjectService
import dev.spyglass.rider.SpyglassSettings
import java.awt.event.HierarchyEvent
import java.beans.PropertyChangeListener
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import javax.swing.JComponent

/** Registered as `rider.xaml.preview.editor` (first): Avalonia files get Spyglass' preview unless it is turned off in Settings. */
class SpyglassXamlPreviewEditorExtension : XamlPreviewEditorExtension {
    override fun accepts(file: VirtualFile, platformKind: PreviewPlatformKind): Boolean =
        SpyglassSettings.getInstance().previewEnabled && EmbeddedPreview.accepts(file.extension, platformKind.name)

    override fun createEditor(project: Project, file: VirtualFile, splitEditor: XamlSplitEditor, platformKind: PreviewPlatformKind): XamlPreviewEditor =
        SpyglassXamlPreviewEditor(project, file, splitEditor)
}

/** Pure decision logic (unit-testable without the IDE). */
object EmbeddedPreview {
    /** Avalonia files; a file whose platform Rider could not tell (UNDEFINED) is ours when it is an `.axaml`. */
    fun accepts(extension: String?, platformKind: String): Boolean =
        platformKind == "AVALONIA" || (platformKind == "UNDEFINED" && extension.equals("axaml", ignoreCase = true))
}

/**
 * The preview half of the split editor. The previewer host runs only while the pane is actually showing (not in "editor only"
 * layout, not on a hidden tab); the project service keeps one host and follows the active .axaml file.
 */
class SpyglassXamlPreviewEditor(
    override val project: Project,
    private val file: VirtualFile,
    override var parentEditor: XamlSplitEditor,
) : UserDataHolderBase(), XamlPreviewEditor {
    private val service = SpyglassPreviewService.getInstance(project)
    private val panel = PreviewPanel(service)
    private val zoom = Property(1.0)

    init {
        service.addListener(this) { panel.refresh() }
        panel.addHierarchyListener { e ->
            if (e.changeFlags and HierarchyEvent.SHOWING_CHANGED.toLong() != 0L) {
                if (panel.isShowing) EmbeddedPreviews.shown(this) else EmbeddedPreviews.hidden(this)
            }
        }
    }

    internal val projectOf: Project get() = project
    internal val fileOf get() = file

    override val toolbar: PreviewEditorToolbar by lazy { buildToolbar() }
    override val virtualFilePath: String get() = file.path
    override val zoomFactorLive: IPropertyView<Double> get() = zoom

    private fun buildToolbar(): PreviewEditorToolbar {
        val manager = ActionManager.getInstance()
        val left = manager.createActionToolbar("SpyglassPreviewLeft", DefaultActionGroup(RestartAction()), true)
        val right = manager.createActionToolbar("SpyglassPreviewRight", DefaultActionGroup(), true)
        left.targetComponent = panel
        right.targetComponent = panel
        return PreviewEditorToolbar(left, right)
    }

    private inner class RestartAction : AnAction("Restart Preview", "Restart the previewer host", AllIcons.Actions.Refresh), DumbAware {
        override fun getActionUpdateThread() = ActionUpdateThread.BGT
        override fun actionPerformed(e: AnActionEvent) = service.restart()
    }

    // ---- XamlPreviewEditor ----
    override fun updateLayout() { panel.revalidate(); panel.repaint() }

    // ---- FileEditor ----
    override fun getComponent(): JComponent = panel
    override fun getPreferredFocusedComponent(): JComponent = panel
    override fun getName(): String = "Spyglass Preview"
    override fun getFile(): VirtualFile = file
    override fun setState(state: FileEditorState) {}
    override fun isModified(): Boolean = false
    override fun isValid(): Boolean = file.isValid
    override fun addPropertyChangeListener(listener: PropertyChangeListener) {}
    override fun removePropertyChangeListener(listener: PropertyChangeListener) {}
    override fun dispose() { EmbeddedPreviews.hidden(this) }
}

/** Which embedded panes are showing per project: the host starts with the first one and stops shortly after the last one hides. */
internal object EmbeddedPreviews {
    private val showing = ConcurrentHashMap<Project, MutableSet<SpyglassXamlPreviewEditor>>()
    /** Tab switches hide one pane and show another a moment later; do not restart the host for that. */
    private const val HIDE_DELAY_SECONDS = 2L

    fun shown(editor: SpyglassXamlPreviewEditor) {
        val project = editor.projectOf
        if (project.isDisposed) return
        showing.computeIfAbsent(project) { ConcurrentHashMap.newKeySet() }.add(editor)
        val service = SpyglassPreviewService.getInstance(project)
        service.requestFile(editor.fileOf)
        if (!service.active) service.onShown()
    }

    fun hidden(editor: SpyglassXamlPreviewEditor) {
        val project = editor.projectOf
        showing[project]?.remove(editor)
        AppExecutorUtil.getAppScheduledExecutorService().schedule({
            ApplicationManager.getApplication().invokeLater({
                if (project.isDisposed) return@invokeLater
                val stillShowing = showing[project]?.isNotEmpty() == true
                val toolWindowOpen = ToolWindowManager.getInstance(project).getToolWindow(PREVIEW_TOOL_WINDOW_ID)?.isVisible == true
                if (!stillShowing && !toolWindowOpen) project.getServiceIfCreated(SpyglassPreviewService::class.java)?.onHidden()
            }, ModalityState.any())
        }, HIDE_DELAY_SECONDS, TimeUnit.SECONDS)
    }

    /** The split editor of [file], when it uses our preview. */
    fun splitEditorOf(project: Project, file: VirtualFile): XamlSplitEditor? =
        FileEditorManager.getInstance(project).getAllEditors(file)
            .filterIsInstance<XamlSplitEditor>().firstOrNull { it.previewEditor is SpyglassXamlPreviewEditor }

    /** "Open Preview" on a file that has the split editor: make the preview visible there. Returns false when there is none. */
    fun reveal(project: Project, file: VirtualFile): Boolean {
        val split = splitEditorOf(project, file) ?: return false
        if (split.layout == XamlSplitEditorSplitLayout.EDITOR_ONLY) split.triggerLayoutChange(XamlSplitEditorSplitLayout.SPLIT, true)
        return true
    }
}
