namespace Spyglass.Protocol.Messages;

// ===== IDE extensions ↔ hub (v0.3, design/hot-reload/README.md §7.1): ws://127.0.0.1:29500/ide =====

/// <summary>ide/hello (I→H req). <see cref="Token"/> comes from ~/.spyglass/hub-PORT.json (written by the hub, mode 600).</summary>
public sealed record IdeHelloParams(string Ide, string Version, IReadOnlyList<string> WorkspaceFolders, string Token);

public sealed record IdeHelloResult(string HubVersion, int Port, IReadOnlyList<string> Capabilities);

/// <summary>xaml/didOpen, xaml/didChange (full text), xaml/didClose (no text). Paths are absolute.</summary>
public sealed record XamlDocumentParams(string Path, long Version, string? Text = null);

/// <summary>xaml/diagnostics (H→I ntf): the newest reload result for a file; an empty list clears the file's diagnostics.</summary>
public sealed record XamlDiagnosticsParams(string Path, long Version, IReadOnlyList<XamlDiagnostic> Items, string Summary);

/// <summary>One connected app as an IDE status bar shows it.</summary>
public sealed record HotReloadAppStatus(string Id, string Name, int Pid, bool HotReload, bool Available, string? Reason, int Documents);

/// <summary>
/// hotReload/status (H→I ntf, also sent right after ide/hello). <see cref="Files"/>: every source file some connected app
/// can hot-reload; the extension sends buffers only for these. <see cref="Last"/>: one-line summary of the newest reload.
/// </summary>
public sealed record HotReloadStatusParams(IReadOnlyList<HotReloadAppStatus> Apps, IReadOnlyList<string> Files, string? Last = null, bool LastOk = true);

/// <summary>editor/reveal (H→I req): open the file and put the caret at line/column (1-based).</summary>
public sealed record EditorRevealParams(string Path, int Line, int Column);
public sealed record EditorRevealResult(bool Ok);
