using System.Text.Json.Serialization;

namespace Spyglass.Protocol.Messages;

// ===== hot reload (v0.3, design/hot-reload/README.md §7.2) =====

/// <summary>One XAML document compiled into an assembly the agent knows about.</summary>
/// <param name="Uri">avares:// URI the document was compiled under.</param>
/// <param name="Path">Absolute source path on the machine that built the app (null when unknown).</param>
/// <param name="Class">x:Class, or null for classless Styles/ResourceDictionary documents.</param>
/// <param name="Assembly">Assembly that compiled the document.</param>
public sealed record XamlDocumentInfo(string Uri, string? Path, string? Class, string Assembly);

/// <summary>
/// hotReload/manifest (A→C ntf, also the result of hotReload/getManifest). When <see cref="Available"/> is false,
/// <see cref="Reason"/> says why (Release build, no JIT, another hot reload provider is loaded…).
/// </summary>
public sealed record HotReloadManifest(bool Available, string? Reason, IReadOnlyList<XamlDocumentInfo> Documents);

public enum HotReloadSource
{
    /// <summary>The client pushed the text (IDE buffer or the hub's file watcher).</summary>
    [JsonStringEnumMemberName("direct")] Direct,
    /// <summary>The text arrived inside a .NET Hot Reload delta (XAML stamp, §6.1).</summary>
    [JsonStringEnumMemberName("stamp")] Stamp,
    /// <summary>A C# delta made the agent refresh views (§5).</summary>
    [JsonStringEnumMemberName("code")] Code,
}

/// <summary>
/// hotReload/codeUpdated (A→C ntf): a .NET Hot Reload delta (dotnet watch, VS, C# Dev Kit) was applied in the app
/// (design §5). <see cref="Types"/> are the updated types; <see cref="Refreshed"/> the elements whose bindings were
/// re-evaluated; <see cref="Warnings"/> e.g. views whose new instances may fail until a restart.
/// </summary>
public sealed record HotReloadCodeUpdate(IReadOnlyList<string> Types, int Refreshed, double Ms, IReadOnlyList<string> Warnings);

/// <summary>hotReload/apply (C→A req). <see cref="Version"/> is the sender's document version, echoed back.</summary>
public sealed record HotReloadApplyParams(string Uri, string Xaml, long Version = 0);

public enum XamlDiagnosticSeverity
{
    [JsonStringEnumMemberName("error")] Error,
    [JsonStringEnumMemberName("warning")] Warning,
    [JsonStringEnumMemberName("info")] Info,
}

/// <summary>A compile or load problem. Line/Column are 1-based and refer to the text that was sent.</summary>
public sealed record XamlDiagnostic(XamlDiagnosticSeverity Severity, string Message, int? Line = null, int? Column = null, string? Code = null);

/// <summary>
/// Result of hotReload/apply, and the payload of hotReload/applied (A→C ntf, sent after every reload whatever started it).
/// </summary>
/// <param name="Ok">The new XAML is live (compiled and applied to every live instance, and installed for new ones).</param>
/// <param name="Instances">Live instances that were re-populated.</param>
/// <param name="Ms">Wall time on the UI thread.</param>
/// <param name="Skipped">Set when nothing was done, e.g. "same content".</param>
/// <param name="RestartRequired">The change cannot be applied in place (root type or x:Class changed…).</param>
/// <param name="RolledBack">A runtime error happened while applying; instances were restored from the last good XAML.</param>
public sealed record HotReloadResult(
    string Uri,
    bool Ok,
    int Instances,
    double Ms,
    IReadOnlyList<XamlDiagnostic> Diagnostics,
    HotReloadSource Source = HotReloadSource.Direct,
    long Version = 0,
    string? Skipped = null,
    bool RestartRequired = false,
    bool RolledBack = false);
