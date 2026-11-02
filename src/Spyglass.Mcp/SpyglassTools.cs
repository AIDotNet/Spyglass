using System.ComponentModel;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Spyglass.App.Core;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Mcp;

/// <summary>MVP read-only tools (design/mcp/01-tools.md #1–6).</summary>
[McpServerToolType]
public sealed class SpyglassTools(SpyglassHub hub)
{
    const string AppParam = "Connected app (id like 'a1', name, or pid). Optional when only one app is connected.";
    const string ElementParam = "Element: a reference like 'e42' from earlier output, a selector ('Button#Save', 'StackPanel > Button.primary', 'ListBox > ListBoxItem:nth(3)'), 'text=Save', or 'xaml=MainView.axaml:23'.";

    // ---------------------------------------------------------------- apps_list

    [McpServerTool(Name = "apps_list", Title = "List connected apps", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the running Avalonia apps connected to Spyglass, with their windows (and which is active), Avalonia version, build type and binding error count. Call this first.")]
    public async Task<string> AppsList()
    {
        var sessions = hub.Sessions;
        if (sessions.Count == 0)
            return $"No app connected. Add `.UseSpyglass()` to the app's AppBuilder (Debug builds) and start it; Spyglass listens on 127.0.0.1:{hub.Port}.";

        var sb = new StringBuilder($"{sessions.Count} app{(sessions.Count == 1 ? "" : "s")} connected.\n");
        foreach (var s in sessions)
        {
            var a = s.App;
            sb.Append($"\n[{s.ShortId}] {a.Name}  pid {a.Pid}  {a.Os} {a.Arch}  Avalonia {a.AvaloniaVersion}  {(a.IsDebugBuild ? "Debug" : "Release")} build\n");
            sb.Append($"     agent: {s.Agent.Name} {s.Agent.Version} ({s.Agent.Flavor.ToString().ToLowerInvariant()}) — capabilities: {string.Join(", ", s.Capabilities)}\n");
            try
            {
                var roots = (await s.GetRootsAsync()).Roots;
                sb.Append("     windows: ").Append(roots.Count == 0 ? "(none)" : string.Join("; ", roots.Select(RootLabel))).Append('\n');
                if (s.Has(Capabilities.BindingErrors))
                    sb.Append($"     binding errors: {(await s.GetBindingErrorsAsync()).Count}\n");
            }
            catch (Exception ex) { sb.Append($"     (unavailable: {ex.Message})\n"); }
        }
        return sb.ToString().TrimEnd();
    }

    static string RootLabel(RootInfo r)
    {
        var b = r.Node.Bounds;
        var size = b is null ? "" : $" {Format.Num(b.W)}×{Format.Num(b.H)}";
        var kind = r.Kind switch { RootKind.Popup => "popup ", RootKind.MainView => "view ", _ => "" };
        var owner = r.OwnerId is { } o ? $" owned by [e{o}]" : "";
        return $"[e{r.Node.Id}] {kind}{r.Node.Type}{(string.IsNullOrEmpty(r.Title) ? "" : $" \"{r.Title}\"")}{size}{(r.IsActive ? " (active)" : "")}{owner}";
    }

    // ---------------------------------------------------------------- ui_snapshot

