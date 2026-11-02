using System.ComponentModel;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Spyglass.App.Core;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Mcp;

/// <summary>Resource dictionary lookup (design/mcp/01-tools.md #18).</summary>
[McpServerToolType]
public sealed class SpyglassResourceTools(SpyglassHub hub)
{
    const string AppParam = "Connected app (id like 'a1', name, or pid). Optional when only one app is connected.";

    [McpServerTool(Name = "resources_find", Title = "Find a resource", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Finds a resource key (exact, otherwise a substring) in the app's resource dictionaries and answers where a {DynamicResource Key} / {StaticResource Key} resolves from: for Light and Dark theme, the value and type that wins, the defining dictionary with its XAML file:line, and the dictionaries it overrides. Pass `element` to search from that element (its own and ancestor resources win over the application's). Resources inside control templates are not covered.")]
    public async Task<string> ResourcesFind(
        [Description("Resource key, e.g. 'SystemAccentColor', or a part of it, e.g. 'Accent'.")] string query,
        [Description("Element reference ([e12]), selector or text=…; the lookup starts at this element. Default: application-wide.")] string? element = null,
        [Description("Only this theme variant: Light, Dark or Default. Default: both Light and Dark.")] string? themeVariant = null,
        [Description("Maximum number of keys when several match. Default 10.")] int limit = 10,
        [Description(AppParam)] string? app = null)
    {
        var s = ElementResolver.App(hub, app);
        if (!s.Has(Capabilities.Resources))
            throw new McpException($"{s.App.Name}'s Spyglass agent does not support resources; update Spyglass.Agent.");
        if (string.IsNullOrWhiteSpace(query)) throw new McpException("`query` is required.");
        int? id = element is null ? null : await ElementResolver.ElementAsync(s, element);
        ResourcesFindResult r;
        try { r = await s.FindResourcesAsync(query.Trim(), id, themeVariant, Math.Clamp(limit, 1, 50)); }
        catch (SpyglassProtocolException ex) { throw element is null ? new McpException(ex.Message) : ElementResolver.Explain(ex, element); }

        if (r.Keys.Count == 0)
            return $"No resource key matches \"{query}\"{(id is null ? " in the application" : $" from [e{id}] or the application")}. " +
                   "Resources defined inside control templates are not searched; try a shorter substring.";

        var sb = new StringBuilder();
        sb.Append(r.Exact ? $"Resource \"{query}\"" : $"{r.Keys.Count}{(r.Truncated ? "+" : "")} keys contain \"{query}\"");
        sb.Append(id is null ? " (application-wide" : $" (from [e{id}]").Append(", active theme: ").Append(r.ActiveVariant).Append(")\n");
        foreach (var k in r.Keys) AppendKey(sb, k, r.ActiveVariant);
        if (r.Truncated) sb.Append("\n… more keys match; narrow `query` or raise `limit`.");
        return sb.ToString().TrimEnd();
    }

    static void AppendKey(StringBuilder sb, ResourceFindKey k, string active)
    {
        sb.Append('\n').Append(k.Key).Append('\n');
        // Variants that resolve to the same definition and value are shown together ("Light, Dark").
        foreach (var group in k.Lookups.GroupBy(l => Signature(l)))
        {
            var first = group.First();
            var variants = string.Join(", ", group.Select(l => l.Variant + (l.Variant == active ? "*" : "")));
            var win = first.Chain.FirstOrDefault(c => c.Winner);
            if (win is null || !first.Found)
            {
                sb.Append($"  {variants}: not found\n");
                continue;
            }
            sb.Append($"  {variants} → {ValueText(first.Value ?? win.Value)}   ← {Where(win)}\n");
            if (first.Note is { } note) sb.Append("    note: ").Append(note).Append('\n');
            foreach (var shadowed in group.SelectMany(l => l.Chain.Where(c => c.Shadowed)).DistinctBy(c => (c.Provider.Id, c.Value?.Text)))
                sb.Append($"    overridden: {Where(shadowed)} = {ValueText(shadowed.Value)}\n");
        }
    }

    static string Signature(ResourcesLookupResult l)
    {
        var w = l.Chain.FirstOrDefault(c => c.Winner);
        return l.Found ? $"{w?.Provider.Id}|{w?.Value?.Text}|{string.Join(",", l.Chain.Where(c => c.Shadowed).Select(c => c.Provider.Id))}" : "-";
    }

    static string ValueText(ValueDto? v) => v is null ? "(none)" : $"{(v.Text.Length > 80 ? v.Text[..79] + "…" : v.Text)} ({TypeName(v)})";

    static string TypeName(ValueDto v) => v.Type is { } t ? t[(t.LastIndexOf('.') + 1)..] : v.Kind.ToString();

    static string Where(ResourceStepDto s)
    {
        var sb = new StringBuilder(s.Path);
        if (s.Provider.Kind == "element") sb.Append($" [e{s.Provider.Id}]");
        if (s.Source is { } src) sb.Append("  ").Append(FileName(src.Uri)).Append(':').Append(src.Line);
        return sb.ToString();
    }

    static string FileName(string uri)
    {
        var i = uri.LastIndexOfAny(['/', '\\']);
        return i >= 0 ? uri[(i + 1)..] : uri;
    }
}
