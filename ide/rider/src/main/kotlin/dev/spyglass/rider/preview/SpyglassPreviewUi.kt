// Tool window, panel and actions of the design-time preview (design/preview/README.md §4).
package dev.spyglass.rider.preview

import com.intellij.icons.AllIcons
import com.intellij.openapi.Disposable
import com.intellij.openapi.actionSystem.ActionPlaces
import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.fileEditor.FileEditorManagerEvent
import com.intellij.openapi.fileEditor.FileEditorManagerListener
import com.intellij.openapi.project.DumbAware
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.openapi.wm.ToolWindow
import com.intellij.openapi.wm.ToolWindowFactory
import com.intellij.openapi.wm.ToolWindowManager
import com.intellij.openapi.wm.ex.ToolWindowManagerListener
import com.intellij.ui.JBColor
import com.intellij.ui.components.JBLabel
import com.intellij.util.ui.JBUI
import com.intellij.util.ui.UIUtil
import dev.spyglass.rider.SpyglassProjectService
import dev.spyglass.rider.SpyglassSettings
import java.awt.BorderLayout
import java.awt.CardLayout
import java.awt.Color
import java.awt.Cursor
import java.awt.Dimension
import java.awt.FlowLayout
import java.awt.Graphics
import java.awt.Graphics2D
import java.awt.RenderingHints
import java.awt.event.ComponentAdapter
import java.awt.event.ComponentEvent
import java.awt.event.KeyAdapter
import java.awt.event.KeyEvent
import java.awt.event.MouseAdapter
import java.awt.event.MouseEvent
import java.awt.event.MouseWheelEvent
import javax.swing.BoxLayout
import javax.swing.JButton
import javax.swing.JComboBox
import javax.swing.JProgressBar
import javax.swing.JComponent
import javax.swing.JPanel
import javax.swing.SwingConstants

const val PREVIEW_TOOL_WINDOW_ID = "Spyglass Preview"

class SpyglassPreviewToolWindowFactory : ToolWindowFactory, DumbAware {
    override fun createToolWindowContent(project: Project, toolWindow: ToolWindow) {
        val service = SpyglassPreviewService.getInstance(project)
        val panel = PreviewPanel(service)
        val content = toolWindow.contentManager.factory.createContent(panel, "", false)
        content.setDisposer(panel)
        toolWindow.contentManager.addContent(content)
        service.addListener(panel) { panel.refresh() }
        toolWindow.setTitleActions(listOf(RestartPreviewAction()))

        // The host runs only while the window is visible. The content is created when the window is first shown, so the
        // state-change event for that first show has already passed: start it here.
        var wasVisible = toolWindow.isVisible
        project.messageBus.connect(panel).subscribe(ToolWindowManagerListener.TOPIC, object : ToolWindowManagerListener {
            override fun stateChanged(toolWindowManager: ToolWindowManager) {
                val tw = toolWindowManager.getToolWindow(PREVIEW_TOOL_WINDOW_ID) ?: return
                val visible = tw.isVisible
                if (visible == wasVisible) return
                wasVisible = visible
                if (visible) service.onShown() else service.onHidden()
            }
        })
        if (toolWindow.isVisible) service.onShown()
    }
}

/** Follows the active editor (the preview shows the .axaml file that has focus) and opens the preview for new files (autoOpen). */
class SpyglassPreviewEditorListener(private val project: Project) : FileEditorManagerListener {
    override fun selectionChanged(event: FileEditorManagerEvent) {
        val file = event.newFile ?: return
        project.getServiceIfCreated(SpyglassPreviewService::class.java)?.fileSelected(file)
    }

