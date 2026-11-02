using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml.Diagnostics;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Agent.Internal;

/// <summary>
/// resources/* (design/protocol/01-methods.md §13): the resource-provider tree of the Application and of elements,
/// the entries of a provider, "where does this key resolve from here" lookups with the provider chain, and live edits of
/// existing values. Public Avalonia APIs only. UI-thread only.
/// Out of scope: resources that live inside control templates / data templates (not reachable without instantiating them).
/// </summary>
internal sealed class ResourceInspector(NodeRegistry registry, ValueCodec codec)
{
    const int MaxChain = 300;
    const int MaxDepth = 16;

    // Original values of entries changed through Spyglass, for resources/reset.
    sealed record Original(object? Value, SolidColorBrush? Brush, Color BrushColor);
    readonly ConditionalWeakTable<IResourceDictionary, Dictionary<string, Original>> _modified = new();
    readonly List<WeakReference<IResourceDictionary>> _modifiedDicts = new();
    // Theme variant of theme-dictionary providers (the object itself does not know it).
    readonly ConditionalWeakTable<object, string> _variants = new();

    public void Register(AgentConnection c)
    {
        c.HandleOnUi<ResourcesGetProvidersParams, ResourcesGetProvidersResult>(Methods.ResourcesGetProviders, GetProviders);
        c.HandleOnUi<ResourcesGetEntriesParams, ResourcesGetEntriesResult>(Methods.ResourcesGetEntries, GetEntries);
        c.HandleOnUi<ResourcesLookupParams, ResourcesLookupResult>(Methods.ResourcesLookup, Lookup);
        c.HandleOnUi<ResourcesFindParams, ResourcesFindResult>(Methods.ResourcesFind, Find);
        c.HandleOnUi<ResourcesSetParams, ResourcesSetResult>(Methods.ResourcesSet, Set);
        c.HandleOnUi<ResourcesResetParams, Empty>(Methods.ResourcesReset, p => { Reset(p); return Empty.Instance; });
    }

    // ---------- provider model ----------

    /// <summary>A provider in the tree: <see cref="Obj"/> owns <see cref="Dict"/> (its own keys).</summary>
    sealed record Node(object Obj, string Kind, string? Variant = null, Uri? Include = null)
    {
        public IResourceDictionary? Dict => OwnDict(Obj);
    }

    static IResourceDictionary? OwnDict(object o) => o switch
    {
        Application a => a.Resources,
        StyledElement se => se.Resources,
        Styles s => s.Resources,
        Style s => s.Resources,
        ResourceInclude ri => Safe(() => ri.Loaded),
        IResourceDictionary d => d,
        _ => null,
    };

    static T? Safe<T>(Func<T> f) where T : class
    {
        try { return f(); } catch { return null; }
    }

    static IEnumerable<IStyle> StyleItems(object o) => o switch
    {
        Application a => a.Styles,
        Styles s => s,
        StyledElement se => ((IStyleHost)se).Styles,
        _ => [],
    };

    IEnumerable<Node> Children(Node n)
    {
        var d = n.Dict;
        if (d is not null)
        {
            foreach (var m in d.MergedDictionaries)
                if (Unwrap(m, "dictionary") is { } child) yield return child;
            foreach (var kv in d.ThemeDictionaries)
            {
                _variants.AddOrUpdate(kv.Value, VKey(kv.Key));
                yield return new Node(kv.Value, "themeDictionary", VKey(kv.Key));
            }
        }
        foreach (var s in StyleItems(n.Obj))
            if (Unwrap(s, s is Style ? "style" : "styles") is { } child) yield return child;
    }

    static Node? Unwrap(object o, string kind) => o switch
    {
        StyleInclude si => Safe(() => si.Loaded) is { } l ? new Node(l, l is Style ? "style" : kind, null, si.Source) : null,
        ResourceInclude ri => new Node(ri, kind, null, ri.Source),
        _ => new Node(o, kind),
    };

    int SubtreeKeys(Node n, int depth = 0)
    {
        if (depth > MaxDepth) return 0;
        var total = n.Dict?.Count ?? 0;
        foreach (var c in Children(n)) total += SubtreeKeys(c, depth + 1);
        return total;
    }