    [McpServerTool(Name = "ui_snapshot", Title = "Snapshot the UI tree", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns the UI of a window (default: the active window) or of one element as an indented text outline. Each line: Type#Name.class \"text\" [ref] size @position state. Use the [eN] references with other tools.")]
    public async Task<string> UiSnapshot(
        [Description(ElementParam + " Default: the active window.")] string? element = null,
        [Description("Maximum depth below the root. Default 8.")] int depth = 8,
        [Description("Maximum number of lines. Default 300.")] int maxNodes = 300,
        [Description("'logical' (default; close to the XAML) or 'visual' (everything that renders).")] string tree = "logical",
        [Description("Include the insides of control templates. Default false.")] bool includeTemplate = false,
        [Description("Include invisible elements. Default false (they are counted, not listed).")] bool includeHidden = false,
        [Description(AppParam)] string? app = null)
    {
        var s = ElementResolver.App(hub, app);
        var kind = tree.Equals("visual", StringComparison.OrdinalIgnoreCase) ? TreeKind.Visual : TreeKind.Logical;
        depth = Math.Clamp(depth, 1, 64);
        maxNodes = Math.Clamp(maxNodes, 10, 5000);

        var roots = (await s.GetRootsAsync()).Roots;
        int rootId;
        if (element is null) rootId = (await ElementResolver.DefaultRootAsync(s)).Node.Id;
        else rootId = await ElementResolver.ElementAsync(s, element);

        NodeDto root;
        try
        {
            // Visual trees are fetched whole so template levels can be skipped without eating the depth budget.
            root = await s.GetTreeAsync(rootId, kind, kind == TreeKind.Visual && !includeTemplate ? -1 : depth);
        }
        catch (SpyglassProtocolException ex) { throw ElementResolver.Explain(ex, element ?? $"e{rootId}"); }

        var errors = s.Has(Capabilities.BindingErrors) ? await s.GetBindingErrorsAsync() : [];
        var errorsByTarget = errors.Where(e => e.TargetId is not null).GroupBy(e => e.TargetId!.Value)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(e => e.Property).Distinct()));

        var sb = new StringBuilder();
        var lines = 0;
        var hidden = 0;
        var truncated = 0;

        IEnumerable<NodeDto> Kids(NodeDto n) =>
            (n.Children ?? []).SelectMany(c => kind == TreeKind.Visual && !includeTemplate && c.IsTemplatePart ? Kids(c) : [c]);

        void Walk(NodeDto n, int level)
        {
            if (!includeHidden && !n.IsVisible && level > 0) { hidden += 1 + Format.Flatten(n).Count() - 1; return; }
            if (lines >= maxNodes) { truncated++; return; }
            var mark = errorsByTarget.TryGetValue(n.Id, out var props) ? $"⚠ binding error ({props})" : null;
            sb.Append(' ', level * 2).Append(Format.NodeLine(n, extra: mark)).Append('\n');
            lines++;
            var kids = Kids(n).ToList();
            if (level >= depth || (n.Children is null && n.ChildCount > 0))
            {
                var count = n.Children is null ? n.ChildCount : kids.Count;
                if (count > 0) sb.Append(' ', (level + 1) * 2).Append($"… {count} child{(count == 1 ? "" : "ren")} (ui_snapshot element=e{n.Id})\n");
                return;
            }
            foreach (var k in kids) Walk(k, level + 1);
        }

        var rootInfo = roots.FirstOrDefault(r => r.Node.Id == rootId);
        sb.Append(rootInfo is null ? "" : $"{RootLabel(rootInfo)} — {kind.ToString().ToLowerInvariant()} tree\n");
        Walk(root, 0);
        if (truncated > 0) sb.Append($"… {truncated} more element(s) not shown (maxNodes={maxNodes}); narrow with element=\n");
        if (hidden > 0) sb.Append($"({hidden} hidden element(s) omitted — includeHidden: true)\n");
        var others = roots.Where(r => r.Node.Id != rootId).ToList();
        if (element is null && others.Count > 0)
            sb.Append("Other windows/popups: ").Append(string.Join("; ", others.Select(RootLabel))).Append(" — pass element=<ref>\n");
        return sb.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- ui_find

    [McpServerTool(Name = "ui_find", Title = "Find elements", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Finds elements in all windows by selector ('Button.primary', 'Window#Main StackPanel > TextBox', 'ListBox > ListBoxItem:nth(2)'), visible text ('text=Save', case-insensitive substring) or XAML location ('xaml=MainView.axaml:23').")]
    public async Task<string> UiFind(
        [Description("Selector, text=…, or xaml=File.axaml:line.")] string query,
        [Description("Maximum results. Default 20.")] int limit = 20,
        [Description("Also search inside control templates. Default false.")] bool includeTemplate = false,
        [Description(AppParam)] string? app = null)
    {
        var s = ElementResolver.App(hub, app);
        SearchResult r;
        try { r = await s.SearchAsync(new SearchParams(query, TreeKind.Visual, Math.Clamp(limit, 1, 500), includeTemplate)); }
        catch (SpyglassProtocolException ex) { throw new McpException($"Invalid query '{query}': {ex.Message}"); }
        if (r.Matches.Count == 0) return $"No elements match \"{query}\".";

        var roots = (await s.GetRootsAsync()).Roots.ToDictionary(x => x.Node.Id);
        var sb = new StringBuilder($"{r.Matches.Count}{(r.Truncated ? "+" : "")} match{(r.Matches.Count == 1 ? "" : "es")} for \"{query}\":\n");
        foreach (var m in r.Matches)
        {
            var where = roots.TryGetValue(m.Path[0], out var root) ? $"   in [e{root.Node.Id}]{(string.IsNullOrEmpty(root.Title) ? "" : $" \"{root.Title}\"")}" : "";
            sb.Append(Format.NodeLine(m.Node)).Append(where).Append('\n');
        }
        if (r.Truncated) sb.Append($"(more results; raise limit)\n");
        return sb.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- element_inspect

    [McpServerTool(Name = "element_inspect", Title = "Inspect an element", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One-call diagnosis of an element: type, XAML file:line, selector path, state, bounds and box model, DataContext type, every property set to a non-default value (with where it came from), and its binding errors. Use this first when something looks wrong.")]
    public async Task<string> ElementInspect(
        [Description(ElementParam)] string element,
        [Description(AppParam)] string? app = null)
    {
        var s = ElementResolver.App(hub, app);
        var id = await ElementResolver.ElementAsync(s, element);
        try
        {
            var d = await s.DescribeAsync(id);
            var layout = await s.GetLayoutAsync(id);
            var props = (await s.GetPropertiesAsync(id)).Where(p => p.IsSet).ToList();
            var errors = s.Has(Capabilities.BindingErrors) ? await s.GetBindingErrorsAsync(id) : [];
            var n = d.Node;

            var sb = new StringBuilder();
            sb.Append(Format.NodeLine(n, withGeometry: false)).Append('\n');
            sb.Append($"type        {n.TypeFull}\n");
            sb.Append($"xaml        {Format.Location(d.Source)}\n");
            sb.Append($"path        {d.Selector}\n");
            var pseudo = n.PseudoClasses is { Count: > 0 } pc ? string.Join(" ", pc) : "(none)";
            sb.Append($"state       {(n.IsVisible ? "visible" : "HIDDEN")}, {(n.IsEnabled ? "enabled" : "DISABLED")}, {(n.IsFocused ? "focused" : "not focused")}; pseudo-classes: {pseudo}\n");
            var b = layout.Bounds;
            sb.Append($"bounds      {Format.Num(b.W)}×{Format.Num(b.H)} @ ({Format.Num(b.X)},{Format.Num(b.Y)}) in window; desired {Format.Num(layout.DesiredSize.W)}×{Format.Num(layout.DesiredSize.H)}\n");
            sb.Append($"box         margin {Format.Thickness(layout.Margin)} · border {Format.Thickness(layout.BorderThickness)} · padding {Format.Thickness(layout.Padding)} · align {layout.HorizontalAlignment}/{layout.VerticalAlignment}");
            if (layout.ActiveConstraints.Count > 0) sb.Append($" · constrained by {string.Join(", ", layout.ActiveConstraints)}");
            sb.Append('\n');
            sb.Append($"datacontext {d.DataContextType ?? "(null)"}\n");

            sb.Append($"properties set ({props.Count}):\n");
            foreach (var p in props.OrderBy(p => p.Priority).ThenBy(p => p.Name))
                sb.Append($"  {p.Name,-22} {Clip(p.Value.Text),-32} {p.Priority}{(p.HasBinding ? " (bound)" : "")}\n");

            if (errors.Count == 0) sb.Append("binding errors: none\n");
            else
            {
                sb.Append($"binding errors ({errors.Count}):\n");
                foreach (var e in errors)
                    sb.Append($"  {e.Property}: path \"{e.Path}\" ×{e.Count} — {e.Message}\n    datacontext {e.DataContextType ?? "(null)"} · xaml {Format.Location(e.Location)}\n");
            }
            return sb.ToString().TrimEnd();
        }
        catch (SpyglassProtocolException ex) { throw ElementResolver.Explain(ex, element); }
    }

    // ---------------------------------------------------------------- element_properties

    [McpServerTool(Name = "element_properties", Title = "List element properties", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists an element's Avalonia properties with value, priority (LocalValue, StyleTrigger, Style, Template, Inherited, Unset…) and whether bound. By default only non-default values. Complex values show a reference like 'o12' that can be passed as element to drill in.")]
    public async Task<string> ElementProperties(
        [Description(ElementParam + " Also accepts an object reference like 'o12'.")] string element,
        [Description("Only these properties; '*' wildcards allowed, e.g. [\"Background\", \"*Width*\"].")] string[]? names = null,
        [Description("Include properties at their default value. Default false.")] bool includeDefaults = false,
        [Description(AppParam)] string? app = null)
    {
        var s = ElementResolver.App(hub, app);
        IReadOnlyList<PropertyDto> props;
        try
        {
            props = ElementResolver.TryParseRef(element, 'o', out var oid)
                ? await s.GetObjectPropertiesAsync(oid)
                : await s.GetPropertiesAsync(await ElementResolver.ElementAsync(s, element));
        }
        catch (SpyglassProtocolException ex) { throw ElementResolver.Explain(ex, element); }

        var filtered = props.Where(p => (includeDefaults || p.IsSet || names is { Length: > 0 }) && Matches(p.Name, names)).ToList();
        if (filtered.Count == 0) return names is { Length: > 0 } ? $"No property matches {string.Join(", ", names)}." : "No properties set (pass includeDefaults: true to list all).";
        var sb = new StringBuilder($"{filtered.Count} propert{(filtered.Count == 1 ? "y" : "ies")} of {element}:\n");
        foreach (var p in filtered)
        {
            var refNote = p.Value.Kind is ValueKind.Object or ValueKind.Collection && p.Value.Data is { } data
                ? $" (o{data.GetProperty("ref").GetInt32()})" : "";
            sb.Append($"  {p.Owner}.{p.Name,-24} {Clip(p.Value.Text) + refNote,-40} {p.Priority}{(p.HasBinding ? " (bound)" : "")}{(p.IsReadOnly ? " (read-only)" : "")}\n");
        }
        return sb.ToString().TrimEnd();
    }

    static bool Matches(string name, string[]? patterns) =>
        patterns is not { Length: > 0 } || patterns.Any(p =>
            System.Text.RegularExpressions.Regex.IsMatch(name, "^" + System.Text.RegularExpressions.Regex.Escape(p).Replace("\\*", ".*") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    static string Clip(string s) => s.Length <= 60 ? s.Replace("\n", "⏎") : s[..59].Replace("\n", "⏎") + "…";

    // ---------------------------------------------------------------- screenshot

    [McpServerTool(Name = "screenshot", Title = "Screenshot", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Captures a PNG of a window (default: the active window) or a single element. Optionally outlines another element in the capture.")]
    public async Task<CallToolResult> Screenshot(
        [Description(ElementParam + " Default: the active window.")] string? element = null,
        [Description("Element to outline in the capture (usually used with a window screenshot).")] string? highlight = null,
        [Description("Longest side in pixels. Default 1024, max 2048.")] int maxSize = 1024,
        [Description(AppParam)] string? app = null)
    {
        var s = ElementResolver.App(hub, app);
        var id = element is null ? (await ElementResolver.DefaultRootAsync(s)).Node.Id : await ElementResolver.ElementAsync(s, element);
        int? highlightId = highlight is null ? null : await ElementResolver.ElementAsync(s, highlight);
        try
        {
            var d = await s.DescribeAsync(id);
            var b = d.Node.Bounds ?? new RectData(0, 0, 1, 1);
            var longest = Math.Max(b.W, b.H);
            var scale = longest <= 0 ? 1 : Math.Min(2, Math.Clamp(maxSize, 64, 2048) / longest);
            if (highlightId is { } h)
            {
                await s.HighlightAsync(h, HighlightStyle.Box, 0);
                await Task.Delay(50);   // let the adorner be laid out before capturing
            }
            ScreenshotResult shot;
            try { shot = await s.ScreenshotAsync(id, scale); }
            finally { if (highlightId is not null) await s.HideHighlightAsync(); }

            return new CallToolResult
            {
                Content =
                [
                    ImageContentBlock.FromBytes(Convert.FromBase64String(shot.Base64), "image/png"),
                    new TextContentBlock { Text = $"{Format.NodeLine(d.Node, withGeometry: false)} — {Format.Num(b.W)}×{Format.Num(b.H)} at scale {scale:0.##} ({shot.Width}×{shot.Height} px)" +
                                                  (highlightId is { } hl ? $"; outlined [e{hl}]" : "") },
                ],
            };
        }
        catch (SpyglassProtocolException ex) { throw ElementResolver.Explain(ex, element ?? $"e{id}"); }
    }

    // ---------------------------------------------------------------- property_explain (v0.2)

    [McpServerTool(Name = "property_explain", Title = "Explain a property value", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Explains why a property has its current value: which style, theme, local value or binding wins, which ones it overrides, which conditional styles are inactive, and the XAML file:line of each. Use it for questions like 'why is this button red?'.")]
    public async Task<string> PropertyExplain(
        [Description(ElementParam)] string element,
        [Description("Property name, e.g. 'Background'.")] string property,
        [Description(AppParam)] string? app = null)
    {
        var s = ElementResolver.App(hub, app);
        var id = await ElementResolver.ElementAsync(s, element);
        try
        {
            var prop = (await s.GetPropertiesAsync(id)).FirstOrDefault(p => p.Name.Equals(property, StringComparison.OrdinalIgnoreCase))
                       ?? throw new McpException($"[e{id}] has no property '{property}'. Use element_properties to list them.");
            var d = await s.DescribeAsync(id);
            var sb = new StringBuilder($"{Format.NodeLine(d.Node, withGeometry: false)} {prop.Name} = {prop.Value.Text}\n");
            sb.Append($"priority: {prop.Priority}{(prop.HasBinding ? " (bound)" : "")}\n");

            if (prop.HasBinding && s.Has(Capabilities.BindingDetails))
            {
                var b = await s.GetBindingDetailsAsync(id, prop.Name);
                sb.Append($"binding: {{Binding {b.Description}}}{(b.ErrorType is { } et ? $" — {et}" : "")}\n");
            }
            if (prop.Priority == "Inherited")
            {
                var parent = (await s.GetAncestorsAsync(id)).SkipLast(1).LastOrDefault();
                sb.Append($"inherited from an ancestor{(parent is null ? "" : $"; run property_explain on its parent [e{parent.Id}]")}\n");
            }

            if (!s.Has(Capabilities.ValueFrames))
            {
                sb.Append("(The app uses the basic agent: switch to `UseSpyglassAdvanced()` to see which style sets the value and what it overrides.)");
                return sb.ToString();
            }

            var frames = (await s.GetValueFramesAsync(id)).Frames
                .Select(f => (f, setter: f.Setters.FirstOrDefault(x => x.Property.Equals(prop.Name, StringComparison.OrdinalIgnoreCase))))
                .Where(x => x.setter is not null).ToList();
            var winner = frames.FirstOrDefault(x => x.f.IsActive && !x.setter!.IsOverridden);
            if (winner.f is not null)
                sb.Append($"winner: {FrameLabel(winner.f)} — {winner.setter!.Value.Text}{Where(winner.f)}\n");
            else if (prop.Priority is "LocalValue" or "Animation")
                sb.Append($"winner: {prop.Priority} — set in code or XAML on the element itself\n");
            var overridden = frames.Where(x => x.f.IsActive && x.setter!.IsOverridden).ToList();
            if (overridden.Count > 0)
            {
                sb.Append("overridden:\n");
                foreach (var (f, setter) in overridden) sb.Append($"  - {FrameLabel(f)} — {setter!.Value.Text}{Where(f)}\n");
            }
            var inactive = frames.Where(x => !x.f.IsActive).ToList();
            if (inactive.Count > 0)
            {
                sb.Append("inactive (condition not met):\n");
                foreach (var (f, setter) in inactive) sb.Append($"  - {FrameLabel(f)} — {setter!.Value.Text}{Where(f)}\n");
            }
            if (frames.Count == 0) sb.Append("No style or theme sets this property.\n");
            return sb.ToString().TrimEnd();
        }
        catch (SpyglassProtocolException ex) { throw ElementResolver.Explain(ex, element); }
    }

    static string FrameLabel(ValueFrameDto f) => f.Source.Kind switch
    {
        FrameSourceKind.Style => $"Style \"{f.Source.Selector}\"",
        FrameSourceKind.ControlTheme => $"ControlTheme {f.Source.TargetType} (theme)",
        FrameSourceKind.Local => "Local value",
        FrameSourceKind.Template => $"Template of {f.Source.TargetType}",
        _ => f.Type,
    };

    static string Where(ValueFrameDto f) => f.Source.Location is { } l ? $"   ({Path.GetFileName(l.Uri)}:{l.Line})" : "";

    // ---------------------------------------------------------------- binding_errors (v0.2)

    [McpServerTool(Name = "binding_errors", Title = "Binding errors", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists runtime binding errors (aggregated: element + property + path, with count), each with the XAML file:line, DataContext type and a fix hint. Pass the returned cursor as `since` after changing code to see only new errors.")]
    public async Task<string> BindingErrors(
        [Description("Cursor from a previous call; only errors added or repeated after it are returned.")] long since = 0,
        [Description("Only errors on this element. " + ElementParam)] string? element = null,
        [Description("Maximum results. Default 50.")] int limit = 50,
        [Description(AppParam)] string? app = null)
    {
        var s = ElementResolver.App(hub, app);
        int? target = element is null ? null : await ElementResolver.ElementAsync(s, element);
        var errors = await s.GetBindingErrorsAsync(target, since);
        var cursor = errors.Count == 0 ? since : errors.Max(e => e.Seq);
        if (errors.Count == 0)
            return since > 0 ? $"No new binding errors since cursor {since}." : "No binding errors.";

        var sb = new StringBuilder($"{errors.Count} binding error{(errors.Count == 1 ? "" : "s")} (cursor: {cursor}).\n");
        foreach (var e in errors.OrderByDescending(e => e.LastTime).Take(Math.Clamp(limit, 1, 500)))
        {
            sb.Append($"[e{e.TargetId?.ToString() ?? "?"}] {e.TargetType}.{e.Property}  path \"{e.Path}\"  ×{e.Count}  last {e.LastTime:HH:mm:ss}\n");
            sb.Append($"     {e.Message}\n");
            sb.Append($"     xaml: {Format.Location(e.Location)}   datacontext: {e.DataContextType ?? "(null)"}\n");
            if (e.DataContextType is null)
                sb.Append("     hint: DataContext is null at this element — check where it is set, or the binding's Source/ElementName.\n");
        }
        return sb.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- layout_explain (v0.2)

    [McpServerTool(Name = "layout_explain", Title = "Explain layout and visibility", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Explains an element's layout: box model, how its size came about (what the parent offered, what it asked for, alignment and min/max limits) and — if it cannot be seen — every reason why: IsVisible=false on it or an ancestor, Opacity 0, zero size, clipped by an ancestor, scrolled out of view, or not attached to a window. Use for 'the button is missing / in the wrong place / too small'.")]
    public async Task<string> LayoutExplain(
        [Description(ElementParam)] string element,
        [Description(AppParam)] string? app = null)
    {
        var s = ElementResolver.App(hub, app);
        var id = await ElementResolver.ElementAsync(s, element);
        try
        {
            var d = await s.DescribeAsync(id);
            var r = await s.ExplainLayoutAsync(id);
            var l = r.Layout;
            var sb = new StringBuilder();
            sb.Append(Format.NodeLine(d.Node, withGeometry: false)).Append('\n');
            sb.Append($"bounds      {Format.Num(l.Bounds.W)}×{Format.Num(l.Bounds.H)} @ ({Format.Num(l.Bounds.X)},{Format.Num(l.Bounds.Y)}) in window; desired {Format.Num(l.DesiredSize.W)}×{Format.Num(l.DesiredSize.H)}\n");
            sb.Append($"box         margin {Format.Thickness(l.Margin)} · border {Format.Thickness(l.BorderThickness)} · padding {Format.Thickness(l.Padding)}\n");
            sb.Append($"limits      width {Opt(l.Width)} · min {Opt(l.MinWidth)} · max {Opt(l.MaxWidth)}  |  height {Opt(l.Height)} · min {Opt(l.MinHeight)} · max {Opt(l.MaxHeight)}  |  align {l.HorizontalAlignment}/{l.VerticalAlignment}\n");
            if (r.ParentType is not null)
            {
                var offered = r.MeasureConstraint is { } c ? $"{(c.W is { } w ? Format.Num(w) : "∞")}×{(c.H is { } h ? Format.Num(h) : "∞")}" : "?";
                var slot = r.ArrangeSlot is { } a ? $"{Format.Num(a.W)}×{Format.Num(a.H)}" : "?";
                sb.Append($"parent      {r.ParentType} [e{r.ParentId}] offered {offered} → desired {Format.Num(l.DesiredSize.W)}×{Format.Num(l.DesiredSize.H)} → slot {slot} → final {Format.Num(l.Bounds.W)}×{Format.Num(l.Bounds.H)}\n");
            }
            if (r.Notes.Count > 0) { sb.Append("size\n"); foreach (var n in r.Notes) sb.Append("  · ").Append(n).Append('\n'); }

            if (r.VisibilityIssues.Count == 0) sb.Append("visibility  visible: nothing hides it.");
            else
            {
                sb.Append($"visibility  NOT visible — {r.VisibilityIssues.Count} reason{(r.VisibilityIssues.Count == 1 ? "" : "s")}:\n");
                foreach (var v in r.VisibilityIssues)
                    sb.Append($"  ✗ {v.Message}{(v.NodeId is { } nid && nid != id ? $"  [e{nid}]" : "")}\n");
            }
            return sb.ToString().TrimEnd();
        }
        catch (SpyglassProtocolException ex) { throw ElementResolver.Explain(ex, element); }
    }

    static string Opt(double? v) => v is { } x ? Format.Num(x) : "–";
}
