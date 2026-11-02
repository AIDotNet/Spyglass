using System.Text.RegularExpressions;
using ModelContextProtocol;
using Spyglass.App.Core;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Mcp;

/// <summary>Resolves the `app` and `element` tool arguments (design/mcp/README.md §5).</summary>
internal static class ElementResolver
{
    public static AgentSession App(SpyglassHub hub, string? app)
    {
        var sessions = hub.Sessions;
        if (sessions.Count == 0)
            throw new McpException("No app connected. Add `.UseSpyglass()` to the app's AppBuilder (Debug builds), start it, " +
                                   $"and make sure Spyglass is listening on 127.0.0.1:{hub.Port}.");
        if (string.IsNullOrWhiteSpace(app))
        {
            if (sessions.Count == 1) return sessions[0];
            throw new McpException("Several apps are connected; pass `app`: " +
                                   string.Join(", ", sessions.Select(s => $"{s.ShortId} ({s.App.Name}, pid {s.App.Pid})")));
        }
        return sessions.FirstOrDefault(s => s.ShortId.Equals(app, StringComparison.OrdinalIgnoreCase))
               ?? sessions.FirstOrDefault(s => s.App.Name.Contains(app, StringComparison.OrdinalIgnoreCase))
               ?? sessions.FirstOrDefault(s => s.App.Pid.ToString() == app)
               ?? throw new McpException($"No connected app matches '{app}'. Connected: " +
                                         string.Join(", ", sessions.Select(s => $"{s.ShortId} ({s.App.Name})")));
    }

    /// <summary>The active window, or the first window when none is active.</summary>
    public static async Task<RootInfo> DefaultRootAsync(AgentSession s)
    {
        var roots = (await s.GetRootsAsync()).Roots;
        return roots.FirstOrDefault(r => r.IsActive)
               ?? roots.FirstOrDefault(r => r.Kind != RootKind.Popup)
               ?? roots.FirstOrDefault()
               ?? throw new McpException("The app has no open window yet.");
    }

    public static bool TryParseRef(string element, char prefix, out int id)
    {
        var m = Regex.Match(element.Trim(), $"^{prefix}(\\d+)$");
        id = m.Success ? int.Parse(m.Groups[1].Value) : 0;
        return m.Success;
    }

    public static async Task<int> ElementAsync(AgentSession s, string element)
    {
        if (TryParseRef(element, 'e', out var id)) return id;
        SearchResult r;
        try { r = await s.SearchAsync(new SearchParams(element, TreeKind.Visual, 11)); }
        catch (SpyglassProtocolException ex) { throw new McpException($"Invalid element '{element}': {ex.Message}"); }

        if (r.Matches.Count == 1) return r.Matches[0].Id;
        if (r.Matches.Count > 1)
            throw new McpException($"'{element}' matches {(r.Truncated ? "more than 10" : r.Matches.Count.ToString())} elements; " +
                                   "use a reference or a more specific selector:\n" +
                                   string.Join("\n", r.Matches.Take(10).Select(m => "  " + Format.NodeLine(m.Node))));
        throw new McpException($"No element matches '{element}'." + await SuggestAsync(s, element));
    }

    /// <summary>Maps agent errors on a reference to guidance the model can act on.</summary>
    public static McpException Explain(SpyglassProtocolException ex, string element) => ex.Code switch
    {
        ErrorCodes.NodeNotFound or ErrorCodes.NodeDetached =>
            new McpException($"Element {element} no longer exists (it was removed, or its window closed). Call ui_snapshot again."),
        _ => new McpException(ex.Message),
    };

    internal static async Task<string> SuggestAsync(AgentSession s, string element)
    {
        var token = Regex.Match(element, @"(?:#|text=)?([\w-]+)\W*$").Groups[1].Value;
        if (token.Length < 2) return "";
        var roots = (await s.GetRootsAsync()).Roots;
        var all = new List<NodeDto>();
        foreach (var r in roots)
            all.AddRange(Format.Flatten(await s.GetTreeAsync(r.Node.Id, TreeKind.Logical)));
        var similar = all
            .Where(n => n.Name is { } name && (name.Contains(token, StringComparison.OrdinalIgnoreCase)
                                               || token.Contains(name, StringComparison.OrdinalIgnoreCase)
                                               || Distance(name.ToLowerInvariant(), token.ToLowerInvariant()) <= 2)
                        || n.Text is { } t && t.Contains(token, StringComparison.OrdinalIgnoreCase))
            .Take(5).ToList();
        return similar.Count == 0 ? " Call ui_snapshot to see the element tree."
            : " Similar: " + string.Join(", ", similar.Select(n => $"{Format.Compound(n)} [e{n.Id}]"));
    }

    static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
