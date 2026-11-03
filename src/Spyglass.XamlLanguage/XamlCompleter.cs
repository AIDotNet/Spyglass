using System.Reflection;
using System.Text.RegularExpressions;
using Spyglass.Protocol.Messages;

namespace Spyglass.XamlLanguage;

/// <summary>One completion/hover request against a loaded <see cref="XamlTypeSystem"/>.</summary>
internal sealed class XamlCompleter(XamlTypeSystem ts, string text, int offset, Func<IEnumerable<string>> resourceKeys)
{
    const string XamlNs = XamlTypeSystem.XamlNs;

    static readonly string[] XDirectives = ["Name", "Key", "DataType", "CompileBindings", "FieldModifier", "Shared", "Class", "ClassModifier"];
    static readonly string[] XIntrinsicElements = ["String", "Double", "Int32", "Int64", "Single", "Boolean", "Char", "Byte", "Decimal", "TimeSpan", "Uri", "Object", "Null"];
    static readonly string[] XIntrinsicExtensions = ["Static", "Type", "Null", "True", "False"];
    static readonly HashSet<string> BindingExtensions = ["Binding", "CompiledBinding", "ReflectionBinding"];

    XamlContext _ctx = null!;

    public XamlCompletionResult Complete()
    {
        _ctx = XamlScanner.At(text, offset);
        var items = _ctx.Kind switch
        {
            XamlContextKind.ElementName => Elements(),
            XamlContextKind.ClosingTag => Closing(),
            XamlContextKind.AttributeName => AttributeNames(),
            XamlContextKind.AttributeValue => AttributeValues(),
            _ => null,
        };
        return items is null ? new XamlCompletionResult(offset, []) : new XamlCompletionResult(_from, Dedupe(items));
    }

    int _from;

    static List<XamlCompletionItem> Dedupe(IEnumerable<XamlCompletionItem> items) =>
        items.GroupBy(i => i.Label, StringComparer.Ordinal).Select(g => g.First()).ToList();

    string XPrefix => _ctx.Prefix(XamlNs) ?? "x";
    IReadOnlyDictionary<string, string> Ns => _ctx.Namespaces;

    // ---------- elements ----------

    List<XamlCompletionItem> Elements()
    {
        var partial = _ctx.Partial;
        _from = _ctx.PartialStart;
        var items = new List<XamlCompletionItem>();

        // <Grid.Row… : property elements of the owner.
        var dot = partial.LastIndexOf('.');
        if (dot > 0)
        {
            var ownerText = partial[..dot];
            if (ts.Resolve(ownerText, Ns) is { } owner)
                foreach (var m in ts.Members(owner).Where(m => m.Kind == XamlMemberKind.Property))
                    items.Add(new($"{ownerText}.{m.Name}", "property", Friendly(m.ValueType), DocId: m.DocId, Deprecated: m.Deprecated));
            return items;
        }

        // Types of every declared namespace.
        foreach (var (prefix, uri) in Ns)
        {
            if (uri == XamlNs)
            {
                items.AddRange(XIntrinsicElements.Select(n => new XamlCompletionItem($"{prefix}:{n}", "type", "XAML intrinsic", SortText: "2")));
                continue;
            }
            foreach (var t in ts.TypesIn(uri).Where(XamlTypeSystem.IsElement))
                items.Add(Element(t, prefix));
        }

        // Undeclared component libraries and the project's own namespaces: the item adds the xmlns declaration.
        if (_ctx.Root is not null && _ctx.XmlnsInsertOffset >= 0 && _ctx.Element is not null)
        {
            var declared = Ns.Values.ToHashSet(StringComparer.Ordinal);
            foreach (var uri in ts.XmlnsUris.Where(u => !declared.Contains(u) && u != XamlNs))
            {
                var prefix = FreePrefix(ts.SuggestedPrefix(uri));
                foreach (var t in ts.TypesIn(uri).Where(t => XamlTypeSystem.IsElement(t) && XamlTypeSystem.IsControl(t)))
                    items.Add(Element(t, prefix) with { Detail = $"{t.Namespace} (adds xmlns:{prefix})", SortText = "3" + t.Name, AdditionalEdit = Xmlns(prefix, uri) });
            }
            foreach (var clr in ts.ProjectNamespaces())
            {
                var uri = "using:" + clr;
                if (declared.Contains(uri)) continue;
                var prefix = FreePrefix(ts.SuggestedPrefix(clr));
                foreach (var t in ts.TypesIn(uri).Where(t => XamlTypeSystem.IsElement(t) && XamlTypeSystem.IsControl(t) && t.Assembly == ts.ProjectAssembly))
                    items.Add(Element(t, prefix) with { Detail = $"{clr} (adds xmlns:{prefix})", SortText = "3" + t.Name, AdditionalEdit = Xmlns(prefix, uri) });
            }
        }

        // <Parent. → its property elements.
        if (_ctx.Element is { } parent && !parent.Name.Contains('.'))
            items.Add(new($"{parent.Name}.", "owner", "property element", SortText: "0", Retrigger: true));
        return items;
    }