    override fun fileOpened(source: FileEditorManager, file: VirtualFile) {
        val settings = SpyglassSettings.getInstance()
        if (EmbeddedPreviews.splitEditorOf(project, file) != null) return   // the file's own editor already has the preview pane
        val tw = ToolWindowManager.getInstance(project).getToolWindow(PREVIEW_TOOL_WINDOW_ID) ?: return
        if (!AutoOpenPolicy.shouldOpen(settings.autoOpen, settings.previewEnabled, SpyglassProjectService.isXaml(file), tw.isVisible)) return
        SpyglassPreviewService.getInstance(project).requestFile(file)
        tw.activate(null, false, false)   // show without taking the focus from the editor
    }
}

private class RestartPreviewAction : AnAction("Restart Preview", "Restart the previewer host", AllIcons.Actions.Refresh), DumbAware {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT
    override fun actionPerformed(e: AnActionEvent) { e.project?.let { SpyglassPreviewService.getInstance(it).restart() } }
}

/** Tools › Spyglass › Open Preview, the editor title button, the editor context menu, Find Action and the shortcut. */
class OpenPreviewAction : AnAction(), DumbAware {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT
    override fun update(e: AnActionEvent) {
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE)
        val isXaml = file != null && SpyglassProjectService.isXaml(file)
        // The editor title button and the context menu appear only for .axaml/.xaml; the Tools menu and Find Action always do.
        // The shortcut works only inside an .axaml/.xaml editor (like VS Code's `when` clause).
        val shortcut = e.place == ActionPlaces.KEYBOARD_SHORTCUT
        e.presentation.isEnabledAndVisible = e.project != null && (isXaml || !(shortcut || e.isFromContextMenu || e.place.contains("Editor", ignoreCase = true)))
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val tw = ToolWindowManager.getInstance(project).getToolWindow(PREVIEW_TOOL_WINDOW_ID) ?: return
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE)?.takeIf { SpyglassProjectService.isXaml(it) }
        if (file != null && EmbeddedPreviews.reveal(project, file)) return   // shown to the right of the editor
        file?.let { SpyglassPreviewService.getInstance(project).requestFile(it) }
        // Already open → focus it; otherwise show it (onShown() starts the host for the requested or active file).
        if (tw.isVisible) tw.activate(null, true, true) else tw.show(null)
    }
}

class RestartPreviewMenuAction : AnAction(), DumbAware {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT
    override fun actionPerformed(e: AnActionEvent) { e.project?.let { SpyglassPreviewService.getInstance(it).restart() } }
}

/** Banner above an .axaml editor: "Open Preview to the Side" (hidden while the preview is open). */
class SpyglassPreviewBannerProvider : com.intellij.ui.EditorNotificationProvider, DumbAware {
    override fun collectNotificationData(project: Project, file: VirtualFile): java.util.function.Function<in com.intellij.openapi.fileEditor.FileEditor, out JComponent?>? {
        if (!SpyglassProjectService.isXaml(file) || !SpyglassSettings.getInstance().previewEnabled) return null
        if (EmbeddedPreviews.splitEditorOf(project, file) != null) return null   // the split editor already offers it
        val service = project.getServiceIfCreated(SpyglassPreviewService::class.java)
        if (service?.active == true) return null
        return java.util.function.Function { editor ->
            com.intellij.ui.EditorNotificationPanel(editor, com.intellij.ui.EditorNotificationPanel.Status.Info).apply {
                text = "Preview this XAML without running the app"
                createActionLabel("Open Preview to the Side") {
                    SpyglassPreviewService.getInstance(project).requestFile(file)
                    ToolWindowManager.getInstance(project).getToolWindow(PREVIEW_TOOL_WINDOW_ID)?.show(null)
                }.apply { toolTipText = "Open Preview to the Side" }
            }
        }
    }
}

