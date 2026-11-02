using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Diagnostics;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace Spyglass.Agent.HotReload;

/// <summary>
/// Carries what code-behind did to the view's named controls over to the controls a re-populate builds in their place:
/// local values set in code (<c>Rail.IsVisible = true</c>, <c>Icon.Data = Geometry.Parse(…)</c>, <c>ToolTip.SetTip</c>)
/// and routed event handlers from app code (<c>Link.Click += …</c> in the constructor). Without this, a reload silently
/// resets the controls to what the XAML alone says and buttons stop responding.
/// A value comes from code when the XAML does not set that property on the element, or sets it to a literal the live value
/// no longer equals. The XAML wins when the edit changed that attribute, or when it is a markup extension (resources,
/// bindings: re-evaluated on purpose).
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Debug-only hot reload.")]
[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Debug-only hot reload.")]
[UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Debug-only hot reload.")]
[UnconditionalSuppressMessage("Trimming", "IL2080", Justification = "Debug-only hot reload.")]
internal static class CodeBehindState
{
    const string XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>What the XAML says about one named element: attribute texts and property-element names.</summary>
    internal sealed record XamlElement(Dictionary<string, string> Attributes, HashSet<string> PropertyElements)
    {
        public bool Mentions(string key) => Attributes.ContainsKey(key) || PropertyElements.Contains(key);
    }

    internal sealed record Entry(StyledElement Old, XamlElement Xaml, List<(AvaloniaProperty Property, object? Value)> Values, List<(RoutedEvent Event, object Subscription)> Handlers);

    public sealed class Snapshot
    {
        internal readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    }

    public static Snapshot Capture(StyledElement root, string previousXaml, Assembly appAssembly)
    {
        var s = new Snapshot();
        if (NameScope.GetNameScope(root) is not { } scope) return s;
        foreach (var (name, xaml) in NamedElements(previousXaml))
        {
            if (scope.Find(name) is not StyledElement old) continue;
            s.Entries[name] = new Entry(old, xaml, LocalValues(old), AppHandlers(old, appAssembly));
        }
        return s;
    }

    /// <summary>Applies <paramref name="s"/> to the controls that <paramref name="newXaml"/> just created. Never throws.</summary>
    public static void Restore(StyledElement root, Snapshot s, string newXaml)
    {
        if (s.Entries.Count == 0 || NameScope.GetNameScope(root) is not { } scope) return;
        Dictionary<string, XamlElement> now;
        try { now = NamedElements(newXaml); }
        catch { return; }
        foreach (var (name, entry) in s.Entries)
        {
            if (scope.Find(name) is not StyledElement fresh || ReferenceEquals(fresh, entry.Old) || fresh.GetType() != entry.Old.GetType()
                || !now.TryGetValue(name, out var xaml))
                continue;
            foreach (var (p, value) in entry.Values)
            {
                var key = Key(p);
                entry.Xaml.Attributes.TryGetValue(key, out var before);
                xaml.Attributes.TryGetValue(key, out var after);
                if (before != after || xaml.PropertyElements.Contains(key) || entry.Xaml.PropertyElements.Contains(key)) continue;   // the edit owns it
                if (after is not null && (IsMarkupExtension(after) || Equals(fresh.GetValue(p), value))) continue;
                if (BindingOperations.GetBindingExpressionBase(fresh, p) is not null) continue;
                try { fresh.SetValue(p, value); } catch { /* the control rejects it now: keep the XAML value */ }
            }
            foreach (var (e, sub) in entry.Handlers)
                if (!entry.Xaml.Mentions(e.Name) && !xaml.Mentions(e.Name) && !entry.Xaml.Mentions(Key(e)) && !xaml.Mentions(Key(e)))
                    AddSubscription(fresh, e, sub);
        }
    }

    // ---------- values ----------

    static List<(AvaloniaProperty, object?)> LocalValues(StyledElement e)
    {
        var list = new List<(AvaloniaProperty, object?)>();
        var registry = AvaloniaPropertyRegistry.Instance;
        foreach (var p in registry.GetRegistered(e.GetType()).Concat(registry.GetRegisteredAttached(e.GetType())).Distinct())
        {
            if (p.IsReadOnly || p == StyledElement.NameProperty || !e.IsSet(p)) continue;
            if (e.GetDiagnostic(p) is not { Priority: BindingPriority.LocalValue } d) continue;
            if (BindingOperations.GetBindingExpressionBase(e, p) is not null) continue;
            if (d.Value is Visual or ILogical) continue;   // a child control: moving it would tear it out of the old tree mid-reload
            list.Add((p, d.Value));
        }
        return list;
    }

    static string Key(AvaloniaProperty p) => p.IsAttached ? $"{p.OwnerType.Name}.{p.Name}" : p.Name;
    static string Key(RoutedEvent e) => $"{e.OwnerType.Name}.{e.Name}";

    static bool IsMarkupExtension(string text) => text.StartsWith('{') && !text.StartsWith("{}", StringComparison.Ordinal);

    // ---------- routed event handlers ----------

    static readonly FieldInfo? HandlersField = typeof(Interactive).GetField("_eventHandlers", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>Subscriptions whose handler is app code (lambdas and methods of <paramref name="app"/>), not the control's own.</summary>
    static List<(RoutedEvent, object)> AppHandlers(StyledElement e, Assembly app)
    {
        var list = new List<(RoutedEvent, object)>();
        if (e is not Interactive || HandlersField?.GetValue(e) is not IDictionary dict) return list;
        foreach (DictionaryEntry kv in dict)
        {
            if (kv.Key is not RoutedEvent re || kv.Value is not IList subs) continue;
            foreach (var sub in subs)
                if (Handler(sub) is { } h && h.Method.DeclaringType?.Assembly == app)
                    list.Add((re, sub!));
        }
        return list;
    }

    static Delegate? Handler(object? subscription) =>
        subscription?.GetType().GetProperty("Handler")?.GetValue(subscription) as Delegate;

    static void AddSubscription(StyledElement e, RoutedEvent re, object sub)
    {
        try
        {
            if (HandlersField is null) return;
            if (HandlersField.GetValue(e) is not IDictionary dict)
            {
                dict = (IDictionary)Activator.CreateInstance(HandlersField.FieldType)!;
                HandlersField.SetValue(e, dict);
            }
            if (dict[re] is not IList subs)
            {
                subs = (IList)Activator.CreateInstance(HandlersField.FieldType.GetGenericArguments()[1])!;
                dict[re] = subs;
            }
            var h = Handler(sub);
            foreach (var existing in subs)
                if (Equals(Handler(existing), h)) return;   // re-wired by the view's own hook
            subs.Add(sub);
        }
        catch { /* Avalonia changed its internals: handlers are not carried over */ }
    }

    // ---------- XAML ----------

    /// <summary>Elements with x:Name/Name in <paramref name="xaml"/>; a name used twice (templates) is left out.</summary>
    static Dictionary<string, XamlElement> NamedElements(string xaml)
    {
        var result = new Dictionary<string, XamlElement>(StringComparer.Ordinal);
        var duplicate = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in XDocument.Parse(xaml).Root!.DescendantsAndSelf())
        {
            var name = (el.Attribute(XName.Get("Name", XamlNs)) ?? el.Attribute("Name"))?.Value;
            if (string.IsNullOrEmpty(name)) continue;
            if (!result.TryAdd(name, Describe(el))) duplicate.Add(name);
        }
        foreach (var d in duplicate) result.Remove(d);
        return result;
    }

    static XamlElement Describe(XElement el)
    {
        var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in el.Attributes())
            if (!a.IsNamespaceDeclaration && a.Name.NamespaceName == "")
                attrs[a.Name.LocalName] = a.Value;
        var props = new HashSet<string>(StringComparer.Ordinal);
        var prefix = el.Name.LocalName + ".";
        foreach (var c in el.Elements())
        {
            var n = c.Name.LocalName;
            if (n.StartsWith(prefix, StringComparison.Ordinal)) props.Add(n[prefix.Length..]);
            else if (n.Contains('.')) props.Add(n);   // attached: <Grid.Row>, <ToolTip.Tip>
        }
        return new XamlElement(attrs, props);
    }
}