    ResourceProviderDto Describe(Node n, bool withChildren, int depth = 0)
    {
        List<ResourceProviderDto>? kids = null;
        if (withChildren && depth < MaxDepth)
        {
            kids = new();
            foreach (var c in Children(n))
                if (SubtreeKeys(c) > 0) kids.Add(Describe(c, true, depth + 1));
        }
        var variant = n.Variant ?? (_variants.TryGetValue(n.Obj, out var v) ? v : null);
        return new ResourceProviderDto
        {
            Id = registry.IdOf(n.Obj),
            Kind = n.Kind,
            Name = NameOf(n),
            Source = SourceOf(n),
            KeyCount = n.Dict?.Count ?? 0,
            Variant = variant,
            Children = kids,
        };
    }

    static string NameOf(Node n)
    {
        if (n.Include is { } u) return FileName(u.ToString());
        return n.Obj switch
        {
            Application a => a.GetType().Name,
            StyledElement se => se.GetType().Name + (string.IsNullOrEmpty(se.Name) ? "" : "#" + se.Name),
            Style s => "Style " + (s.Selector?.ToString() ?? ""),
            Styles s => s.GetType().Name,
            _ when n.Kind == "themeDictionary" => "ThemeDictionaries[" + n.Variant + "]",
            ResourceDictionary d => XamlSourceInfo.GetXamlSourceInfo(d) is { SourceUri: { } su } ? FileName(su.ToString()) : "ResourceDictionary",
            var o => o.GetType().Name,
        };
    }

    /// <summary>The variant, the variants it inherits from, then Default.</summary>
    static IEnumerable<ThemeVariant> VariantOrder(ThemeVariant variant)
    {
        var seen = new HashSet<ThemeVariant>();
        for (ThemeVariant? v = variant; v is not null; v = v.InheritVariant)
            if (seen.Add(v)) yield return v;
        if (seen.Add(ThemeVariant.Default)) yield return ThemeVariant.Default;
    }

    static string VKey(ThemeVariant v) => v.Key.ToString() ?? "";

    static string FileName(string uri)
    {
        var i = uri.LastIndexOfAny(['/', '\\']);
        return i >= 0 ? uri[(i + 1)..] : uri;
    }

    static SourceInfo? SourceOf(Node n)
    {
        if (n.Include is { } u) return new SourceInfo(u.ToString(), 1, 1);
        return XamlSourceInfo.GetXamlSourceInfo(n.Obj) is { SourceUri: { } uri } si
            ? new SourceInfo(uri.ToString(), XamlLines.ToOneBased(si.LineNumber, n.Obj), si.LinePosition)
            : null;
    }

    Node NodeOf(int id)
    {
        var o = registry.Get<object>(id);
        return o switch
        {
            Application => new Node(o, "application"),
            StyledElement => new Node(o, "element"),
            Style => new Node(o, "style"),
            Styles => new Node(o, "styles"),
            _ => new Node(o, _variants.TryGetValue(o, out var v) ? "themeDictionary" : "dictionary", v),
        };
    }

    static ThemeVariant? ParseVariant(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim().ToLowerInvariant() switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        "default" => ThemeVariant.Default,
        _ => new ThemeVariant(text.Trim(), ThemeVariant.Default),
    };

    static IEnumerable<StyledElement> StylingChain(StyledElement start)
    {
        for (object? cur = start; cur is StyledElement se; cur = (cur as IStyleHost)?.StylingParent)
            yield return se;
    }

    static Application App => Application.Current ?? throw new SpyglassProtocolException(ErrorCodes.InvalidParams, "The application is not available.");

    // ---------- resources/getProviders ----------

    ResourcesGetProvidersResult GetProviders(ResourcesGetProvidersParams p)
    {
        var list = new List<ResourceProviderDto> { Describe(new Node(App, "application"), true) };
        ThemeVariant active = App.ActualThemeVariant;
        if (p.ElementId is { } id)
        {
            var el = registry.Get<StyledElement>(id);
            active = el.ActualThemeVariant;
            foreach (var se in StylingChain(el))
            {
                var n = new Node(se, "element");
                if (SubtreeKeys(n) > 0) list.Add(Describe(n, true));
            }
        }
        return new ResourcesGetProvidersResult(list, VKey(active), ["Default", "Light", "Dark"]);
    }