    XamlCompletionItem Element(Type t, string prefix) => new(
        prefix.Length == 0 ? t.Name : $"{prefix}:{t.Name}", "element", t.Namespace,
        DocId: $"T:{t.FullName}", SortText: (XamlTypeSystem.IsControl(t) ? "1" : "2") + t.Name);

    XamlTextEdit Xmlns(string prefix, string uri) => new(_ctx.XmlnsInsertOffset, 0, $" xmlns:{prefix}=\"{uri}\"");

    /// <summary><paramref name="wanted"/>, or wanted2, wanted3… if the document already uses it for another namespace.</summary>
    string FreePrefix(string wanted)
    {
        var p = wanted;
        for (var n = 2; Ns.ContainsKey(p); n++) p = wanted + n;
        return p;
    }

    List<XamlCompletionItem> Closing()
    {
        _from = _ctx.PartialStart;
        return _ctx.Element is { } open ? [new XamlCompletionItem(open.Name, "closingTag", "close tag", InsertText: open.Name + ">")] : [];
    }

    // ---------- attribute names ----------

    List<XamlCompletionItem> AttributeNames()
    {
        var element = _ctx.Element!;
        var partial = _ctx.Partial;
        _from = _ctx.PartialStart;
        var items = new List<XamlCompletionItem>();
        if (element.Name.Contains('.')) return items;   // property element: no attributes

        // Grid.| → attached properties and events of the owner.
        var colon = partial.IndexOf(':');
        var dot = partial.LastIndexOf('.');
        if (dot > colon && dot > 0)
        {
            var ownerText = partial[..dot];
            if (ts.Resolve(ownerText, Ns) is not { } owner) return items;
            foreach (var m in ts.Attached(owner))
                items.Add(new($"{ownerText}.{m.Name}", m.Kind == XamlMemberKind.Event ? "event" : "attachedProperty", Friendly(m.ValueType) ?? "event",
                    InsertText: $"{ownerText}.{m.Name}=\"$1\"", Snippet: true, DocId: m.DocId, Deprecated: m.Deprecated, Retrigger: m.Kind != XamlMemberKind.Event));
            return items;
        }

        var type = ts.Resolve(element.Name, Ns);
        var present = element.Attributes.Keys.Where(k => k != partial).ToHashSet(StringComparer.Ordinal);
        if (type is not null)
            foreach (var m in ts.Members(type))
            {
                if (present.Contains(m.Name) || !m.Settable && m.Kind == XamlMemberKind.Property) continue;
                var ev = m.Kind == XamlMemberKind.Event;
                items.Add(new(m.Name, ev ? "event" : "property", ev ? "event" : Friendly(m.ValueType), InsertText: $"{m.Name}=\"$1\"", Snippet: true,
                    DocId: m.DocId, Deprecated: m.Deprecated, Retrigger: true, SortText: (ev ? "2" : "1") + m.Name));
            }

        var xp = XPrefix;
        var isRoot = _ctx.Root == element;
        foreach (var d in XDirectives)
        {
            if (d is "Class" or "ClassModifier" && !isRoot) continue;
            if (present.Contains($"{xp}:{d}")) continue;
            items.Add(new($"{xp}:{d}", "directive", "XAML directive", InsertText: $"{xp}:{d}=\"$1\"", Snippet: true, Retrigger: d == "DataType", SortText: "3" + d));
        }
        if (isRoot) items.Add(new("xmlns:", "namespace", "namespace declaration", InsertText: "xmlns:$1=\"$2\"", Snippet: true, SortText: "4"));

        // Attached property owners reachable through declared prefixes: Grid. Canvas. DockPanel. a library's own…
        var owners = ts.AttachedOwners().ToHashSet();
        foreach (var (prefix, uri) in Ns)
            foreach (var t in ts.TypesIn(uri).Where(owners.Contains))
                items.Add(new($"{(prefix.Length == 0 ? "" : prefix + ":")}{t.Name}.", "owner", "attached properties", DocId: $"T:{t.FullName}", Retrigger: true, SortText: "5" + t.Name));
        return items;
    }