/** The preview: toolbar (zoom, theme, restart), frames from the host or a message, an error bar, and a status line. */
class PreviewPanel(private val service: SpyglassPreviewService) : JPanel(BorderLayout()), Disposable {
    private val view = ViewPanel()
    private val errorBar = JBLabel().apply {
        isOpaque = true
        background = JBColor(Color(0xFFE5E5), Color(0x4A2B2B))
        foreground = JBColor(Color(0x8B0000), Color(0xFF8A8A))
        border = JBUI.Borders.empty(4, 8)
        cursor = Cursor.getPredefinedCursor(Cursor.HAND_CURSOR)
        isVisible = false
        addMouseListener(object : MouseAdapter() {
            override fun mouseClicked(e: MouseEvent) = service.navigateToError()
        })
    }
    private val messageLabel = JBLabel("", SwingConstants.CENTER)
    private val actionButton = JButton()
    private val progress = JProgressBar().apply { isIndeterminate = true; isVisible = false; preferredSize = Dimension(160, 8) }
    private val messagePanel = JPanel().apply {
        layout = BoxLayout(this, BoxLayout.Y_AXIS)
        border = JBUI.Borders.empty(16)
        add(messageLabel.apply { alignmentX = 0.5f })
        add(javax.swing.Box.createVerticalStrut(8))
        add(progress.apply { alignmentX = 0.5f })
        add(javax.swing.Box.createVerticalStrut(8))
        add(actionButton.apply { alignmentX = 0.5f })
    }
    private val cards = JPanel(CardLayout())
    private val zoomBox = JComboBox(arrayOf("Fit") + PreviewZoom.STEPS.map { "${it.toLong()}%" }).apply { toolTipText = "Zoom (Ctrl+wheel)" }
    private val themeBox = JComboBox(arrayOf("Auto", "Light", "Dark")).apply { toolTipText = "Theme variant (Window roots)" }
    private val dot = StatusDot()
    private val statusLabel = JBLabel(" ")
    private var updatingControls = false

    init {
        val viewCard = JPanel(BorderLayout()).apply { add(view, BorderLayout.CENTER); add(errorBar, BorderLayout.SOUTH) }
        cards.add(viewCard, "view")
        cards.add(JPanel(java.awt.GridBagLayout()).apply { add(messagePanel) }, "message")
        val toolbar = JPanel(FlowLayout(FlowLayout.LEFT, 6, 2)).apply {
            add(JBLabel("Zoom:")); add(zoomBox); add(JBLabel("Theme:")); add(themeBox)
        }
        val status = JPanel(FlowLayout(FlowLayout.LEFT, 6, 2)).apply { add(dot); add(statusLabel) }
        add(toolbar, BorderLayout.NORTH)
        add(cards, BorderLayout.CENTER)
        add(status, BorderLayout.SOUTH)
        zoomBox.addActionListener {
            if (!updatingControls) service.setZoom(if (zoomBox.selectedIndex <= 0) PreviewZoom.FIT else PreviewZoom(false, PreviewZoom.STEPS[zoomBox.selectedIndex - 1]))
        }
        themeBox.addActionListener {
            if (!updatingControls) service.setTheme(PreviewTheme.entries[themeBox.selectedIndex.coerceIn(0, 2)])
        }
        actionButton.addActionListener {
            if (service.needsBuild && service.buildTarget != null && service.phase == PreviewPhase.UNAVAILABLE) service.build() else service.restart()
        }
        refresh()
    }