    // ---------- resources/getEntries ----------

    ResourcesGetEntriesResult GetEntries(ResourcesGetEntriesParams p)
    {
        var n = NodeOf(p.ProviderId);
        var d = n.Dict ?? throw new SpyglassProtocolException(ErrorCodes.InvalidParams, $"{n.Obj.GetType().Name} has no resources.");
        var entries = new List<ResourceEntryDto>();
        var truncated = false;
        Collect(d, null);
        if (ParseVariant(p.ThemeVariant) is { } tv)
            foreach (var kv in d.ThemeDictionaries)
                if (kv.Key == tv && kv.Value is IResourceDictionary td) Collect(td, VKey(kv.Key));
        return new ResourcesGetEntriesResult(Describe(n, false), entries, truncated);

        void Collect(IResourceDictionary dict, string? variant)
        {
            foreach (var key in dict.Keys.ToList())
            {
                if (entries.Count >= p.Limit) { truncated = true; return; }
                var e = Entry(dict, key, variant);
                if (p.Query is { Length: > 0 } q
                    && !e.Key.Contains(q, StringComparison.OrdinalIgnoreCase)
                    && !e.Value.Text.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                entries.Add(e);
            }
        }
    }

    static string KeyText(object key) => key is Type t ? t.Name : key.ToString() ?? "";

    ResourceEntryDto Entry(IResourceDictionary dict, object key, string? variant)
    {
        object? value = null;
        try { dict.TryGetResource(key, null, out value); } catch { /* a faulty deferred resource: shown as null */ }
        string? reference = null;
        switch (value)
        {
            case DynamicResourceExtension dr:
                reference = $"{{DynamicResource {dr.ResourceKey}}}";
                value = dr.ResourceKey is { } k && dict.TryGetResource(k, null, out var r) ? r : App.TryGetResource(dr.ResourceKey ?? "", App.ActualThemeVariant, out var r2) ? r2 : null;
                break;
            case StaticResourceExtension sr:
                reference = $"{{StaticResource {sr.ResourceKey}}}";
                value = App.TryGetResource(sr.ResourceKey ?? "", App.ActualThemeVariant, out var r3) ? r3 : null;
                break;
        }
        var skey = key as string;
        return new ResourceEntryDto
        {
            Key = KeyText(key),
            KeyType = key is Type ? "type" : key is string ? "string" : key.GetType().Name,
            Value = DescribeValue(value),
            Reference = reference,
            Source = EntrySource(dict, key),
            Variant = variant ?? (_variants.TryGetValue(dict, out var vv) ? vv : null),
            Editable = skey is not null && EditTarget(value) is not null,
            IsModifiedBySpyglass = skey is not null && _modified.TryGetValue(dict, out var m) && m.ContainsKey(skey),
        };
    }

    ValueDto DescribeValue(object? value) => value switch
    {
        ITemplate or IDataTemplate => new ValueDto(ValueKind.Object, "{" + value.GetType().Name + "}", null, value.GetType().FullName),
        ControlTheme t => new ValueDto(ValueKind.Object, "{ControlTheme " + (t.TargetType?.Name ?? "") + "}", null, value.GetType().FullName),
        _ => codec.Encode(value),
    };

    static SourceInfo? EntrySource(IResourceDictionary dict, object key)
    {
        var si = XamlSourceInfo.GetXamlSourceInfo(dict, key);
        return si is { SourceUri: { } uri } ? new SourceInfo(uri.ToString(), XamlLines.ToOneBased(si.LineNumber, dict), si.LinePosition) : null;
    }

    // ---------- resources/lookup ----------

    sealed class Walk(string key, ThemeVariant variant, ResourceInspector owner)
    {
        public readonly List<ResourceStepDto> Steps = new();
        public bool Truncated;
        readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);

        public void Scan(Node n, string path, int depth = 0)
        {
            if (depth > MaxDepth || !_seen.Add(n.Obj)) return;
            var d = n.Dict;
            if (d is { Count: > 0 }) Step(n, d, path);
            if (d is not null)
            {
                // Theme dictionaries: the active variant, then the variants it inherits from, then Default (as ResourceDictionary does).
                foreach (var v in VariantOrder(variant))
                    if (d.ThemeDictionaries.TryGetValue(v, out var tp))
                    {
                        owner._variants.AddOrUpdate(tp, VKey(v));
                        Scan(new Node(tp, "themeDictionary", VKey(v)), path + " › ThemeDictionaries[" + VKey(v) + "]", depth + 1);
                    }
                for (var i = d.MergedDictionaries.Count - 1; i >= 0; i--)
                    if (Unwrap(d.MergedDictionaries[i], "dictionary") is { } m)
                        Scan(m, path + " › " + NameOf(m), depth + 1);
            }
            var styles = StyleItems(n.Obj).ToList();
            for (var i = styles.Count - 1; i >= 0; i--)
                if (Unwrap(styles[i], styles[i] is Style ? "style" : "styles") is { } c)
                    Scan(c, path + " › " + NameOf(c), depth + 1);
        }

        void Step(Node n, IResourceDictionary d, string path)
        {
            if (Steps.Count >= MaxChain) { Truncated = true; return; }
            var hit = d.ContainsKey(key);
            Steps.Add(new ResourceStepDto
            {
                Provider = owner.Describe(n, false),
                Path = path,
                Hit = hit,
                Value = hit ? owner.DescribeValue(Resolve(d)) : null,
                Source = hit ? EntrySource(d, key) : null,
            });
        }

        object? Resolve(IResourceDictionary d)
        {
            try { d.TryGetResource(key, null, out var v); return v; } catch { return null; }
        }
    }