    // ---------- attribute values ----------

    List<XamlCompletionItem> AttributeValues()
    {
        var attr = _ctx.Attribute!;
        var value = _ctx.Partial;
        _from = _ctx.ValueStart;
        var element = _ctx.Element!;

        if (value.StartsWith('{') && !value.StartsWith("{}", StringComparison.Ordinal)) return MarkupExtension(value);

        if (attr == "xmlns" || attr.StartsWith("xmlns:", StringComparison.Ordinal))
        {
            var items = ts.XmlnsUris.Order(StringComparer.Ordinal).Select(u => new XamlCompletionItem(u, "namespace", "xmlns")).ToList();
            items.Add(new(XamlNs, "namespace", "XAML language"));
            items.AddRange(ts.ProjectNamespaces().Select(n => new XamlCompletionItem("using:" + n, "namespace", "project namespace")));
            return items;
        }

        var xp = XPrefix;
        if (attr == $"{xp}:DataType") return Types(projectFirst: true);
        if (attr == $"{xp}:CompileBindings" || attr == $"{xp}:Shared") return Literals(["True", "False"]);

        var elementType = ts.Resolve(element.Name, Ns);
        // <Setter Property="…" Value="…"> inside a Style/ControlTheme: properties of the styled type.
        if (elementType?.Name == "Setter" && attr is "Property" or "Value")
        {
            var target = StyleTarget(element);
            if (attr == "Property")
            {
                if (target is null) return [];
                return ts.Members(target).Where(m => m.Kind == XamlMemberKind.Property && m.Settable)
                    .Select(m => new XamlCompletionItem(m.Name, "property", Friendly(m.ValueType), DocId: m.DocId, Deprecated: m.Deprecated)).ToList();
            }
            var prop = element.Attributes.GetValueOrDefault("Property");
            var member = prop is null ? null : ts.Member(target, prop, Ns);
            return member?.ValueType is { } vt ? ValuesOf(vt) : [];
        }

        var m = ts.Member(elementType, attr, Ns);
        if (m is null) return [];
        if (m.Kind == XamlMemberKind.Event) return Handlers();
        if (XamlTypeSystem.IsTypeValued(m.ValueType)) return Types(projectFirst: false);
        return m.ValueType is null ? [] : ValuesOf(m.ValueType);
    }

    List<XamlCompletionItem> Literals(IEnumerable<string> values) => values.Select(v => new XamlCompletionItem(v, "value")).ToList();

    List<XamlCompletionItem> ValuesOf(Type t) =>
        ts.Values(t).Select(v => new XamlCompletionItem(v.Value, "value", Friendly(t), DocId: v.DocId)).ToList();

    /// <summary>Element-capable types through the document's prefixes (x:DataType, TargetType, x:Type).</summary>
    List<XamlCompletionItem> Types(bool projectFirst)
    {
        var items = new List<XamlCompletionItem>();
        foreach (var (prefix, uri) in Ns)
            foreach (var t in ts.TypesIn(uri))
            {
                if (!t.IsPublic || t.IsGenericTypeDefinition || t.IsInterface && !projectFirst) continue;
                var own = t.Assembly == ts.ProjectAssembly;
                if (!projectFirst && !XamlTypeSystem.IsElement(t)) continue;
                items.Add(new(prefix.Length == 0 ? t.Name : $"{prefix}:{t.Name}", "type", t.Namespace, DocId: $"T:{t.FullName}",
                    SortText: (projectFirst && own ? "0" : "1") + t.Name));
            }
        return items;
    }

