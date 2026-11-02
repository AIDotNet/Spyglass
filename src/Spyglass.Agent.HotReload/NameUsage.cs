using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Xml.Linq;

namespace Spyglass.Agent.HotReload;

/// <summary>
/// Property-style x:Name (Avalonia's OnlyProperties generator, which Spyglass.HotReload switches on, design §5.1) looks the
/// control up on every access: once a reload removes the element, code-behind that uses the name gets null and the next
/// <c>Name.IsVisible = …</c> throws on the UI thread, which takes the app down. Finds such names before anything is applied.
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Debug-only hot reload.")]
[UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Debug-only hot reload.")]
[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Debug-only hot reload.")]
internal static class NameUsage
{
    const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    const string XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Generated name properties of <paramref name="type"/> that <paramref name="xaml"/> no longer declares but code still reads, with one reader each.</summary>
    public static List<(string Name, string UsedIn)> RemovedButUsed(Type type, string xaml)
    {
        var result = new List<(string, string)>();
        var generated = type.GetProperties(All)
            .Where(p => p.GetMethod is not null && p.GetCustomAttribute<System.CodeDom.Compiler.GeneratedCodeAttribute>()?.Tool?.StartsWith("Avalonia.Generators", StringComparison.Ordinal) == true)
            .ToList();
        if (generated.Count == 0) return result;

        var declared = Names(xaml);
        var removed = generated.Where(p => !declared.Contains(p.Name)).ToList();
        if (removed.Count == 0) return result;

        var getters = removed.ToDictionary(p => p.GetMethod!.MetadataToken, p => p.Name);
        var skip = generated.Select(p => p.GetMethod!).ToHashSet();
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in Methods(type))
        {
            if (skip.Contains(m)) continue;
            byte[]? il;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); }
            catch { continue; }
            if (il is null) continue;
            // call (0x28) / callvirt (0x6F) followed by a MethodDef token of a removed getter.
            for (var i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] is not (0x28 or 0x6F)) continue;
                var token = BitConverter.ToInt32(il, i + 1);
                if (getters.TryGetValue(token, out var name) && !found.ContainsKey(name))
                    found[name] = $"{type.Name}.{Readable(m)}";
            }
        }
        foreach (var (name, usedIn) in found) result.Add((name, usedIn));
        return result;
    }

    static IEnumerable<MethodBase> Methods(Type t)
    {
        foreach (var m in t.GetMethods(All)) yield return m;
        foreach (var c in t.GetConstructors(All)) yield return c;
        foreach (var n in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))   // lambdas, closures, local functions' state machines
            foreach (var m in Methods(n)) yield return m;
    }

    /// <summary>"&lt;.ctor&gt;b__12_0" → "ctor", "&lt;LoadAsync&gt;d__5.MoveNext" → "LoadAsync".</summary>
    static string Readable(MethodBase m)
    {
        var n = m.Name;
        if (n == "MoveNext" && m.DeclaringType?.Name is { } owner && owner.StartsWith('<')) n = owner;
        if (n.StartsWith('<') && n.IndexOf('>') is > 1 and var end) n = n[1..end];
        return n == ".ctor" ? "ctor" : n;
    }

    static HashSet<string> Names(string xaml)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in XDocument.Parse(xaml).Root!.DescendantsAndSelf())
            if ((el.Attribute(XName.Get("Name", XamlNs)) ?? el.Attribute("Name"))?.Value is { Length: > 0 } n)
                set.Add(n);
        return set;
    }
}
