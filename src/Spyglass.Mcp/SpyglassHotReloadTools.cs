using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Spyglass.App.Core;
using Spyglass.Protocol;

namespace Spyglass.Mcp;

/// <summary>Hot reload status (design/hot-reload/README.md §7.3).</summary>
[McpServerToolType]
public sealed class SpyglassHotReloadTools(SpyglassHub hub)
{
    [McpServerTool(Name = "hot_reload_status", Title = "Hot reload status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Shows whether XAML hot reload is active in each connected app and the most recent reloads with their errors (file:line). " +
                 "After you save a .axaml file, call this to confirm the running app picked it up, or to read the compile error.")]
    public string HotReloadStatus(
        [Description("Only this app (id like 'a1', name, or pid). Default: all connected apps.")] string? app = null,
        [Description("Maximum recent reloads to list. Default 10.")] int limit = 10)
    {
        var sessions = app is null ? hub.Sessions.ToList() : [ElementResolver.App(hub, app)];
        var sb = new StringBuilder();
        if (sessions.Count == 0) sb.AppendLine("No app is connected.");
        foreach (var s in sessions)
        {
            sb.Append($"{s.ShortId} {s.App.Name} (pid {s.App.Pid}): ");
            if (!s.Has(Capabilities.HotReload)) sb.AppendLine("hot reload not installed (add the Spyglass.HotReload package to the app, Debug builds).");
            else if (s.HotReload is not { } m) sb.AppendLine("hot reload starting…");
            else if (!m.Available) sb.AppendLine($"hot reload unavailable: {m.Reason}.");
            else sb.AppendLine($"hot reload active, {m.Documents.Count} XAML file(s); saving one of them updates the running app.");
        }

        var ids = sessions.Select(s => s.ShortId).ToHashSet();
        var recent = hub.HotReload.History.Where(e => ids.Contains(e.SessionShortId)).TakeLast(Math.Clamp(limit, 1, 100)).ToList();
        if (recent.Count == 0) return sb.Append("No reloads yet.").ToString();

        sb.AppendLine().AppendLine("Recent reloads (oldest first):");
        foreach (var e in recent)
        {
            sb.Append($"{e.Time:HH:mm:ss} {ServeCli.Describe(e)}").AppendLine();
            foreach (var d in e.Result.Diagnostics.Skip(1).Take(4))
                sb.AppendLine($"    {d.Severity.ToString().ToLowerInvariant()}{(d.Line is { } l ? $" line {l}" : "")}: {d.Message}");
        }
        return sb.ToString().TrimEnd();
    }
}