    /// <summary>Methods of the root's x:Class that can handle an event (two parameters: sender, args).</summary>
    List<XamlCompletionItem> Handlers()
    {
        var cls = _ctx.Root?.Attributes.GetValueOrDefault($"{XPrefix}:Class");
        var type = cls is null ? null : ts.ProjectAssembly?.GetType(cls);
        if (type is null) return [];
        try
        {
            return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(mi => !mi.IsSpecialName && mi.GetParameters().Length == 2 && mi.ReturnType.FullName == "System.Void")
                .Select(mi => new XamlCompletionItem(mi.Name, "method", $"{type.Name}.{mi.Name}(sender, e)")).ToList();
        }
        catch { return []; }
    }

    /// <summary>The type a Setter in a Style (Selector="… Button.accent") or ControlTheme (TargetType) styles.</summary>
    Type? StyleTarget(XamlElement setter)
    {
        foreach (var e in setter.SelfAndAncestors().Skip(1))
        {
            var local = e.Name.Contains(':') ? e.Name[(e.Name.IndexOf(':') + 1)..] : e.Name;
            if (local is "ControlTheme" or "ControlTemplate" && e.Attributes.GetValueOrDefault("TargetType") is { } tt) return TypeRef(tt);
            if (local == "Style" && e.Attributes.GetValueOrDefault("Selector") is { } sel) return SelectorTarget(sel);
        }
        return null;
    }

    /// <summary>The last type in a selector: "Button.accent /template/ ContentPresenter#Part" → ContentPresenter.</summary>
    Type? SelectorTarget(string selector)
    {
        var last = Regex.Split(selector.Replace("/template/", " "), @"[\s>,]+").LastOrDefault(s => s.Length > 0 && s != "^");
        if (last is null) return null;
        var m = Regex.Match(last.TrimStart('^'), @"^([A-Za-z_][\w]*(?:\|[A-Za-z_]\w*)?)");
        return m.Success ? TypeRef(m.Groups[1].Value.Replace('|', ':')) : null;
    }

    /// <summary>"Button", "local:Card", "{x:Type local:Card}".</summary>
    Type? TypeRef(string value)
    {
        var v = value.Trim();
        var m = Regex.Match(v, @"^\{\s*[\w]+:Type\s+(?:TypeName\s*=\s*)?([\w:.]+)\s*\}$");
        if (m.Success) v = m.Groups[1].Value;
        return ts.Resolve(v, Ns);
    }

    // ---------- markup extensions ----------

    sealed class Frame
    {
        public int NameStart;
        public string Name = "";
        public bool NameDone;
        public int SegmentStart;
        public int Segment;
    }

    List<XamlCompletionItem> MarkupExtension(string value)
    {
        var stack = new Stack<Frame>();
        var quoted = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (quoted) { if (c == '\'') quoted = false; continue; }
            var top = stack.Count > 0 ? stack.Peek() : null;
            switch (c)
            {
                case '\\': i++; break;
                case '\'' when top is not null: quoted = true; break;
                case '{': stack.Push(new Frame { NameStart = i + 1, SegmentStart = i + 1 }); break;
                case '}': if (stack.Count > 0) stack.Pop(); break;
                case ',' when top is not null:
                    top.NameDone = true;
                    top.Segment++;
                    top.SegmentStart = i + 1;
                    break;
                default:
                    if (top is { NameDone: false })
                    {
                        if (char.IsWhiteSpace(c))
                        {
                            if (top.Name.Length > 0) { top.NameDone = true; top.SegmentStart = i + 1; }
                            else top.NameStart = i + 1;
                        }
                        else top.Name += c;
                    }
                    break;
            }
        }
        if (quoted || stack.Count == 0) return [];
        var f = stack.Peek();
        var valueStart = _ctx.ValueStart;

        if (!f.NameDone)
        {
            _from = valueStart + f.NameStart;
            return ExtensionNames();
        }

