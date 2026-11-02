using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spyglass.VisualStudio.Core;

// The few hub ↔ IDE messages this extension uses (design/hot-reload/README.md §7.1). Copied from
// src/Spyglass.Protocol/Messages/Ide.cs and HotReload.cs on purpose: the extension builds on its own, against the wire
// format (camelCase JSON), not against the hub's assemblies.

/// <summary>JSON-RPC method names of the hub's /ide endpoint.</summary>
public static class IdeMethods
{
    public const string Path = "/ide";
    public const string Hello = "ide/hello";                     // I→H req
    public const string DidOpen = "xaml/didOpen";                // I→H ntf
    public const string DidChange = "xaml/didChange";            // I→H ntf (full text)
    public const string DidClose = "xaml/didClose";              // I→H ntf
    public const string Diagnostics = "xaml/diagnostics";        // H→I ntf
    public const string Status = "hotReload/status";             // H→I ntf
    public const string Reveal = "editor/reveal";                // H→I req
}

public static class RpcErrorCodes
{
    public const int MethodNotFound = -32601;
    public const int InternalError = -32603;
    public const int Unauthorized = -32011;
}

public sealed record IdeHelloParams(string Ide, string Version, IReadOnlyList<string> WorkspaceFolders, string Token);

public sealed record IdeHelloResult(string HubVersion, int Port, IReadOnlyList<string> Capabilities);

/// <summary>xaml/didOpen, xaml/didChange (full text), xaml/didClose (no text). Paths are absolute.</summary>
public sealed record XamlDocumentParams(string Path, long Version, string? Text = null);

public enum XamlDiagnosticSeverity { Error, Warning, Info }

/// <summary>Line and column are 1-based.</summary>
public sealed record XamlDiagnostic(XamlDiagnosticSeverity Severity, string Message, int? Line = null, int? Column = null, string? Code = null);

/// <summary>xaml/diagnostics: the newest reload result for a file; an empty list clears the file's diagnostics.</summary>
public sealed record XamlDiagnosticsParams(string Path, long Version, IReadOnlyList<XamlDiagnostic> Items, string Summary);

public sealed record HotReloadAppStatus(string Id, string Name, int Pid, bool HotReload, bool Available, string? Reason, int Documents);

/// <summary>hotReload/status: connected apps, and every file some app can hot-reload (only those are sent).</summary>
public sealed record HotReloadStatusParams(IReadOnlyList<HotReloadAppStatus> Apps, IReadOnlyList<string> Files, string? Last = null, bool LastOk = true);

/// <summary>editor/reveal: open the file and put the caret at line/column (1-based).</summary>
public sealed record EditorRevealParams(string Path, int Line, int Column);

public sealed record EditorRevealResult(bool Ok);

public static class IdeJson
{
    /// <summary>camelCase, nulls omitted, enums as strings (the hub writes "error"; reading is case-insensitive).</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