    fun refresh() {
        val phase = service.phase
        val showView = service.frame != null && phase != PreviewPhase.UNAVAILABLE && phase != PreviewPhase.EXITED && phase != PreviewPhase.DISABLED
        (cards.layout as CardLayout).show(cards, if (showView || phase == PreviewPhase.READY) "view" else "message")
        val err = service.error
        errorBar.isVisible = err?.error != null
        if (err?.error != null) {
            val where = if (err.line != null) " (line ${err.line}${err.column?.let { ", column $it" } ?: ""})" else ""
            errorBar.text = "<html>" + escape(SpyglassPreviewService.firstLine(err.error)) + escape(where) + "</html>"
            errorBar.toolTipText = "<html>Click to go to the error<pre>" + escape(err.error) + "</pre></html>"
        }
        messageLabel.text = service.message ?: ""
        progress.isVisible = phase == PreviewPhase.LOADING || phase == PreviewPhase.STARTING
        val label = when {
            phase == PreviewPhase.UNAVAILABLE && service.needsBuild -> "Build Project"
            phase == PreviewPhase.EXITED -> "Restart Preview"
            phase == PreviewPhase.UNAVAILABLE -> "Try Again"
            else -> null
        }
        actionButton.isVisible = label != null
        if (label != null) actionButton.text = label

        updatingControls = true
        zoomBox.selectedIndex = if (service.zoom.fit) 0 else PreviewZoom.STEPS.indexOf(service.zoom.percent).let { if (it < 0) 0 else it + 1 }
        themeBox.selectedIndex = service.theme.ordinal
        updatingControls = false

        val (level, text) = PreviewStatus.describe(phase, service.updating, service.lastUpdateMs, err?.error?.let(SpyglassPreviewService::firstLine), service.message)
        dot.level = level
        statusLabel.text = text
        statusLabel.toolTipText = text
        view.repaint()
    }

    private fun escape(s: String) = s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")

    override fun dispose() {}

    /** A small coloured circle: grey idle, amber busy, green ok, red error. */
    private class StatusDot : JComponent() {
        var level = StatusLevel.IDLE
            set(v) { field = v; repaint() }
        init { preferredSize = Dimension(12, 12) }
        override fun paintComponent(g: Graphics) {
            val g2 = g.create() as Graphics2D
            try {
                g2.setRenderingHint(RenderingHints.KEY_ANTIALIASING, RenderingHints.VALUE_ANTIALIAS_ON)
                g2.color = when (level) {
                    StatusLevel.IDLE -> JBColor.GRAY
                    StatusLevel.BUSY -> JBColor(Color(0xE0A800), Color(0xE0A800))
                    StatusLevel.OK -> JBColor(Color(0x2E9E4F), Color(0x4FC46E))
                    StatusLevel.ERROR -> JBColor.RED
                }
                g2.fillOval(1, 1, 10, 10)
            } finally { g2.dispose() }
        }
    }

