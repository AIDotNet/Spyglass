// Wire types of the Spyglass hub's /ide endpoint (design/hot-reload/README.md §7.1; C#: Spyglass.Protocol/Messages/Ide.cs).
// camelCase JSON, parsed with Gson (bundled with the IntelliJ Platform). Every property has a default so Kotlin generates a
// no-arg constructor and Gson keeps the defaults for absent fields.
package dev.spyglass.rider.hub

object Methods {
    const val IDE_HELLO = "ide/hello"
    const val XAML_DID_OPEN = "xaml/didOpen"
    const val XAML_DID_CHANGE = "xaml/didChange"
    const val XAML_DID_CLOSE = "xaml/didClose"
    const val XAML_DIAGNOSTICS = "xaml/diagnostics"
    const val HOT_RELOAD_STATUS = "hotReload/status"
    const val EDITOR_REVEAL = "editor/reveal"
}

data class IdeHelloParams(
    val ide: String = "",
    val version: String = "",
    val workspaceFolders: List<String> = emptyList(),
    val token: String = "",
)

data class IdeHelloResult(val hubVersion: String = "", val port: Int = 0, val capabilities: List<String> = emptyList())

/** xaml/didOpen, xaml/didChange (full text), xaml/didClose (no text). Paths are absolute, in the OS's form. */
data class XamlDocumentParams(val path: String = "", val version: Long = 0, val text: String? = null)

/** One reload diagnostic; [line]/[column] are 1-based. [severity] is "error", "warning" or "info". */
data class XamlDiagnostic(
    val severity: String = "error",
    val message: String = "",
    val line: Int? = null,
    val column: Int? = null,
    val code: String? = null,
) {
    val isError: Boolean get() = severity == "error"
}

/** xaml/diagnostics: the newest reload result for a file; an empty [items] clears the file's diagnostics. */
data class XamlDiagnostics(
    val path: String = "",
    val version: Long = 0,
    val items: List<XamlDiagnostic> = emptyList(),
    val summary: String = "",
)

data class AppStatus(
    val id: String = "",
    val name: String = "",
    val pid: Int = 0,
    val hotReload: Boolean = false,
    val available: Boolean = false,
    val reason: String? = null,
    val documents: Int = 0,
)

/** hotReload/status: [files] are the source files some connected app can hot-reload; only these are sent. */
data class HotReloadStatus(
    val apps: List<AppStatus> = emptyList(),
    val files: List<String> = emptyList(),
    val last: String? = null,
    val lastOk: Boolean = true,
)

/** editor/reveal: open [path] with the caret at [line]/[column] (1-based). */
data class RevealRequest(val path: String = "", val line: Int = 1, val column: Int = 1)

data class RevealResult(val ok: Boolean)
