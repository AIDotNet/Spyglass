using System.Reflection;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Interactivity;

namespace S6.HotReload;

/// Undo what the PREVIOUS XAML did on the root instance itself (spike R4e/R4h):
/// local property values set by root attributes, and event handlers attached by root attributes.
static class RootReset
{
    const string AvaloniaNs = "https://github.com/avaloniaui";

    public static string Undo(AvaloniaObject root, string oldXaml)
    {
        var el = XDocument.Parse(oldXaml).Root!;
        int cleared = 0, removed = 0;
        foreach (var a in el.Attributes())
        {
            if (a.IsNamespaceDeclaration || a.Name.NamespaceName != "") continue; // skip x:Class, x:DataType, d:...
            var name = a.Name.LocalName;
            Type owner = root.GetType(); var member = name;
            var dot = name.IndexOf('.');
            if (dot > 0) { owner = ResolveAvaloniaType(name[..dot]) ?? owner; member = name[(dot + 1)..]; }

            if (FindStatic<RoutedEvent>(owner, member + "Event") is { } re)
            {
                var m = root.GetType().GetMethod(a.Value, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m != null && root is Interactive i)
                {
                    var d = Delegate.CreateDelegate(typeof(EventHandler<>).MakeGenericType(re.EventArgsType), root, m, false)
                            ?? Delegate.CreateDelegate(typeof(EventHandler<RoutedEventArgs>), root, m);
                    i.RemoveHandler(re, d); removed++;
                }
                continue;
            }
            if (FindStatic<AvaloniaProperty>(owner, member + "Property") is { } p && !p.IsReadOnly)
            { root.ClearValue(p); cleared++; continue; }
            // CLR events (e.g. AttachedToVisualTree) would go here via EventInfo.RemoveEventHandler.
        }
        return $"cleared {cleared} props, removed {removed} handlers";
    }

    static T? FindStatic<T>(Type t, string name) where T : class
    {
        for (var c = t; c != null; c = c.BaseType)
            if (c.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy)?.GetValue(null) is T v) return v;
        return null;
    }

    static Type? ResolveAvaloniaType(string n) =>
        AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("Avalonia"))
            .Select(a => a.GetType("Avalonia.Controls." + n) ?? a.GetType("Avalonia.Input." + n) ?? a.GetType("Avalonia." + n))
            .FirstOrDefault(t => t != null);
}
