package dev.spyglass.rider

import com.intellij.execution.ExecutionException
import com.intellij.execution.RunContentExecutor
import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.execution.process.KillableColoredProcessHandler
import com.intellij.execution.process.ProcessTerminatedListener
import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.ToggleAction
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.editor.Document
import com.intellij.openapi.fileEditor.FileDocumentManagerListener
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.fileEditor.FileEditorManagerListener
import com.intellij.openapi.project.DumbAware
import com.intellij.openapi.project.Project
import com.intellij.openapi.startup.ProjectActivity
import com.intellij.openapi.ui.popup.JBPopupFactory
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.openapi.wm.ToolWindow
import com.intellij.openapi.wm.ToolWindowFactory
import com.intellij.openapi.wm.ToolWindowManager
import com.intellij.execution.filters.TextConsoleBuilderFactory
import com.intellij.execution.ui.ConsoleViewContentType
import com.intellij.util.EnvironmentUtil
import dev.spyglass.rider.hub.HubClient
import dev.spyglass.rider.hub.ProjectFiles
import java.nio.file.Path
import java.nio.file.Paths

// ---------- startup & listeners ----------

/** Connects when the project contains .axaml files; otherwise the first opened .axaml/.xaml file does. */
class SpyglassStartupActivity : ProjectActivity {
    override suspend fun execute(project: Project) {
        val base = project.basePath ?: return
        if (ProjectFiles.find(Paths.get(base), setOf("axaml"), limit = 1).isNotEmpty())
            SpyglassProjectService.getInstance(project).ensureStarted()
    }
}

class SpyglassFileEditorListener(private val project: Project) : FileEditorManagerListener {
    override fun fileOpened(source: FileEditorManager, file: VirtualFile) {
        if (SpyglassProjectService.isXaml(file)) SpyglassProjectService.getInstance(project).onFileOpened(file)
    }

    override fun fileClosed(source: FileEditorManager, file: VirtualFile) {
        if (SpyglassProjectService.isXaml(file)) project.getServiceIfCreated(SpyglassProjectService::class.java)?.onFileClosed(file)
    }
}

class SpyglassSaveListener : FileDocumentManagerListener {
    override fun beforeDocumentSaving(document: Document) = SpyglassSettings.forEachService { it.onDocumentSaving(document) }
}

// ---------- log tool window ----------

class SpyglassToolWindowFactory : ToolWindowFactory, DumbAware {
    override fun createToolWindowContent(project: Project, toolWindow: ToolWindow) {
        val console = TextConsoleBuilderFactory.getInstance().createBuilder(project).apply { setViewer(true) }.console
        val content = toolWindow.contentManager.factory.createContent(console.component, "Log", false)
        content.setDisposer(console)
        toolWindow.contentManager.addContent(content)
        SpyglassProjectService.getInstance(project).subscribeLog(console) { console.print(it + "\n", ConsoleViewContentType.NORMAL_OUTPUT) }
    }
}

// ---------- actions ----------

private fun AnActionEvent.service(): SpyglassProjectService? = project?.let { SpyglassProjectService.getInstance(it) }

abstract class SpyglassAction : AnAction(), DumbAware {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT
    override fun update(e: AnActionEvent) {
        e.presentation.isEnabledAndVisible = e.project != null
    }
}

class ToggleHotReloadAction : ToggleAction(), DumbAware {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT
    override fun isSelected(e: AnActionEvent) = SpyglassSettings.getInstance().hotReloadEnabled
    override fun setSelected(e: AnActionEvent, state: Boolean) {
        SpyglassSettings.getInstance().hotReloadEnabled = state
    }
}

class ReconnectAction : SpyglassAction() {
    override fun actionPerformed(e: AnActionEvent) {
        e.service()?.let {
            it.log("Reconnecting…")
            it.reconnect()
        }
    }
}

class ShowLogAction : SpyglassAction() {
    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        ToolWindowManager.getInstance(project).getToolWindow("Spyglass")?.show()
    }
}

/** Starts the Spyglass window (the configured command without arguments). */
class OpenSpyglassAction : SpyglassAction() {
    override fun actionPerformed(e: AnActionEvent) {
        val service = e.service() ?: return
        val env = EnvironmentUtil.getEnvironmentMap()
        val argv = HubClient.launchCommandLine(SpyglassSettings.getInstance().command)
        val exe = HubClient.findExecutable(argv[0], env)
        if (exe == null) {
            notify(e.project!!, "'${argv[0]}' not found. Install Spyglass with <code>dotnet tool install -g TKCode.Spyglass</code>, or set the command in Settings › Tools › Spyglass.", NotificationType.WARNING)
            return
        }
        try {
            ProcessBuilder(listOf(exe) + argv.drop(1))
                .redirectOutput(ProcessBuilder.Redirect.DISCARD)
                .redirectError(ProcessBuilder.Redirect.DISCARD)
                .apply { environment().clear(); environment().putAll(env) }
                .start().outputStream.close()
            service.log("Started ${argv.joinToString(" ")}")
        } catch (ex: Exception) {
            notify(e.project!!, "Cannot start Spyglass: ${ex.message}", NotificationType.ERROR)
        }
    }
}

/** `dotnet watch run --project X.csproj` in the Run tool window, so C# edits apply without restarting. */
class RunWithDotnetWatchAction : SpyglassAction() {
    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val base = project.basePath ?: return
        ApplicationManager.getApplication().executeOnPooledThread {
            val projects = ProjectFiles.find(Paths.get(base), setOf("csproj"), limit = 50)
            ApplicationManager.getApplication().invokeLater {
                when {
                    project.isDisposed -> {}
                    projects.isEmpty() -> notify(project, "No .csproj in this project.", NotificationType.WARNING)
                    projects.size == 1 -> run(project, projects[0])
                    else -> JBPopupFactory.getInstance()
                        .createPopupChooserBuilder(projects)
                        .setTitle("Project to Run with dotnet watch")
                        .setRenderer(com.intellij.ui.SimpleListCellRenderer.create("") { Paths.get(base).relativize(it).toString() })
                        .setItemChosenCallback { run(project, it) }
                        .createPopup()
                        .showCenteredInCurrentWindow(project)
                }
            }
        }
    }

    private fun run(project: Project, csproj: Path) {
        val env = EnvironmentUtil.getEnvironmentMap()
        val dotnet = HubClient.findExecutable("dotnet", env) ?: "dotnet"
        val cl = GeneralCommandLine(dotnet, "watch", "run", "--project", csproj.toString())
            .withWorkDirectory(csproj.parent.toFile())
            .withParentEnvironmentType(GeneralCommandLine.ParentEnvironmentType.CONSOLE)
            // No terminal to answer dotnet watch's "restart?" prompt for edits it cannot apply: restart automatically.
            .withEnvironment("DOTNET_WATCH_RESTART_ON_RUDE_EDIT", "true")
        try {
            val handler = KillableColoredProcessHandler(cl)
            ProcessTerminatedListener.attach(handler)
            RunContentExecutor(project, handler)
                .withTitle("dotnet watch: ${csproj.fileName}")
                .withActivateToolWindow(true)
                .withStop({ handler.destroyProcess() }, { !handler.isProcessTerminated })
                .run()
        } catch (ex: ExecutionException) {
            notify(project, "Cannot run dotnet watch: ${ex.message}", NotificationType.ERROR)
        }
    }
}

internal fun notify(project: Project, content: String, type: NotificationType) {
    NotificationGroupManager.getInstance().getNotificationGroup("Spyglass").createNotification(content, type).notify(project)
}