    ResourcesLookupResult Lookup(ResourcesLookupParams p)
    {
        var element = p.ElementId is { } id ? registry.Get<StyledElement>(id) : null;
        var variant = ParseVariant(p.ThemeVariant) ?? element?.ActualThemeVariant ?? App.ActualThemeVariant;
        var walk = new Walk(p.Key, variant, this);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (element is not null)
            foreach (var se in StylingChain(element)) walk.Scan(new Node(se, "element"), NameOf(new Node(se, "element")));
        walk.Scan(new Node(App, "application"), "Application");

        var steps = walk.Steps;
        var winner = steps.FindIndex(s => s.Hit);
        for (var i = 0; i < steps.Count; i++)
            if (steps[i].Hit) steps[i] = steps[i] with { Winner = i == winner, Shadowed = i != winner };

        object? real = null;
        var found = element is not null ? element.TryFindResource(p.Key, variant, out real) : App.TryGetResource(p.Key, variant, out real);
        string? note = null;
        if (found != (winner >= 0))
            note = found ? "Avalonia resolved this key from a provider that is not listed (for example a template or a dynamically added source)." 
                         : "A provider defines the key but Avalonia did not resolve it for this theme variant.";
        return new ResourcesLookupResult
        {
            Key = p.Key,
            Variant = VKey(variant),
            Found = found,
            Value = found ? DescribeValue(real) : null,
            Chain = steps,
            Truncated = walk.Truncated,
            Note = note,
        };
    }

    // ---------- resources/find ----------

    ResourcesFindResult Find(ResourcesFindParams p)
    {
        if (string.IsNullOrWhiteSpace(p.Query)) throw new SpyglassProtocolException(ErrorCodes.InvalidParams, "query is required");
        var roots = new List<Node> { new(App, "application") };
        StyledElement? el = p.ElementId is { } id ? registry.Get<StyledElement>(id) : null;
        if (el is not null) roots.AddRange(StylingChain(el).Select(se => new Node(se, "element")));

        var all = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var r in roots) Gather(r, all, seen, 0);

