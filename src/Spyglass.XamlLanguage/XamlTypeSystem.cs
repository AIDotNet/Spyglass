using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Spyglass.XamlLanguage;

public enum XamlMemberKind { Property, AttachedProperty, Event }

/// <summary>A property or event as XAML sees it.</summary>
/// <param name="ValueType">The property's type (from StyledProperty&lt;T&gt; etc.), null for events.</param>
/// <param name="Settable">False for read-only properties that are still usable as property elements (collections).</param>
public sealed record XamlMember(string Name, XamlMemberKind Kind, Type? ValueType, Type Owner, string DocId, bool Deprecated, bool Settable = true);

/// <summary>
/// The XAML view of a project's assemblies, read with <see cref="MetadataLoadContext"/> (metadata only: nothing is loaded
/// into the hub or run). Models Avalonia's rules: xmlns URIs from [XmlnsDefinition] (per declaring assembly), <c>using:</c>
/// and <c>clr-namespace:</c> namespaces, Avalonia properties (StyledProperty/DirectProperty/AttachedProperty fields), routed
/// and CLR events, markup extensions. Not thread-safe; <see cref="XamlProjectCache"/> serialises access.
/// </summary>
public sealed class XamlTypeSystem : IDisposable
{
    public const string AvaloniaNs = "https://github.com/avaloniaui";
    public const string XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    readonly MetadataLoadContext _mlc;
    readonly List<Assembly> _assemblies = [];
    readonly Dictionary<string, List<(Assembly Assembly, string Namespace)>> _xmlns = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _prefixes = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<Type>> _byNamespace = new(StringComparer.Ordinal);   // CLR namespace → types (all assemblies)
    readonly Dictionary<Type, IReadOnlyList<XamlMember>> _members = [];
    readonly Dictionary<Type, IReadOnlyList<XamlMember>> _attached = [];
    readonly Dictionary<string, Dictionary<string, string>?> _docs = new(StringComparer.OrdinalIgnoreCase);
    List<Type>? _attachedOwners;

    /// <summary>The project's own assembly (TargetPath), when built.</summary>
    public Assembly? ProjectAssembly { get; }

    /// <summary>Every xmlns URI some assembly defines, e.g. https://github.com/avaloniaui, a component library's URI.</summary>
    public IReadOnlyCollection<string> XmlnsUris => _xmlns.Keys;

    XamlTypeSystem(MetadataLoadContext mlc, Assembly? project)
    {
        _mlc = mlc;
        ProjectAssembly = project;
    }