        var segment = value[f.SegmentStart..].TrimStart();
        var extType = ts.Resolve(f.Name, Ns);
        var extName = f.Name.Contains(':') ? f.Name[(f.Name.IndexOf(':') + 1)..] : f.Name;
        var eq = segment.IndexOf('=');
        if (eq >= 0)
        {
            var key = segment[..eq].Trim();
            var partial = segment[(eq + 1)..].TrimStart();
            _from = offset - partial.Length;
            switch (key)
            {
                case "Path" when BindingExtensions.Contains(extName): return BindingPath(partial);
                case "ElementName": return Names();
                case "ResourceKey": return Resources();
            }
            var prop = extType is null ? null : XamlTypeSystem.ClrProperties(extType).FirstOrDefault(p => p.Name == key);
            return prop is null ? [] : ValuesOf(prop.PropertyType);
        }

        _from = offset - segment.Length;
        var items = new List<XamlCompletionItem>();
        if (f.Segment == 0)
        {
            if (BindingExtensions.Contains(extName)) items.AddRange(BindingPath(segment));
            else if (extName is "StaticResource" or "DynamicResource") items.AddRange(Resources());
            else if (extName == "TemplateBinding") items.AddRange(TemplateProperties());
            else if (extName == "Type" && f.Name.Contains(':')) items.AddRange(Types(projectFirst: false));
        }
        if (extType is not null)
            foreach (var p in XamlTypeSystem.ClrProperties(extType))
                if (Safe(() => p.SetMethod is { IsPublic: true }))
                    items.Add(new($"{p.Name}=", "property", Friendly(p.PropertyType), DocId: $"P:{p.DeclaringType?.FullName}.{p.Name}", Retrigger: true, SortText: "9" + p.Name));
        return items;
    }

    List<XamlCompletionItem> ExtensionNames()
    {
        var items = new List<XamlCompletionItem>();
        foreach (var (prefix, uri) in Ns)
        {
            var p = prefix.Length == 0 ? "" : prefix + ":";
            if (uri == XamlNs)
            {
                items.AddRange(XIntrinsicExtensions.Select(n => new XamlCompletionItem(p + n, "markupExtension", "XAML intrinsic")));
                continue;
            }
            foreach (var t in ts.TypesIn(uri).Where(XamlTypeSystem.IsMarkupExtension))
                items.Add(new(p + XamlTypeSystem.ExtensionName(t), "markupExtension", t.Namespace, DocId: $"T:{t.FullName}",
                    SortText: (BindingExtensions.Contains(XamlTypeSystem.ExtensionName(t)) || XamlTypeSystem.ExtensionName(t).EndsWith("Resource", StringComparison.Ordinal) ? "0" : "1") + t.Name));
        }
        return items;
    }

    /// <summary>Properties along a binding path from the x:DataType in scope: "Customer.Addr" → Customer's properties.</summary>
    List<XamlCompletionItem> BindingPath(string partial)
    {
        if (partial.Length > 0 && (partial[0] is '$' or '#' or '^' || partial.Contains('[') || partial.Contains('(')))
            return [];
        var type = DataType();
        if (type is null) return [];
        var segments = partial.Split('.');
        foreach (var s in segments[..^1])
        {
            var p = XamlTypeSystem.ClrProperties(type).FirstOrDefault(x => x.Name == s.Trim());
            if (p is null) return [];
            type = Unwrap(p.PropertyType);
        }
        _from = offset - segments[^1].Length;
        return XamlTypeSystem.ClrProperties(type)
            .Select(p => new XamlCompletionItem(p.Name, "property", Friendly(p.PropertyType), DocId: $"P:{p.DeclaringType?.FullName}.{p.Name}", SortText: "0" + p.Name))
            .ToList();
    }

    Type? DataType()
    {
        var key = $"{XPrefix}:DataType";
        foreach (var e in _ctx.Element!.SelfAndAncestors())
            if (e.Attributes.GetValueOrDefault(key) is { } v) return TypeRef(v);
        return null;
    }

    List<XamlCompletionItem> TemplateProperties()
    {
        foreach (var e in _ctx.Element!.SelfAndAncestors())
        {
            var local = e.Name.Contains(':') ? e.Name[(e.Name.IndexOf(':') + 1)..] : e.Name;
            Type? target = null;
            if (local is "ControlTemplate" or "ControlTheme" && e.Attributes.GetValueOrDefault("TargetType") is { } tt) target = TypeRef(tt);
            else if (local == "Style" && e.Attributes.GetValueOrDefault("Selector") is { } sel) target = SelectorTarget(sel);
            if (target is not null)
                return ts.Members(target).Where(m => m.Kind == XamlMemberKind.Property)
                    .Select(m => new XamlCompletionItem(m.Name, "property", Friendly(m.ValueType), DocId: m.DocId, SortText: "0" + m.Name)).ToList();
        }
        return [];
    }

    List<XamlCompletionItem> Resources() =>
        XamlScanner.Keys(text).Concat(resourceKeys()).Distinct(StringComparer.Ordinal)
            .Select(k => new XamlCompletionItem(k, "resource", "resource key", SortText: "0" + k)).ToList();

    List<XamlCompletionItem> Names() =>
        XamlScanner.Names(text).Distinct(StringComparer.Ordinal).Select(n => new XamlCompletionItem(n, "name", "x:Name")).ToList();

    // ---------- hover ----------

    public XamlHoverResult? Hover()
    {
        var s = offset;
        var e = offset;
        while (s > 0 && IsWord(text[s - 1])) s--;
        while (e < text.Length && IsWord(text[e])) e++;
        if (s == e) return null;
        var word = text[s..e];
        _ctx = XamlScanner.At(text, e);
        string? md = null;
        switch (_ctx.Kind)
        {
            case XamlContextKind.ElementName or XamlContextKind.ClosingTag:
            {
                var dot = word.LastIndexOf('.');
                if (dot > 0 && ts.Resolve(word[..dot], Ns) is { } owner)
                    md = MemberMarkdown(ts.Members(owner).FirstOrDefault(m => m.Name == word[(dot + 1)..]));
                else if (ts.Resolve(word, Ns) is { } t) md = TypeMarkdown(t);
                break;
            }
            case XamlContextKind.AttributeName:
                md = MemberMarkdown(ts.Member(ts.Resolve(_ctx.Element!.Name, Ns), word, Ns));
                break;
            case XamlContextKind.AttributeValue when s > 0 && text[s - 1] == '{':
                if (ts.Resolve(word, Ns) is { } ext) md = TypeMarkdown(ext);
                break;
        }
        return md is null ? null : new XamlHoverResult(md, s, e);
    }

    static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_' or ':' or '.';

    string TypeMarkdown(Type t)
    {
        var doc = ts.Documentation($"T:{t.FullName}");
        var kind = XamlTypeSystem.IsControl(t) ? "control" : "class";
        return $"```csharp\n{kind} {t.FullName}\n```\n_{t.Assembly.GetName().Name}_" + (doc is null ? "" : $"\n\n{doc}");
    }

    string? MemberMarkdown(XamlMember? m)
    {
        if (m is null) return null;
        var doc = ts.Documentation(m.DocId);
        var sig = m.Kind switch
        {
            XamlMemberKind.Event => $"event {m.Owner.Name}.{m.Name}",
            XamlMemberKind.AttachedProperty => $"attached {Friendly(m.ValueType)} {m.Owner.Name}.{m.Name}",
            _ => $"{Friendly(m.ValueType)} {m.Owner.Name}.{m.Name}",
        };
        var values = m.ValueType is null ? [] : ts.Values(m.ValueType).Select(v => v.Value).Take(12).ToList();
        return $"```csharp\n{sig}\n```" + (doc is null ? "" : $"\n\n{doc}") + (values.Count > 0 ? $"\n\nValues: {string.Join(", ", values.Select(v => $"`{v}`"))}" : "");
    }

    // ---------- helpers ----------

    static Type Unwrap(Type t) => Safe(() => t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Nullable`1") ? t.GetGenericArguments()[0] : t;

    internal static string? Friendly(Type? t)
    {
        if (t is null) return null;
        try
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Nullable`1") return Friendly(t.GetGenericArguments()[0]) + "?";
            var name = t.FullName switch
            {
                "System.Double" => "double", "System.Int32" => "int", "System.Boolean" => "bool", "System.String" => "string",
                "System.Object" => "object", "System.Single" => "float", "System.Int64" => "long", _ => null,
            };
            if (name is not null) return name;
            if (!t.IsGenericType) return t.Name;
            return $"{t.Name[..t.Name.IndexOf('`')]}<{string.Join(", ", t.GetGenericArguments().Select(Friendly))}>";
        }
        catch { return t.Name; }
    }

    static T Safe<T>(Func<T> f) { try { return f(); } catch { return default!; } }
}
