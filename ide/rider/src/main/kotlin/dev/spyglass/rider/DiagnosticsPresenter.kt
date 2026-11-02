package dev.spyglass.rider

import com.intellij.icons.AllIcons
import com.intellij.openapi.Disposable
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.ModalityState
import com.intellij.openapi.editor.colors.CodeInsightColors
import com.intellij.openapi.editor.impl.DocumentMarkupModel
import com.intellij.openapi.editor.markup.GutterIconRenderer
import com.intellij.openapi.editor.markup.HighlighterLayer
import com.intellij.openapi.editor.markup.HighlighterTargetArea
import com.intellij.openapi.editor.markup.RangeHighlighter
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VirtualFile
import dev.spyglass.rider.hub.DiagnosticRanges
import dev.spyglass.rider.hub.HubClient
import dev.spyglass.rider.hub.XamlDiagnostic
import dev.spyglass.rider.hub.XamlDiagnostics
import java.nio.file.Paths
import java.util.concurrent.ConcurrentHashMap
import javax.swing.Icon

/**
 * Shows xaml/diagnostics as error/warning highlighting (squiggle, error stripe mark and gutter icon with the message) in
 * the project's markup model. Independent of the file's language and of Rider's backend-driven daemon, so it works for
 * .axaml next to Rider's built-in Avalonia support and AvaloniaRider. The newest result per file replaces the previous
 * one; an empty list clears it.
 */
class DiagnosticsPresenter(private val project: Project, parent: Disposable) {
    private val latest = ConcurrentHashMap<String, XamlDiagnostics>()
    /** EDT only. */
    private val shown = HashMap<String, List<RangeHighlighter>>()

    init {
        Disposer.register(parent) { latest.clear(); onEdt { clearHighlighters() } }
    }

    fun show(d: XamlDiagnostics) {
        val key = HubClient.pathKey(d.path)
        if (d.items.isEmpty()) latest.remove(key) else latest[key] = d
        onEdt { apply(key, d.path) }
    }

    /** Draws the stored result again, e.g. when the file is opened after it arrived. */
    fun reapply(file: VirtualFile) {
        val path = SpyglassProjectService.pathOf(file)
        val key = HubClient.pathKey(path)
        if (latest.containsKey(key)) onEdt { apply(key, path) }
    }

    fun clearAll() {
        latest.clear()
        onEdt { clearHighlighters() }
    }

    /** Messages for the file, for tests and tooltips. */
    fun itemsFor(path: String): List<XamlDiagnostic> = latest[HubClient.pathKey(path)]?.items ?: emptyList()

    private fun onEdt(action: () -> Unit) =
        ApplicationManager.getApplication().invokeLater({ if (!project.isDisposed) action() }, ModalityState.any())

    private fun clearHighlighters() {
        for (list in shown.values) list.forEach { if (it.isValid) it.dispose() }
        shown.clear()
    }

    private fun apply(key: String, path: String) {
        shown.remove(key)?.forEach { if (it.isValid) it.dispose() }
        val d = latest[key] ?: return
        val file = LocalFileSystem.getInstance().findFileByNioFile(Paths.get(path)) ?: return
        val document = FileDocumentManager.getInstance().getCachedDocument(file) ?: return
        val markup = DocumentMarkupModel.forDocument(document, project, true)
        val text = document.immutableCharSequence
        shown[key] = d.items.map { item ->
            val r = DiagnosticRanges.range(text, item.line, item.column)
            val attributes = when (item.severity) {
                "error" -> CodeInsightColors.ERRORS_ATTRIBUTES
                "warning" -> CodeInsightColors.WARNINGS_ATTRIBUTES
                else -> CodeInsightColors.WEAK_WARNING_ATTRIBUTES
            }
            val end = maxOf(r.first, minOf(r.last + 1, document.textLength))
            markup.addRangeHighlighter(attributes, r.first.coerceAtMost(document.textLength), end, HighlighterLayer.ERROR + 1, HighlighterTargetArea.EXACT_RANGE).apply {
                val message = "Spyglass hot reload: ${item.message}" + (item.code?.let { " ($it)" } ?: "")
                errorStripeTooltip = message
                gutterIconRenderer = DiagnosticGutterIcon(item.severity, message)
            }
        }
    }
}

private class DiagnosticGutterIcon(private val severity: String, private val message: String) : GutterIconRenderer() {
    override fun getIcon(): Icon = when (severity) {
        "error" -> AllIcons.General.Error
        "warning" -> AllIcons.General.Warning
        else -> AllIcons.General.Information
    }

    override fun getTooltipText(): String = message
    override fun isNavigateAction(): Boolean = false
    override fun equals(other: Any?) = other is DiagnosticGutterIcon && other.severity == severity && other.message == message
    override fun hashCode() = severity.hashCode() * 31 + message.hashCode()
}
