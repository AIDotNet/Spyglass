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

// ===== Avalonia XAML IntelliSense (design/xaml-language/README.md); capability "xamlLanguage" in ide/hello =====
// Offsets are UTF-16 code units into Text (what JavaScript strings and .NET strings index).

/// <summary>xaml/prepare (I→H ntf): an .axaml was opened; start loading its project's types.</summary>
public sealed record XamlPrepareParams(string Path);

/// <summary>xaml/completion (I→H req): the full text of the editor buffer and the cursor.</summary>
public sealed record XamlCompletionParams(string Path, string Text, int Offset);

/// <summary>An edit besides the completion itself, e.g. the xmlns declaration a component library's element needs.</summary>
public sealed record XamlTextEdit(int Offset, int Length, string Text);

/// <summary>
/// One completion. <see cref="Kind"/>: element, property, attachedProperty, event, value, markupExtension, namespace,
/// directive, type, resource, name, closingTag, owner, method. <see cref="InsertText"/> defaults to the label;
/// <see cref="Snippet"/>: it contains $1-style tab stops. <see cref="Retrigger"/>: ask again after inserting (Grid. → Row).
/// <see cref="DocId"/>: pass to xaml/documentation for the summary.
/// </summary>
public sealed record XamlCompletionItem(
    string Label,
    string Kind,
    string? Detail = null,
    string? InsertText = null,
    bool Snippet = false,
    string? DocId = null,
    bool Deprecated = false,
    bool Retrigger = false,
    string? SortText = null,
    XamlTextEdit? AdditionalEdit = null);

/// <summary>The items replace Text[From..Offset]. <see cref="Incomplete"/>: types are still loading, ask again.</summary>
public sealed record XamlCompletionResult(int From, IReadOnlyList<XamlCompletionItem> Items, bool Incomplete = false, string? Message = null);

/// <summary>xaml/hover (I→H req).</summary>
public sealed record XamlHoverParams(string Path, string Text, int Offset);

/// <summary>Markdown for Text[From..To], or null.</summary>
public sealed record XamlHoverResult(string? Markdown, int From = 0, int To = 0);

/// <summary>xaml/documentation (I→H req): the summary of a completion item's DocId.</summary>
public sealed record XamlDocumentationParams(string Path, string DocId);
public sealed record XamlDocumentationResult(string? Markdown);