    /// <summary>Framework reference assemblies resolve base types but are not searched for XAML types.</summary>
    static bool IsFramework(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name is "mscorlib" or "netstandard" || name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal)
               || path.Contains($"{Path.DirectorySeparatorChar}packs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    public static XamlTypeSystem Load(IReadOnlyList<string> references, string? projectAssembly)
    {
        var paths = references.Where(File.Exists).ToList();
        var project = projectAssembly is not null && File.Exists(projectAssembly) ? projectAssembly : null;
        if (project is not null) paths.Add(project);
        // One path per simple name (PathAssemblyResolver picks by version anyway; the project's own build must win).
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths) byName[Path.GetFileNameWithoutExtension(p)] = p;
        if (project is not null) byName[Path.GetFileNameWithoutExtension(project)] = project;
        // Without System.Runtime (a broken restore) MetadataLoadContext cannot resolve anything: use the running runtime's.
        if (!byName.ContainsKey("System.Runtime") && !byName.ContainsKey("mscorlib"))
            foreach (var p in Directory.EnumerateFiles(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"))
                byName.TryAdd(Path.GetFileNameWithoutExtension(p), p);

        var mlc = new MetadataLoadContext(new PathAssemblyResolver(byName.Values));
        Assembly? projectAsm = null;
        var assemblies = new List<Assembly>();
        foreach (var p in byName.Values)
        {
            if (IsFramework(p) && p != project) continue;
            try
            {
                var a = mlc.LoadFromAssemblyPath(p);
                assemblies.Add(a);
                if (p == project) projectAsm = a;
            }
            catch { /* not a .NET assembly */ }
        }
        var ts = new XamlTypeSystem(mlc, projectAsm);
        ts._assemblies.AddRange(assemblies);
        ts.Index();
        return ts;
    }

    void Index()
    {
        foreach (var a in _assemblies)
        {
            IList<CustomAttributeData> attrs;
            try { attrs = a.GetCustomAttributesData(); } catch { continue; }
            foreach (var at in attrs)
            {
                string? name;
                try { name = at.AttributeType.FullName; } catch { continue; }
                if (at.ConstructorArguments is not [{ Value: string uri }, { Value: string value }]) continue;
                if (name is "Avalonia.Metadata.XmlnsDefinitionAttribute")
                {
                    if (!_xmlns.TryGetValue(uri, out var list)) _xmlns[uri] = list = [];
                    list.Add((a, value));
                }
                else if (name is "Avalonia.Metadata.XmlnsPrefixAttribute") _prefixes.TryAdd(uri, value);
            }
            foreach (var t in ExportedTypes(a))
            {
                var ns = t.Namespace ?? "";
                if (!_byNamespace.TryGetValue(ns, out var list)) _byNamespace[ns] = list = [];
                list.Add(t);
            }
        }
    }

    static IEnumerable<Type> ExportedTypes(Assembly a)
    {
        try { return a.GetExportedTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>().Where(t => t.IsPublic); }
        catch { return []; }
    }

    // ---------- namespaces ----------

    /// <summary>The prefix a library asks for ([XmlnsPrefix]), else one derived from the URI's last segment.</summary>
    public string SuggestedPrefix(string uri)
    {
        if (_prefixes.TryGetValue(uri, out var p)) return p;
        var seg = uri.TrimEnd('/').Split('/', ':', '.').LastOrDefault(s => s.Length > 0) ?? "ns";
        var letters = new string(seg.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return letters.Length == 0 || char.IsDigit(letters[0]) ? "ns" : letters.Length > 8 ? letters[..8] : letters;
    }

    /// <summary>Public types an xmlns value maps to: an xmlns URI, <c>using:Ns</c> or <c>clr-namespace:Ns[;assembly=A]</c>.</summary>
    public IEnumerable<Type> TypesIn(string xmlns)
    {
        if (xmlns == XamlNs) return [];
        if (ClrNamespace(xmlns) is { } clr)
        {
            var list = _byNamespace.GetValueOrDefault(clr.Namespace) ?? [];
            return clr.Assembly is null ? list : list.Where(t => string.Equals(t.Assembly.GetName().Name, clr.Assembly, StringComparison.OrdinalIgnoreCase));
        }
        if (!_xmlns.TryGetValue(xmlns, out var maps)) return [];
        return maps.SelectMany(m => (_byNamespace.GetValueOrDefault(m.Namespace) ?? []).Where(t => t.Assembly == m.Assembly)).Distinct();
    }

    static (string Namespace, string? Assembly)? ClrNamespace(string xmlns)
    {
        if (xmlns.StartsWith("using:", StringComparison.Ordinal)) return (xmlns[6..].Trim(), null);
        if (!xmlns.StartsWith("clr-namespace:", StringComparison.Ordinal)) return null;
        var parts = xmlns[14..].Split(';');
        var asm = parts.Skip(1).Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("assembly=", StringComparison.Ordinal))?[9..];
        return (parts[0].Trim(), string.IsNullOrEmpty(asm) ? null : asm);
    }

    /// <summary>Namespaces of the project's own assembly that no xmlns URI covers (offered as <c>using:</c>).</summary>
    public IEnumerable<string> ProjectNamespaces() => ProjectAssembly is null ? [] :
        ExportedTypes(ProjectAssembly).Select(t => t.Namespace).OfType<string>().Distinct().Order(StringComparer.Ordinal);

    /// <summary>Resolves <c>prefix:Name</c> (or <c>Name</c>) with the document's prefix → xmlns map.</summary>
    public Type? Resolve(string qualifiedName, IReadOnlyDictionary<string, string> namespaces)
    {
        var colon = qualifiedName.IndexOf(':');
        var prefix = colon < 0 ? "" : qualifiedName[..colon];
        var name = colon < 0 ? qualifiedName : qualifiedName[(colon + 1)..];
        if (!namespaces.TryGetValue(prefix, out var uri)) return null;
        var types = TypesIn(uri).Where(t => t.Name == name || t.Name == name + "Extension").ToList();
        return types.FirstOrDefault(t => t.Name == name) ?? types.FirstOrDefault();
    }

    // ---------- types ----------

    /// <summary>Usable as an XAML element: a public, non-abstract, non-generic class or struct that is not an attribute/exception/delegate.</summary>
    public static bool IsElement(Type t)
    {
        try
        {
            if (!t.IsPublic || t.IsAbstract || t.IsInterface || t.IsGenericTypeDefinition || t.IsEnum || t.IsNested) return false;
            if (t.IsValueType) return false;
            if (t.Name.StartsWith('<')) return false;
            for (var b = t.BaseType; b is not null; b = b.BaseType)
                if (b.FullName is "System.Attribute" or "System.Exception" or "System.Delegate" or "System.EventArgs") return false;
            return true;
        }
        catch { return false; }
    }

    public static bool Is(Type t, string fullName)
    {
        try
        {
            for (Type? c = t; c is not null; c = c.BaseType)
                if (c.FullName == fullName) return true;
        }
        catch { /* a base type in an assembly that is not referenced */ }
        return false;
    }

    public static bool IsControl(Type t) => Is(t, "Avalonia.Controls.Control");

    /// <summary>A markup extension: Avalonia duck-types them (a ProvideValue method), plus the binding classes.</summary>
    public static bool IsMarkupExtension(Type t)
    {
        try
        {
            if (!IsElement(t)) return false;
            if (t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(m => m.Name == "ProvideValue")) return true;
            return Is(t, "Avalonia.Data.BindingBase") || Is(t, "Avalonia.Markup.Xaml.MarkupExtension");
        }
        catch { return false; }
    }

    public static string ExtensionName(Type t) =>
        t.Name.EndsWith("Extension", StringComparison.Ordinal) && t.Name.Length > 9 ? t.Name[..^9] : t.Name;

    /// <summary>Public instance properties of a plain CLR type (x:DataType view models, binding paths).</summary>
    public static IEnumerable<PropertyInfo> ClrProperties(Type t)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (Type? c = t; c is not null && c.FullName != "System.Object"; c = SafeBase(c))
        {
            PropertyInfo[] props;
            try { props = c.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly); } catch { continue; }
            foreach (var p in props)
                if (Safe(() => p.GetIndexParameters().Length == 0) && seen.Add(p.Name)) yield return p;
        }
        // Interfaces (a DataType that is an interface).
        if (t.IsInterface)
            foreach (var i in Safe(() => t.GetInterfaces(), []))
            foreach (var p in Safe(() => i.GetProperties(BindingFlags.Public | BindingFlags.Instance), []))
                if (seen.Add(p.Name)) yield return p;
    }

