using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Diagnostics;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Spyglass.Agent.Internal;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Agent.HotReload;

/// <summary>
/// Applies XAML text to the running app (design/hot-reload/README.md §4). UI thread only.
/// <list type="bullet">
/// <item>x:Class views/windows: re-populate every live instance in place, and install !XamlIlPopulateOverride so instances
/// created later (DataTemplates, new windows) get the new XAML too.</item>
/// <item>App.axaml (x:Class of an Application): re-populate <see cref="Application.Current"/>.</item>
/// <item>Styles / ResourceDictionary documents without x:Class (HR5, spike S8): compiled includes are the included
/// document's root object itself; find those objects in every style/resource host and replace them, then re-apply the
/// documents that merge this one (MergeResourceInclude copies entries) or use its keys as StaticResource.</item>
/// </list>
/// Every compile is a group with the classless documents the text includes, at their current (last good) text, so includes
/// see the newest version of each other.
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Debug-only; the runtime XAML compiler is not trim-safe by design.")]
[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Debug-only; reflects over the app's own x:Class types.")]
[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Debug-only; hot reload is unavailable without JIT (UnavailableReason).")]
internal sealed class XamlReloader
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const string XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Time budget per UI-thread slice; instances beyond it continue at background priority (design §4.3, R8).</summary>
    static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(16);

    sealed record GoodText(string Text, string Hash);

    /// <summary>One per process: the agent's protocol handlers and the .NET Hot Reload handler share the "last good" texts.</summary>
    public static XamlReloader Shared { get; } = new();

    readonly Dictionary<string, GoodText> _lastGood = new();
    readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Objects this reloader created for classless documents (runtime-compiled objects carry no source path).</summary>
    readonly ConditionalWeakTable<object, string> _origins = new();

    /// <summary>Raised on the UI thread after every apply that did something (including failures), dependents included.</summary>
    public event Action<HotReloadResult>? Applied;

    public async Task<HotReloadResult> ApplyAsync(string uri, string xaml, HotReloadSource source, long version)
    {
        if (SpyglassHotReload.UnavailableReason() is { } reason)
            throw new SpyglassProtocolException(ErrorCodes.HotReloadUnavailable, $"Hot reload is unavailable: {reason}.");
        var doc = SpyglassHotReload.Find(uri)
            ?? throw new SpyglassProtocolException(ErrorCodes.XamlDocumentUnknown, $"No XAML document '{uri}' is compiled into this app.");

        await _gate.WaitAsync();
        try
        {
            var dependents = new List<HotReloadResult>();
            var result = await ApplyCoreAsync(doc, xaml, source, version, force: false, [doc.Uri], dependents);
            if (result.Skipped is null) Applied?.Invoke(result);
            foreach (var d in dependents) Applied?.Invoke(d);
            return result;
        }
        finally { _gate.Release(); }
    }

    async Task<HotReloadResult> ApplyCoreAsync(RegisteredDocument doc, string xaml, HotReloadSource source, long version,
        bool force, HashSet<string> visited, List<HotReloadResult> dependents)
    {
        var sw = Stopwatch.StartNew();
        var hash = Hash(xaml);
        var last = LastGood(doc);
        HotReloadResult Result(bool ok, int n, IReadOnlyList<XamlDiagnostic> d, string? skipped = null, bool restart = false, bool rolledBack = false) =>
            new(doc.Uri, ok, n, Math.Round(sw.Elapsed.TotalMilliseconds, 1), d, source, version, skipped, restart, rolledBack);

        if (!force && last.Hash == hash) return Result(true, 0, [], skipped: "same content");

        // Cheap structural checks first: XML well-formedness (with positions), x:Class and root element unchanged.
        if (CheckStructure(xaml, last.Text, doc.Class) is { } structural)
            return structural.Restart ? Result(false, 0, [structural.Diagnostic], restart: true) : Result(false, 0, [structural.Diagnostic]);

        if (doc.Class is null)
            return await ApplyClasslessAsync(doc, xaml, last, hash, visited, dependents, Result);

        var type = doc.ClassType;
        if (type is null)
            return Result(false, 0, [Error($"Type {doc.Class} is not in {doc.Assembly.GetName().Name}; rebuild the app.")]);

        // Property-style x:Name returns null once its element is gone: code-behind still using it would crash the app.
        if (!typeof(Application).IsAssignableFrom(type) && NameUsage.RemovedButUsed(type, xaml) is { Count: > 0 } used)
            return Result(false, 0, used.Select(u => Error(
                $"x:Name \"{u.Name}\" is still used by {u.UsedIn}; without the element it would be null there and crash the app. " +
                "Remove that use from the code-behind first, or keep the element.")).ToList());

        var instances = LiveInstances(type);
        var diagnostics = new List<XamlDiagnostic>();
        var config = Config(doc, diagnostics);
        var applied = 0;
        var slice = Stopwatch.StartNew();

        foreach (var instance in instances)
        {
            var state = instance is StyledElement se ? StateKeeper.Capture(se) : null;
            var code = instance is StyledElement se2 ? CaptureCode(se2, last.Text, doc) : null;
            try
            {
                Populate(instance, doc, xaml, last.Text, config);
            }
            catch (Exception ex)
            {
                diagnostics.AddRange(XamlErrors.From(ex));
                // Compile errors throw before anything new is applied, but Populate already cleared the instance:
                // put the last good XAML back (also covers runtime errors half-way through, spike H14).
                var rolledBack = TryRestore(instance, doc, last.Text, xaml);
                if (rolledBack && instance is StyledElement back && code is not null) CodeBehindState.Restore(back, code, last.Text);
                if (applied == 0 && rolledBack)
                    return Result(false, 0, diagnostics, rolledBack: false);   // nothing changed anywhere: a plain compile/load error
                return Result(false, applied, diagnostics, rolledBack: rolledBack, restart: !rolledBack);
            }
            if (instance is StyledElement el)
            {
                RefreshNamedFields(el);
                if (code is not null) CodeBehindState.Restore(el, code, xaml);   // before the hooks: they may re-wire on top
                InvokeHooks(el);
                StateKeeper.Restore(el, state!);   // logical tree is ready now; scroll offsets wait for layout inside
            }
            applied++;

            if (slice.Elapsed > Slice)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                slice.Restart();
            }
        }

        if (!typeof(Application).IsAssignableFrom(type)) InstallOverride(type, doc, xaml);
        _lastGood[doc.Uri] = new GoodText(xaml, hash);
        if (applied == 0) diagnostics.Add(new XamlDiagnostic(XamlDiagnosticSeverity.Info, $"No live {type.Name}; new instances will use this XAML."));
        return Result(true, applied, diagnostics);
    }

    // ---------- classless Styles / ResourceDictionary (HR5) ----------

    async Task<HotReloadResult> ApplyClasslessAsync(RegisteredDocument doc, string xaml, GoodText last, string hash,
        HashSet<string> visited, List<HotReloadResult> dependents,
        Func<bool, int, IReadOnlyList<XamlDiagnostic>, string?, bool, bool, HotReloadResult> result)
    {
        var diagnostics = new List<XamlDiagnostic>();
        object fresh;
        try { fresh = LoadWithIncludes(doc, null, xaml, Config(doc, diagnostics)); }   // compile check; nothing live touched
        catch (Exception ex) { return result(false, 0, XamlErrors.From(ex).ToList(), null, false, false); }

        // 1. Live objects that came from this document (compiled StyleInclude/ResourceInclude, or our earlier replacements).
        var replaced = 0;
        foreach (var origin in FindOrigins(doc))
        {
            var obj = replaced == 0 ? fresh : LoadWithIncludes(doc, null, xaml, Config(doc, []));   // one instance per use
            if (!origin.Accepts(obj)) continue;
            origin.Replace(obj);
            _origins.AddOrUpdate(obj, doc.Uri);
            replaced++;
        }
        _lastGood[doc.Uri] = new GoodText(xaml, hash);

        // 2. Documents whose compiled form copied this one (MergeResourceInclude) or read its keys once (StaticResource).
        var keys = Keys(last.Text).Union(Keys(xaml)).ToHashSet();
        var instances = replaced;
        foreach (var dep in SpyglassHotReload.Documents)
        {
            if (!visited.Add(dep.Uri)) continue;
            var text = LastGood(dep).Text;
            if (!MergesOrReads(dep, text, doc, keys)) { visited.Remove(dep.Uri); continue; }
            var r = await ApplyCoreAsync(dep, text, HotReloadSource.Direct, 0, force: true, visited, dependents);
            dependents.Add(r);
            if (r.Ok) instances += r.Instances;
            else diagnostics.Add(new XamlDiagnostic(XamlDiagnosticSeverity.Warning,
                $"{Path.GetFileName(dep.Path)} uses this document and could not be refreshed: {r.Diagnostics.FirstOrDefault()?.Message}"));
        }

        if (instances == 0)
            diagnostics.Add(new XamlDiagnostic(XamlDiagnosticSeverity.Info, "Not used by anything live; documents that include it later get this version."));
        return result(true, instances, diagnostics, null, false, false);
    }

    /// <summary>A place in a style or resource host that holds an object created from a document.</summary>
    sealed record Origin(Func<object, bool> Accepts, Action<object> Replace);

    List<Origin> FindOrigins(RegisteredDocument doc)
    {
        var found = new List<Origin>();
        var seenHosts = new HashSet<object>(ReferenceEqualityComparer.Instance);

        void Styles(IList<IStyle> list)
        {
            if (!seenHosts.Add(list)) return;
            for (var i = 0; i < list.Count; i++)
            {
                var item = list[i];
                var index = i;
                if (IsFrom(item, doc)) found.Add(new Origin(o => o is IStyle, o => list[index] = (IStyle)o));
                else if (item is IList<IStyle> nested) Styles(nested);
                    if (item is Avalonia.Styling.Styles s) Resources(s.Resources);
            }
        }

        void Resources(IResourceDictionary dict)
        {
            if (!seenHosts.Add(dict)) return;
            var merged = dict.MergedDictionaries;
            for (var i = 0; i < merged.Count; i++)
            {
                var item = merged[i];
                var index = i;
                if (IsFrom(item, doc)) found.Add(new Origin(o => o is IResourceProvider, o => merged[index] = (IResourceProvider)o));
                else if (item is IResourceDictionary nested) Resources(nested);
            }
            foreach (var (variant, provider) in dict.ThemeDictionaries.ToList())
            {
                if (IsFrom(provider, doc)) found.Add(new Origin(o => o is IThemeVariantProvider, o => dict.ThemeDictionaries[variant] = (IThemeVariantProvider)o));
                else if (provider is IResourceDictionary nested) Resources(nested);
            }
        }

        if (Application.Current is { } app)
        {
            Styles(app.Styles);
            Resources(app.Resources);
        }
        foreach (var (tl, _, _, _) in new TreeWalker(new NodeRegistry()).Roots())
            foreach (var v in tl.GetSelfAndVisualDescendants())
            {
                if (v is StyledElement se) Styles(se.Styles);
                if (v is Control c) Resources(c.Resources);
            }
        return found;
    }

    bool IsFrom(object item, RegisteredDocument doc)
    {
        if (_origins.TryGetValue(item, out var uri)) return SpyglassHotReload.SameUri(uri, doc.Uri);
        return item is AvaloniaObject ao && XamlSourceInfo.GetXamlSourceInfo(ao) is { SourceUri: { } su }
            && string.Equals(su.IsFile ? su.LocalPath : su.ToString(), doc.Path, SpyglassHotReload.PathComparison);
    }

    static readonly Regex KeyAttribute = new("""x:Key\s*=\s*"([^"{}]+)" """.Trim(), RegexOptions.Compiled);

    static IEnumerable<string> Keys(string text) => KeyAttribute.Matches(text).Select(m => m.Groups[1].Value);

    /// <summary><paramref name="dep"/>'s text merges <paramref name="changed"/> or reads one of its keys as a StaticResource.</summary>
    static bool MergesOrReads(RegisteredDocument dep, string text, RegisteredDocument changed, HashSet<string> keys)
    {
        if (dep.Uri == changed.Uri) return false;
        foreach (var (kind, uri) in Includes(dep, text))
            if (kind == "MergeResourceInclude" && SpyglassHotReload.SameUri(uri, changed.Uri)) return true;
        if (keys.Count == 0 || !text.Contains("StaticResource", StringComparison.Ordinal)) return false;
        return keys.Any(k => Regex.IsMatch(text, @"\{StaticResource\s+(ResourceKey\s*=\s*)?" + Regex.Escape(k) + @"\s*\}"));
    }

    static readonly Regex IncludeElement = new(
        """<\s*(?:\w+:)?(StyleInclude|ResourceInclude|MergeResourceInclude)\b[^>]*?\bSource\s*=\s*"([^"]+)"[^>]*>""",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Include elements in <paramref name="text"/> with their sources resolved to absolute avares URIs.</summary>
    static IEnumerable<(string Kind, string Uri)> Includes(RegisteredDocument doc, string text)
    {
        foreach (Match m in IncludeElement.Matches(text))
        {
            var source = m.Groups[2].Value;
            Uri? resolved = null;
            // Only "scheme://" is absolute: on Unix Uri.TryCreate("/X.axaml", Absolute) succeeds as file:///X.axaml.
            try { resolved = source.Contains("://", StringComparison.Ordinal) ? new Uri(source) : new Uri(new Uri(doc.Uri), source); }
            catch (UriFormatException) { }
            if (resolved is not null) yield return (m.Groups[1].Value, resolved.ToString());
        }
    }

    /// <summary>
    /// Compiles <paramref name="xaml"/> together with the classless documents it includes, at their last good text, so a
    /// StyleInclude/ResourceInclude/MergeResourceInclude gets the newest version (spike S8). Returns the main object.
    /// </summary>
    object LoadWithIncludes(RegisteredDocument doc, object? root, string xaml, RuntimeXamlLoaderConfiguration config)
    {
        // Document = source path: objects get the same XamlSourceInfo (file:///…:line) as compiled ones, so "open in editor",
        // xaml= lookups and the classless origin search keep working after a reload (without it: file://runtimexaml0/).
        var group = new List<RuntimeXamlLoaderDocument> { (root is null
            ? new RuntimeXamlLoaderDocument(new Uri(doc.Uri), xaml)
            : new RuntimeXamlLoaderDocument(new Uri(doc.Uri), root, xaml)) };
        group[0].Document = doc.Path;
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { doc.Uri };
        var queue = new Queue<(RegisteredDocument, string)>([(doc, xaml)]);
        while (queue.Count > 0)
        {
            var (d, text) = queue.Dequeue();
            foreach (var (_, uri) in Includes(d, text))
                if (added.Add(uri) && SpyglassHotReload.Find(uri) is { Class: null } inc)
                {
                    var t = LastGood(inc).Text;
                    group.Add(new RuntimeXamlLoaderDocument(new Uri(inc.Uri), t) { Document = inc.Path });
                    queue.Enqueue((inc, t));
                }
        }
        if (group.Count == 1) return AvaloniaRuntimeXamlLoader.Load(group[0], config);
        return AvaloniaRuntimeXamlLoader.LoadGroup(group, config)[0]!;
    }

    GoodText LastGood(RegisteredDocument doc)
    {
        if (_lastGood.TryGetValue(doc.Uri, out var a)) return a;
        var text = doc.CompiledText;
        return _lastGood[doc.Uri] = new GoodText(text, Hash(text));
    }

    static RuntimeXamlLoaderConfiguration Config(RegisteredDocument doc, List<XamlDiagnostic> sink) => new()
    {
        LocalAssembly = doc.Assembly,
        UseCompiledBindingsByDefault = doc.CompiledBindings,   // without it binding errors are not compile errors (spike H10b/H10c)
        CreateSourceInfo = true,                               // keep XamlSourceInfo, so "open in IDE" still works after a reload
        DiagnosticHandler = d =>
        {
            if (d.Severity is RuntimeXamlDiagnosticSeverity.Warning or RuntimeXamlDiagnosticSeverity.Info)
                sink.Add(new XamlDiagnostic(d.Severity == RuntimeXamlDiagnosticSeverity.Warning ? XamlDiagnosticSeverity.Warning : XamlDiagnosticSeverity.Info,
                    d.Title, d.LineNumber, d.LinePosition, d.Id));
            return d.Severity;
        },
    };

    /// <summary>Steps 3–5 of design §4.3: undo the previous root attributes, clear styles/resources, populate.</summary>
    void Populate(AvaloniaObject instance, RegisteredDocument doc, string xaml, string previous, RuntimeXamlLoaderConfiguration config)
    {
        RootReset.Undo(instance, previous);
        switch (instance)
        {
            case Application app:
                app.Styles.Clear();
                Clear(app.Resources);
                break;
            case StyledElement se:
                se.Styles.Clear();
                if (se is Control c) Clear(c.Resources);
                break;
        }
        LoadWithIncludes(doc, instance, xaml, config);
    }

    /// <summary>Re-populating only adds: merged and theme dictionaries would pile up otherwise.</summary>
    static void Clear(IResourceDictionary d)
    {
        d.Clear();
        d.MergedDictionaries.Clear();
        d.ThemeDictionaries.Clear();
    }

    /// <summary>Re-populates from <paramref name="good"/> after a failed apply of <paramref name="failed"/>.</summary>
    bool TryRestore(AvaloniaObject instance, RegisteredDocument doc, string good, string failed)
    {
        try
        {
            // The failed text may have partly applied root attributes; undo both before restoring.
            try { RootReset.Undo(instance, failed); } catch { /* malformed: nothing of it was applied */ }
            Populate(instance, doc, good, good, Config(doc, []));
            if (instance is StyledElement se) RefreshNamedFields(se);
            return true;
        }
        catch { return false; }
    }

    // ---------- structure ----------

    sealed record Structural(XamlDiagnostic Diagnostic, bool Restart);

    static Structural? CheckStructure(string xaml, string previous, string? @class)
    {
        XElement root;
        try { root = XDocument.Parse(xaml, LoadOptions.SetLineInfo).Root!; }
        catch (XmlException ex) { return new Structural(new XamlDiagnostic(XamlDiagnosticSeverity.Error, ex.Message, ex.LineNumber, ex.LinePosition, "XML"), false); }

        var xClass = root.Attribute(XName.Get("Class", XamlNs))?.Value;
        if (xClass != @class)
            return new Structural(Error($"x:Class changed from {@class ?? "(none)"} to {xClass ?? "(none)"}; restart the app to apply.", root), true);
        try
        {
            var before = XDocument.Parse(previous).Root!.Name;
            if (before != root.Name)
                return new Structural(Error($"Root element changed from {before.LocalName} to {root.Name.LocalName}; restart the app to apply.", root), true);
        }
        catch (XmlException) { /* previous text unreadable: skip the check */ }
        return null;
    }

    static XamlDiagnostic Error(string message, XElement? at = null) =>
        at is IXmlLineInfo li && li.HasLineInfo()
            ? new XamlDiagnostic(XamlDiagnosticSeverity.Error, message, li.LineNumber, li.LinePosition)
            : new XamlDiagnostic(XamlDiagnosticSeverity.Error, message);

    // ---------- instances ----------

    /// <summary>
    /// Every live instance of <paramref name="type"/>: the current Application for App.axaml, otherwise every top level's
    /// visual tree, popups included (spike R8, H13).
    /// </summary>
    static List<AvaloniaObject> LiveInstances(Type type)
    {
        if (typeof(Application).IsAssignableFrom(type))
            return Application.Current is { } app && type.IsInstanceOfType(app) ? [app] : [];
        var found = new List<AvaloniaObject>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var (tl, _, _, _) in new TreeWalker(new NodeRegistry()).Roots())
            foreach (var v in tl.GetSelfAndVisualDescendants())
                if (Matches(v, type) && seen.Add(v)) found.Add(v);
        return found;
    }

    /// <summary>The type itself, or a subclass that has no XAML of its own (it runs the base InitializeComponent).</summary>
    static bool Matches(Visual v, Type type)
    {
        var t = v.GetType();
        if (t == type) return true;
        return type.IsAssignableFrom(t) && t.GetField("!XamlIlPopulateOverride", Static | BindingFlags.DeclaredOnly) is null;
    }

    // ---------- after populate ----------

    /// <summary>
    /// Field-style x:Name (Avalonia's default InitializeComponent) still points at the old controls after a re-populate
    /// (spike H7): point them at the new ones. Property-style names (OnlyProperties, design §5.1) need nothing.
    /// </summary>
    static void RefreshNamedFields(StyledElement instance)
    {
        if (NameScope.GetNameScope(instance) is not { } scope) return;
        for (var t = instance.GetType(); t is not null && t != typeof(StyledElement); t = t.BaseType)
            foreach (var f in t.GetFields(Instance | BindingFlags.DeclaredOnly))
                if (!f.IsInitOnly && f.GetCustomAttribute<System.CodeDom.Compiler.GeneratedCodeAttribute>()?.Tool?.StartsWith("Avalonia.Generators", StringComparison.Ordinal) == true
                    && scope.Find(f.Name) is { } c && f.FieldType.IsInstanceOfType(c))
                    f.SetValue(instance, c);
    }

    static CodeBehindState.Snapshot? CaptureCode(StyledElement instance, string previous, RegisteredDocument doc)
    {
        try { return CodeBehindState.Capture(instance, previous, doc.Assembly); }
        catch (Exception ex) { Debug.WriteLine($"[Spyglass] hot reload: code-behind state not captured: {ex.Message}"); return null; }
    }

    static void InvokeHooks(StyledElement instance)
    {
        try
        {
            if (instance is ISpyglassHotReloadAware aware) aware.OnHotReloaded();
            instance.GetType().GetMethod("InitializeComponentState", Instance, Type.EmptyTypes)?.Invoke(instance, null);
        }
        catch (Exception ex) { Debug.WriteLine($"[Spyglass] hot reload hook failed: {ex}"); }
    }

    // ---------- new instances (design §4.2, spike R3) ----------

    /// <summary>
    /// Points the type's XamlIl populate override at the new XAML. The delegate must never throw (that would break every
    /// constructor, R3e): on failure it runs the compiled XAML through the trampoline instead (R3g).
    /// </summary>
    void InstallOverride(Type type, RegisteredDocument doc, string xaml)
    {
        var field = type.GetField("!XamlIlPopulateOverride", Static);
        var trampoline = type.GetMethod("!XamlIlPopulateTrampoline", Static);
        if (field is null || trampoline is null) return;

        Action<object>? self = null;
        self = o =>
        {
            try { LoadWithIncludes(doc, o, xaml, Config(doc, [])); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Spyglass] hot reload: new {type.Name} falls back to compiled XAML: {ex.Message}");
                field.SetValue(null, null);
                try { trampoline.Invoke(null, [o]); }
                finally { field.SetValue(null, self); }
            }
        };
        field.SetValue(null, self);
    }

    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>Implement on a view to run code after Spyglass hot-reloads its XAML (re-wire handlers, re-read named controls…).</summary>
public interface ISpyglassHotReloadAware
{
    void OnHotReloaded();
}