    private inner class ViewPanel : JComponent() {
        private var offsetX = 0.0
        private var offsetY = 0.0
        private var scale = 1.0

        init {
            isFocusable = true
            focusTraversalKeysEnabled = false
            preferredSize = Dimension(320, 240)
            addComponentListener(object : ComponentAdapter() {
                override fun componentResized(e: ComponentEvent) = reportSize()
                override fun componentShown(e: ComponentEvent) = reportSize()
            })
            val mouse = object : MouseAdapter() {
                override fun mousePressed(e: MouseEvent) {
                    requestFocusInWindow()
                    toLogical(e)?.let { (x, y) -> service.send(Messages.pointerPressed(x, y, button(e), modifiers(e))) }
                }
                override fun mouseReleased(e: MouseEvent) {
                    toLogical(e)?.let { (x, y) -> service.send(Messages.pointerReleased(x, y, button(e), modifiers(e))) }
                }
                override fun mouseMoved(e: MouseEvent) { toLogical(e)?.let { (x, y) -> service.send(Messages.pointerMoved(x, y, modifiers(e))) } }
                override fun mouseDragged(e: MouseEvent) { toLogical(e)?.let { (x, y) -> service.send(Messages.pointerMoved(x, y, modifiers(e))) } }
                override fun mouseWheelMoved(e: MouseWheelEvent) {
                    if (e.isControlDown || e.isMetaDown) {   // Ctrl/Cmd + wheel zooms the picture instead of scrolling it
                        service.setZoom(if (e.preciseWheelRotation < 0) PreviewZoom.zoomIn(scale) else PreviewZoom.zoomOut(scale))
                        return
                    }
                    toLogical(e)?.let { (x, y) -> service.send(Messages.scroll(x, y, 0.0, PreviewInput.wheelNotches(e.preciseWheelRotation), modifiers(e))) }
                }
            }
            addMouseListener(mouse); addMouseMotionListener(mouse); addMouseWheelListener(mouse)
            addKeyListener(object : KeyAdapter() {
                override fun keyPressed(e: KeyEvent) { PreviewInput.avaloniaKey(e.keyCode, e.keyLocation)?.let { service.send(Messages.key(true, it, keySymbol(e), modifiers(e))) } }
                override fun keyReleased(e: KeyEvent) { PreviewInput.avaloniaKey(e.keyCode, e.keyLocation)?.let { service.send(Messages.key(false, it, keySymbol(e), modifiers(e))) } }
                override fun keyTyped(e: KeyEvent) {
                    val c = e.keyChar
                    if (c.code >= 32 && c.code != 127 && !e.isControlDown && !e.isMetaDown) service.send(Messages.text(c.toString(), modifiers(e)))
                }
            })
        }

        private fun ideScale(): Double = com.intellij.ui.scale.JBUIScale.sysScale(this).toDouble().coerceAtLeast(1.0)

        private fun reportSize() {
            if (width > 0 && height > 0) service.viewportChanged(width.toDouble(), height.toDouble(), ideScale())
        }

        override fun paintComponent(g: Graphics) {
            g.color = UIUtil.getPanelBackground()
            g.fillRect(0, 0, width, height)
            val img = service.frame ?: return
            val frameScale = service.frameScale
            val w = img.width / frameScale
            val h = img.height / frameScale
            scale = service.zoom.scale(w, h, width.toDouble(), height.toDouble())
            offsetX = maxOf(0.0, (width - w * scale) / 2)
            offsetY = maxOf(0.0, (height - h * scale) / 2)
            val g2 = g.create() as Graphics2D
            try {
                g2.setRenderingHint(RenderingHints.KEY_INTERPOLATION, if (scale >= 1.0) RenderingHints.VALUE_INTERPOLATION_NEAREST_NEIGHBOR else RenderingHints.VALUE_INTERPOLATION_BILINEAR)
                g2.drawImage(img, offsetX.toInt(), offsetY.toInt(), (w * scale).toInt(), (h * scale).toInt(), null)
            } finally { g2.dispose() }
        }

        /** Component coordinates → the previewed window's logical position (DIPs). */
        private fun toLogical(e: MouseEvent): Pair<Double, Double>? {
            if (service.frame == null) return null
            return PreviewInput.toLogical(e.x - offsetX, e.y - offsetY, scale)
        }

        private fun button(e: MouseEvent) = when (e.button) {
            MouseEvent.BUTTON1 -> MouseButtons.LEFT
            MouseEvent.BUTTON2 -> MouseButtons.MIDDLE
            MouseEvent.BUTTON3 -> MouseButtons.RIGHT
            else -> MouseButtons.NONE
        }

        /** Press events carry the pressed button in modifiersEx; release events do not (Swing reports it in `button`). */
        private fun modifiers(e: java.awt.event.InputEvent): List<Int> {
            val m = e.modifiersEx
            return PreviewInput.modifiers(
                control = e.isControlDown, shift = e.isShiftDown, alt = e.isAltDown,
                meta = e.isMetaDown && !(e is MouseEvent && e.button == MouseEvent.BUTTON3),
                left = m and java.awt.event.InputEvent.BUTTON1_DOWN_MASK != 0,
                right = m and java.awt.event.InputEvent.BUTTON3_DOWN_MASK != 0,
                middle = m and java.awt.event.InputEvent.BUTTON2_DOWN_MASK != 0,
            )
        }

        private fun keySymbol(e: KeyEvent): String? = e.keyChar.takeIf { it != KeyEvent.CHAR_UNDEFINED && it.code >= 32 }?.toString()
    }
}