        var exact = all.Contains(p.Query);
        var keys = (exact ? [p.Query] : all.Where(k => k.Contains(p.Query, StringComparison.OrdinalIgnoreCase)).OrderBy(k => k, StringComparer.Ordinal).ToList());
        var truncated = keys.Count > p.Limit;
        var variants = ParseVariant(p.ThemeVariant) is { } one ? [one] : new List<ThemeVariant> { ThemeVariant.Light, ThemeVariant.Dark };
        var result = keys.Take(p.Limit)
            .Select(k => new ResourceFindKey(k, variants.Select(v => Lookup(new ResourcesLookupParams(k, p.ElementId, VKey(v)))).ToList()))
            .ToList();
        return new ResourcesFindResult(result, exact, truncated, VKey(el?.ActualThemeVariant ?? App.ActualThemeVariant));
    }

    void Gather(Node n, HashSet<string> keys, HashSet<object> seen, int depth)
    {
        if (depth > MaxDepth || !seen.Add(n.Obj)) return;
        if (n.Dict is { } d) foreach (var k in d.Keys) if (k is string s) keys.Add(s);
        foreach (var c in Children(n)) Gather(c, keys, seen, depth + 1);
    }

    // ---------- resources/set / reset ----------

    /// <summary>The type a client value is decoded to, or null when the value cannot be edited.</summary>
    static Type? EditTarget(object? v) => v switch
    {
        null => null,
        bool => typeof(bool),
        string => typeof(string),
        double => typeof(double),
        float => typeof(float),
        int => typeof(int),
        long => typeof(long),
        Color => typeof(Color),
        IBrush => typeof(IBrush),
        Thickness => typeof(Thickness),
        CornerRadius => typeof(CornerRadius),
        Size => typeof(Size),
        Point => typeof(Point),
        Rect => typeof(Rect),
        Enum e => e.GetType(),
        _ => null,
    };

    ResourcesSetResult Set(ResourcesSetParams p)
    {
        var n = NodeOf(p.ProviderId);
        var d = n.Dict ?? throw new SpyglassProtocolException(ErrorCodes.InvalidParams, "That provider has no resources.");
        if (!d.ContainsKey(p.Key))
            throw new SpyglassProtocolException(ErrorCodes.InvalidParams,
                $"'{p.Key}' is not defined directly in {NameOf(n)}. Spyglass edits existing values only (it never adds or removes keys); use resources/lookup to find the defining provider.");
        d.TryGetResource(p.Key, null, out var current);
        var target = EditTarget(current) ?? throw new SpyglassProtocolException(ErrorCodes.ConversionFailed,
            $"'{p.Key}' holds {(current is null ? "null" : current.GetType().Name)}, which cannot be edited (supported: text, numbers, bool, enums, colours, brushes, thickness, corner radius, size, point, rect).");
        var value = codec.Decode(p.Value, target);

        var originals = _modified.GetOrCreateValue(d);
        if (!originals.ContainsKey(p.Key))
        {
            originals[p.Key] = current is SolidColorBrush scb ? new Original(current, scb, scb.Color) : new Original(current, null, default);
            _modifiedDicts.Add(new WeakReference<IResourceDictionary>(d));
        }
        try
        {
            // A mutable XAML brush is changed in place so that StaticResource users follow too; everything else is replaced
            // (DynamicResource users are notified by the dictionary).
            if (current is SolidColorBrush mutable && value is ISolidColorBrush ns) mutable.Color = ns.Color;
            else d[p.Key] = value;
        }
        catch (Exception ex)
        {
            throw new SpyglassProtocolException(ErrorCodes.SetValueFailed, $"{ex.GetType().Name}: {ex.Message}");
        }
        return new ResourcesSetResult(Entry(d, p.Key, null));
    }

    void Reset(ResourcesResetParams p)
    {
        if (p.All)
        {
            foreach (var w in _modifiedDicts.ToList())
                if (w.TryGetTarget(out var d) && _modified.TryGetValue(d, out var m))
                    foreach (var k in m.Keys.ToList()) Restore(d, k, m);
            _modifiedDicts.Clear();
            return;
        }
        var n = NodeOf(p.ProviderId ?? throw new SpyglassProtocolException(ErrorCodes.InvalidParams, "providerId is required"));
        if (n.Dict is not { } dict || !_modified.TryGetValue(dict, out var mods)) return;
        if (p.Key is null) foreach (var k in mods.Keys.ToList()) Restore(dict, k, mods);
        else Restore(dict, p.Key, mods);
    }

    static void Restore(IResourceDictionary d, string key, Dictionary<string, Original> mods)
    {
        if (!mods.Remove(key, out var o)) return;
        if (o.Brush is { } brush) { brush.Color = o.BrushColor; if (!ReferenceEquals(d.TryGetResource(key, null, out var cur) ? cur : null, brush)) d[key] = brush; }
        else d[key] = o.Value;
    }
}
