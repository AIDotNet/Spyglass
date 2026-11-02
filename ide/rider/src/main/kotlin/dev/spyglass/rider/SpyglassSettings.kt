package dev.spyglass.rider

import com.intellij.openapi.components.BaseState
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.SimplePersistentStateComponent
import com.intellij.openapi.components.State
import com.intellij.openapi.components.Storage
import com.intellij.openapi.components.service
import com.intellij.openapi.options.BoundConfigurable
import com.intellij.openapi.project.ProjectManager
import com.intellij.openapi.ui.DialogPanel
import com.intellij.ui.dsl.builder.COLUMNS_LARGE
import com.intellij.ui.dsl.builder.bindIntText
import com.intellij.ui.dsl.builder.bindSelected
import com.intellij.ui.dsl.builder.bindText
import com.intellij.ui.dsl.builder.columns
import com.intellij.ui.dsl.builder.panel

/** Settings › Tools › Spyglass (the same settings as the VS Code extension's `spyglass.*`). */
@Service(Service.Level.APP)
@State(name = "SpyglassSettings", storages = [Storage("spyglass.xml")])
class SpyglassSettings : SimplePersistentStateComponent<SpyglassSettings.Options>(Options()) {
    class Options : BaseState() {
        /** Hub port; 0 = SPYGLASS_PORT or 29500. */
        var port by property(0)
        /** Command used to start `spyglass serve`: an executable or Spyglass.dll. */
        var command by string("spyglass")
        /** Start `spyglass serve` when nothing listens. */
        var autoStart by property(true)
        /** Send unsaved .axaml edits to running apps. */
        var hotReloadEnabled by property(true)
        /** ms without typing before an edit is sent; 0 = only on save. */
        var delayMs by property(300)
    }

    val port get() = state.port
    val command get() = state.command?.takeIf { it.isNotBlank() } ?: "spyglass"
    val autoStart get() = state.autoStart
    val delayMs get() = state.delayMs

    var hotReloadEnabled: Boolean
        get() = state.hotReloadEnabled
        set(value) {
            if (state.hotReloadEnabled == value) return
            state.hotReloadEnabled = value
            forEachService { it.enabledChanged() }
        }

    companion object {
        fun getInstance(): SpyglassSettings = service()

        internal fun forEachService(action: (SpyglassProjectService) -> Unit) {
            for (p in ProjectManager.getInstance().openProjects) {
                if (!p.isDisposed) p.getServiceIfCreated(SpyglassProjectService::class.java)?.let(action)
            }
        }
    }
}

class SpyglassConfigurable : BoundConfigurable("Spyglass") {
    private val s = SpyglassSettings.getInstance()
    private val o = s.state
    private var enabled = true

    override fun createPanel(): DialogPanel = panel {
        group("XAML Hot Reload") {
            row { checkBox("Send unsaved .axaml edits to running apps").bindSelected({ s.hotReloadEnabled }, { enabled = it }) }
            row("Delay (ms):") {
                intTextField(0..60_000).bindIntText(o::delayMs)
                    .comment("Time without typing before an edit is sent; 0 = only on save")
            }
        }
        group("Connection") {
            row("Port:") {
                intTextField(0..65_535).bindIntText(o::port).comment("0 = SPYGLASS_PORT or 29500")
            }
            row("Command:") {
                textField().bindText({ o.command ?: "spyglass" }, { o.command = it.trim() }).columns(COLUMNS_LARGE)
                    .comment("The Spyglass dotnet tool (<code>dotnet tool install -g Spyglass</code>), a path to it, or a path to Spyglass.dll")
            }
            row { checkBox("Start spyglass serve in the background when no Spyglass is running").bindSelected(o::autoStart) }
        }
    }

    override fun apply() {
        val before = Triple(o.port, o.command, o.autoStart)
        enabled = s.hotReloadEnabled
        super.apply()
        s.hotReloadEnabled = enabled
        if (before != Triple(o.port, o.command, o.autoStart)) SpyglassSettings.forEachService { it.restartClient() }
    }
}
