package dev.spyglass.rider

import com.intellij.openapi.actionSystem.ActionGroup
import com.intellij.openapi.actionSystem.ActionManager
import com.intellij.openapi.actionSystem.impl.SimpleDataContext
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.ModalityState
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.popup.JBPopupFactory
import com.intellij.openapi.util.text.StringUtil
import com.intellij.openapi.wm.CustomStatusBarWidget
import com.intellij.openapi.wm.StatusBar
import com.intellij.openapi.wm.StatusBarWidget
import com.intellij.openapi.wm.StatusBarWidgetFactory
import com.intellij.ui.JBColor
import com.intellij.ui.awt.RelativePoint
import com.intellij.ui.components.JBLabel
import com.intellij.util.ui.JBUI
import com.intellij.util.ui.UIUtil
import dev.spyglass.rider.hub.ConnectionState
import java.awt.Point
import java.awt.event.MouseAdapter
import java.awt.event.MouseEvent
import javax.swing.JComponent

class SpyglassStatusBarWidgetFactory : StatusBarWidgetFactory {
    override fun getId(): String = SpyglassStatusBarWidget.ID
    override fun getDisplayName(): String = "Spyglass Hot Reload"
    override fun isAvailable(project: Project): Boolean = true
    override fun createWidget(project: Project): StatusBarWidget = SpyglassStatusBarWidget(project)
    override fun canBeEnabledOn(statusBar: StatusBar): Boolean = true
}

/** ⚡ connected apps / ✗ XAML error (red) / not running; click for the Spyglass actions. Hidden in non-Avalonia projects. */
class SpyglassStatusBarWidget(private val project: Project) : CustomStatusBarWidget {
    companion object {
        const val ID = "SpyglassHotReload"
    }

    private val label = JBLabel().apply {
        border = JBUI.Borders.empty(0, 6)
        isVisible = false
    }

    init {
        label.addMouseListener(object : MouseAdapter() {
            override fun mouseClicked(e: MouseEvent) {
                val group = ActionManager.getInstance().getAction("Spyglass.StatusBarPopup") as? ActionGroup ?: return
                val context = SimpleDataContext.builder().add(CommonDataKeys.PROJECT, project).build()
                val popup = JBPopupFactory.getInstance().createActionGroupPopup(
                    "Spyglass", group, context, JBPopupFactory.ActionSelectionAid.SPEEDSEARCH, false)
                val size = popup.content.preferredSize
                popup.show(RelativePoint(label, Point(0, -size.height)))
            }
        })
    }

    override fun ID(): String = ID
    override fun getComponent(): JComponent = label

    override fun install(statusBar: StatusBar) {
        SpyglassProjectService.getInstance(project).addChangeListener(this) {
            ApplicationManager.getApplication().invokeLater({ render() }, ModalityState.any())
        }
        render()
    }

    override fun dispose() {}

    private fun render() {
        if (project.isDisposed) return
        val service = SpyglassProjectService.getInstance(project)
        val client = service.client
        label.isVisible = client != null
        if (client == null) return
        val settings = SpyglassSettings.getInstance()
        var color: java.awt.Color? = null
        val tooltip: String
        when {
            !settings.hotReloadEnabled -> {
                label.text = "⊘ Spyglass"
                tooltip = "XAML hot reload is off (Tools › Spyglass › Toggle XAML Hot Reload)"
            }
            client.state == ConnectionState.STARTING || client.state == ConnectionState.CONNECTING -> {
                label.text = "↻ Spyglass"
                tooltip = if (client.state == ConnectionState.STARTING) "Starting spyglass serve…" else "Connecting to Spyglass…"
            }
            !client.isConnected -> {
                label.text = "Spyglass: offline"
                color = UIUtil.getInactiveTextColor()
                tooltip = "Spyglass is not running on port ${client.port}. Click for details."
            }
            else -> {
                val s = client.status
                val apps = s?.apps.orEmpty().filter { it.hotReload && it.available }
                val icon = if (!service.lastOk) "✗" else if (apps.isNotEmpty()) "⚡" else "◉"
                label.text = "$icon Spyglass" + if (apps.isNotEmpty()) ": " + apps.joinToString(", ") { it.name } else ""
                if (!service.lastOk) color = JBColor.RED
                val lines = s?.apps.orEmpty().map { a ->
                    "${a.name} (pid ${a.pid}): " + when {
                        !a.hotReload -> "no hot reload (add Spyglass.HotReload)"
                        a.available -> "${a.documents} XAML file(s)"
                        else -> "unavailable: ${a.reason}"
                    }
                }.toMutableList()
                if (lines.isEmpty()) lines += "No app connected. Run an app that calls UseSpyglass()."
                (service.lastSummary ?: s?.last)?.let { lines += ""; lines += it }
                tooltip = lines.joinToString("<br>") { StringUtil.escapeXmlEntities(it) }
            }
        }
        label.foreground = color ?: UIUtil.getLabelForeground()
        label.toolTipText = "<html>$tooltip</html>"
        label.parent?.let { it.revalidate(); it.repaint() }
    }
}