    /// <summary>Properties and events usable as attributes of <paramref name="t"/>, base classes included.</summary>
    public IReadOnlyList<XamlMember> Members(Type t)
    {
        if (_members.TryGetValue(t, out var cached)) return cached;
        var result = new Dictionary<string, XamlMember>(StringComparer.Ordinal);
        for (Type? c = t; c is not null && c.FullName != "System.Object"; c = SafeBase(c))
        {
            foreach (var f in Safe(() => c.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly), []))
            {
                if (AvaloniaProperty(f) is { } ap && !ap.Attached)
                {
                    // A read-only DirectProperty (Bounds, IsEffectivelyEnabled) has the same field type as a writable one:
                    // its CLR property has no public setter.
                    var settable = !ap.Direct || Safe(() => c.GetProperty(ap.Name, BindingFlags.Public | BindingFlags.Instance)?.SetMethod is { IsPublic: true });
                    if (!settable && (ap.ValueType is null || !IsCollection(ap.ValueType))) continue;   // usable nowhere in XAML
                    result.TryAdd(ap.Name, new XamlMember(ap.Name, XamlMemberKind.Property, ap.ValueType, c, PropertyDocId(c, ap.Name), Obsolete(f), settable));
                }
                else if (RoutedEvent(f) is { } ev)
                    result.TryAdd(ev, new XamlMember(ev, XamlMemberKind.Event, null, c, $"E:{c.FullName}.{ev}", Obsolete(f)));
            }
            foreach (var p in Safe(() => c.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), []))
            {
                if (result.ContainsKey(p.Name) || !Safe(() => p.GetIndexParameters().Length == 0)) continue;
                var settable = Safe(() => p.SetMethod is { IsPublic: true });
                if (!settable && !IsCollection(p.PropertyType)) continue;
                result[p.Name] = new XamlMember(p.Name, XamlMemberKind.Property, p.PropertyType, c, $"P:{c.FullName}.{p.Name}", Obsolete(p), settable);
            }
            foreach (var e in Safe(() => c.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), []))
                result.TryAdd(e.Name, new XamlMember(e.Name, XamlMemberKind.Event, null, c, $"E:{c.FullName}.{e.Name}", Obsolete(e)));
        }
        return _members[t] = result.Values.OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Attached properties and routed events <paramref name="owner"/> declares (Grid.Row, Button.Click…).</summary>
    public IReadOnlyList<XamlMember> Attached(Type owner)
    {
        if (_attached.TryGetValue(owner, out var cached)) return cached;
        var result = new Dictionary<string, XamlMember>(StringComparer.Ordinal);
        for (Type? c = owner; c is not null && c.FullName != "System.Object"; c = SafeBase(c))
            foreach (var f in Safe(() => c.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly), []))
            {
                if (AvaloniaProperty(f) is { Attached: true } ap)
                    result.TryAdd(ap.Name, new XamlMember(ap.Name, XamlMemberKind.AttachedProperty, ap.ValueType, c, PropertyDocId(c, ap.Name), Obsolete(f)));
                else if (c == owner && RoutedEvent(f) is { } ev)
                    result.TryAdd(ev, new XamlMember(ev, XamlMemberKind.Event, null, c, $"E:{c.FullName}.{ev}", Obsolete(f)));
            }
        return _attached[owner] = result.Values.OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Types that declare attached properties (Grid, Canvas, DockPanel, ToolTip, a library's own…).</summary>
    public IReadOnlyList<Type> AttachedOwners()
    {
        if (_attachedOwners is not null) return _attachedOwners;
        var list = new List<Type>();
        foreach (var t in _byNamespace.Values.SelectMany(x => x))
        {
            if (!t.IsPublic || t.IsGenericTypeDefinition) continue;
            if (Safe(() => t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly), []).Any(f => AvaloniaProperty(f) is { Attached: true }))
                list.Add(t);
        }
        return _attachedOwners = list;
    }

    /// <summary>The member an attribute name refers to on <paramref name="element"/>: <c>Width</c>, <c>Grid.Row</c>, <c>local:Ext.Hint</c>.</summary>
    public XamlMember? Member(Type? element, string attribute, IReadOnlyDictionary<string, string> namespaces)
    {
        var colon = attribute.IndexOf(':');
        var dot = attribute.LastIndexOf('.');
        if (dot > colon)
        {
            var owner = Resolve(attribute[..dot], namespaces);
            var name = attribute[(dot + 1)..];
            return owner is null ? null
                : Attached(owner).FirstOrDefault(m => m.Name == name) ?? Members(owner).FirstOrDefault(m => m.Name == name);
        }
        return element is null || colon >= 0 ? null : Members(element).FirstOrDefault(m => m.Name == attribute);
    }

    sealed record AvaloniaPropertyInfo(string Name, Type? ValueType, bool Attached, bool Direct);

    static AvaloniaPropertyInfo? AvaloniaProperty(FieldInfo f)
    {
        if (!f.Name.EndsWith("Property", StringComparison.Ordinal) || f.Name.Length <= 8) return null;
        try
        {
            var ft = f.FieldType;
            var def = ft.IsGenericType ? ft.GetGenericTypeDefinition().FullName : ft.FullName;
            var name = f.Name[..^8];
            var args = ft.IsGenericType ? ft.GetGenericArguments() : [];
            return def switch
            {
                "Avalonia.StyledProperty`1" => new(name, args[0], false, false),
                "Avalonia.AttachedProperty`1" => new(name, args[0], true, false),
                "Avalonia.DirectProperty`2" => new(name, args[1], false, true),
                _ => null,
            };
        }
        catch { return null; }
    }

    static string? RoutedEvent(FieldInfo f)
    {
        if (!f.Name.EndsWith("Event", StringComparison.Ordinal) || f.Name.Length <= 5) return null;
        try
        {
            var ft = f.FieldType;
            var def = ft.IsGenericType ? ft.GetGenericTypeDefinition().FullName : ft.FullName;
            return def is "Avalonia.Interactivity.RoutedEvent" or "Avalonia.Interactivity.RoutedEvent`1" ? f.Name[..^5] : null;
        }
        catch { return null; }
    }

    static string PropertyDocId(Type owner, string name) => $"P:{owner.FullName}.{name}";

    static bool IsCollection(Type t) => Safe(() => t.FullName != "System.String"
        && t.GetInterfaces().Any(i => i.FullName is "System.Collections.IList" or "System.Collections.IDictionary"
            || (i.IsGenericType && i.GetGenericTypeDefinition().FullName is "System.Collections.Generic.ICollection`1" or "System.Collections.Generic.IDictionary`2")));

    static bool Obsolete(MemberInfo m) => Safe(() => m.GetCustomAttributesData().Any(a => a.AttributeType.FullName == "System.ObsoleteAttribute"));

    static Type? SafeBase(Type t) { try { return t.BaseType; } catch { return null; } }

    static T Safe<T>(Func<T> f, T fallback = default!)
    {
        try { return f(); } catch { return fallback; }
    }

    // ---------- values ----------

    /// <summary>
    /// Literal values XAML accepts for a property of type <paramref name="t"/>: enum names, True/False, the type's own static
    /// instances (FontWeight.Bold, Cursor…), named colors for brushes and colors.
    /// </summary>
    public IReadOnlyList<(string Value, string? DocId)> Values(Type t)
    {
        if (Safe(() => t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Nullable`1")) t = t.GetGenericArguments()[0];
        var name = t.FullName;
        if (name == "System.Boolean") return [("True", null), ("False", null)];
        if (Safe(() => t.IsEnum))
            return Safe(() => t.GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (f.Name, (string?)$"F:{t.FullName}.{f.Name}")).ToList(), []);
        if (name is "Avalonia.Media.IBrush" or "Avalonia.Media.Brush" or "Avalonia.Media.ISolidColorBrush" or "Avalonia.Media.SolidColorBrush"
            or "Avalonia.Media.Color" or "Avalonia.Media.Color?")
        {
            var colors = _byNamespace.GetValueOrDefault("Avalonia.Media")?.FirstOrDefault(x => x.FullName == "Avalonia.Media.Colors");
            if (colors is not null)
                return Safe(() => colors.GetProperties(BindingFlags.Public | BindingFlags.Static).Select(p => (p.Name, (string?)$"P:Avalonia.Media.Colors.{p.Name}")).ToList(), []);
        }
        var statics = new List<(string, string?)>();
        foreach (var p in Safe(() => t.GetProperties(BindingFlags.Public | BindingFlags.Static), []))
            if (Safe(() => p.PropertyType == t)) statics.Add((p.Name, $"P:{t.FullName}.{p.Name}"));
        foreach (var f in Safe(() => t.GetFields(BindingFlags.Public | BindingFlags.Static), []))
            if (Safe(() => f.FieldType == t)) statics.Add((f.Name, $"F:{t.FullName}.{f.Name}"));
        return statics;
    }

    public static bool IsTypeValued(Type? t) => t?.FullName == "System.Type";

    // ---------- documentation ----------

    /// <summary>The &lt;summary&gt; of a doc id (T:, P:, F:, E:) from the assembly's XML documentation file, as plain text.</summary>
    public string? Documentation(string docId)
    {
        var typeName = docId[2..];
        if (!docId.StartsWith("T:", StringComparison.Ordinal))
        {
            var dot = typeName.LastIndexOf('.');
            if (dot > 0) typeName = typeName[..dot];
        }
        var ns = typeName.LastIndexOf('.') is var i and > 0 ? typeName[..i] : "";
        var type = _byNamespace.GetValueOrDefault(ns)?.FirstOrDefault(t => t.FullName == typeName);
        if (type is null) return null;
        var docs = Docs(type.Assembly);
        if (docs is null) return null;
        if (docs.TryGetValue(docId, out var s)) return s;
        // Avalonia documents properties on the field (FooProperty) or the CLR property; try the other one.
        if (docId.StartsWith("P:", StringComparison.Ordinal) && docs.TryGetValue("F:" + docId[2..] + "Property", out s)) return s;
        if (docId.StartsWith("E:", StringComparison.Ordinal) && docs.TryGetValue("F:" + docId[2..] + "Event", out s)) return s;
        return null;
    }

    Dictionary<string, string>? Docs(Assembly a)
    {
        var location = Safe(() => a.Location, "");
        if (string.IsNullOrEmpty(location)) return null;
        if (_docs.TryGetValue(location, out var cached)) return cached;
        Dictionary<string, string>? map = null;
        var xml = Path.ChangeExtension(location, ".xml");
        if (File.Exists(xml))
        {
            try
            {
                map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var m in XDocument.Load(xml).Descendants("member"))
                    if (m.Attribute("name")?.Value is { } id && m.Element("summary") is { } summary)
                        map[id] = Plain(summary);
            }
            catch { map = null; }
        }
        return _docs[location] = map;
    }

    static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    static string Plain(XElement summary)
    {
        var sb = new StringBuilder();
        foreach (var n in summary.Nodes())
        {
            if (n is XText t) sb.Append(t.Value);
            else if (n is XElement e)
            {
                var cref = e.Attribute("cref")?.Value ?? e.Attribute("langword")?.Value ?? e.Attribute("name")?.Value;
                if (e.Name.LocalName is "see" or "seealso" or "paramref" or "typeparamref" && cref is not null)
                {
                    var shortName = cref.Contains(':') ? cref[(cref.IndexOf(':') + 1)..] : cref;
                    var parts = shortName.Split('(')[0].Split('.');
                    sb.Append('`').Append(parts.Length > 1 && cref.StartsWith("T:", StringComparison.Ordinal) ? parts[^1] : string.Join('.', parts.TakeLast(2))).Append('`');
                }
                else sb.Append(e.Value);
            }
        }
        return Spaces.Replace(sb.ToString(), " ").Trim();
    }

    public void Dispose() => _mlc.Dispose();
}
