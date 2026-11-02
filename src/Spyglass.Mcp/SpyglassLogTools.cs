using System.ComponentModel;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Spyglass.App.Core;
using Spyglass.Protocol;

namespace Spyglass.Mcp;

/// <summary>Avalonia log tools (design/mcp/01-tools.md #8).</summary>
[McpServerToolType]
public sealed class SpyglassLogTools(SpyglassHub hub)
{
    const string AppParam = "Connected app (id like 'a1', name, or pid). Optional when only one app is connected.";

    static readonly string[] Levels = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    [McpServerTool(Name = "logs_read", Title = "Read Avalonia logs", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Reads Avalonia's own log output from the app (layout, visual, property, control, animation, binding… warnings and errors), newest last, each with the element it came from. Pass the returned cursor as `since` after changing code to see only new entries.")]
    public async Task<string> LogsRead(
        [Description("Cursor from a previous call; only entries logged after it are returned.")] long since = 0,
        [Description("Minimum level: Verbose, Debug, Information, Warning, Error or Fatal. Default Warning.")] string minLevel = "Warning",
        [Description("Only this area, e.g. Binding, Layout, Visual, Property, Control, Animations.")] string? area = null,
        [Description("Maximum entries (the newest are kept). Default 50.")] int limit = 50,
        [Description(AppParam)] string? app = null)
    {
        var s = ElementResolver.App(hub, app);
        if (!s.Has(Capabilities.Logs))
            throw new McpException($"{s.App.Name}'s Spyglass agent does not capture logs; update Spyglass.Agent.");
        var level = Normalize(minLevel);
        var r = await s.GetLogsAsync(since, level, string.IsNullOrWhiteSpace(area) ? null : area, Math.Clamp(limit, 1, 500));
        var cursor = r.Entries.Count == 0 ? since : r.Entries.Max(e => e.Seq);

        var notes = new StringBuilder();
        if (Rank(level) < Rank(r.MinLevel))
            notes.Append($"\nnote: the app records {r.MinLevel} and above, so {level} entries are not available (the Spyglass Logs page can lower it).");
        if (r.Areas is { } areas)
            notes.Append($"\nnote: the app records only these areas: {string.Join(", ", areas)}.");

        if (r.Entries.Count == 0)
            return (since > 0 ? $"No new log entries since cursor {since}" : "No log entries") +
                   $" at {level} or above{(area is null ? "" : $" in area {area}")}." + notes;

        var sb = new StringBuilder($"{r.Entries.Count} log entr{(r.Entries.Count == 1 ? "y" : "ies")} (cursor: {cursor})");
        if (r.Omitted > 0) sb.Append($"; {r.Omitted} older omitted (raise `limit` or narrow `minLevel`/`area`)");
        sb.Append(".\n");
        foreach (var e in r.Entries)
        {
            sb.Append($"{e.Time:HH:mm:ss.fff} {Short(e.Level)} [{e.Area}] {e.Message.Replace("\n", " ⏎ ")}");
            if (e.SourceType is not null)
                sb.Append($"  ← {e.SourceType}{(e.SourceId is { } id ? $" [e{id}]" : " (gone)")}");
            sb.Append('\n');
        }
        return (sb.ToString().TrimEnd() + notes).TrimEnd();
    }

    static string Normalize(string level)
    {
        var l = level.Trim().ToLowerInvariant();
        return Levels.FirstOrDefault(x => x.Equals(l, StringComparison.OrdinalIgnoreCase)) ?? l switch
        {
            "info" => "Information",
            "warn" => "Warning",
            "err" => "Error",
            "trace" => "Verbose",
            _ => throw new McpException($"Unknown minLevel '{level}'. Use one of: {string.Join(", ", Levels)}."),
        };
    }

    static int Rank(string level) => Array.FindIndex(Levels, x => x.Equals(level, StringComparison.OrdinalIgnoreCase));

    static string Short(string level) => level switch
    {
        "Verbose" => "VRB",
        "Debug" => "DBG",
        "Information" => "INF",
        "Warning" => "WRN",
        "Error" => "ERR",
        "Fatal" => "FTL",
        _ => level.ToUpperInvariant(),
    };
}
