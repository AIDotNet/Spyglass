using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Spyglass.Agent.HotReload;

/// <summary>
/// Undoes what the previous XAML did to the root instance itself before it is re-populated (design §4.3 step 3).
/// Re-populating only adds: a removed root attribute would keep its old local value (spike R4e) and root event handlers
/// would be subscribed once more per reload (R4h). Children need nothing: they are rebuilt.
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Debug-only hot reload.")]
[UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Debug-only hot reload.")]
[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Debug-only hot reload.")]
internal static class RootReset
{
    const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Undo(AvaloniaObject root, string previousXaml)
    {
        var el = XDocument.Parse(previousXaml).Root!;
        var rootType = root.GetType();

        // Attributes in the default (Avalonia) namespace: Padding="20", Button.Click="OnClick", Grid.Row="1", Loaded="…".
        foreach (var a in el.Attributes())
            if (!a.IsNamespaceDeclaration && a.Name.NamespaceName == "")
                UndoMember(root, rootType, a.Name.LocalName, a.Value);

        // Property elements on the root: <UserControl.Background>, <Window.KeyBindings>… (Styles/Resources are cleared anyway).
        var prefix = el.Name.LocalName + ".";
        foreach (var child in el.Elements())
            if (child.Name.LocalName.StartsWith(prefix, StringComparison.Ordinal))
                UndoMember(root, rootType, child.Name.LocalName[prefix.Length..], handler: null);
    }

    static void UndoMember(AvaloniaObject root, Type rootType, string name, string? handler)
    {
        var owner = rootType;
        var member = name;
        var dot = name.IndexOf('.');
        if (dot > 0)
        {
            owner = ResolveType(name[..dot]) ?? rootType;
            member = name[(dot + 1)..];
        }

        if (member == "KeyBindings" && root is InputElement ie) { ie.KeyBindings.Clear(); return; }

        if (handler is not null && FindStatic<RoutedEvent>(owner, member + "Event") is { } re)
        {
            if (root is Interactive i && HandlerDelegate(root, handler, typeof(EventHandler<>).MakeGenericType(re.EventArgsType)) is { } d)
                i.RemoveHandler(re, d);
            return;
        }
        if (handler is not null && rootType.GetEvent(member) is { EventHandlerType: { } eht } ev)
        {
            if (HandlerDelegate(root, handler, eht) is { } d) ev.RemoveEventHandler(root, d);
            return;
        }
        if (FindStatic<AvaloniaProperty>(owner, member + "Property") is { IsReadOnly: false } p)
            root.ClearValue(p);
    }

    static Delegate? HandlerDelegate(object target, string method, Type delegateType)
    {
        var m = target.GetType().GetMethod(method, AnyInstance);
        return m is null ? null : Delegate.CreateDelegate(delegateType, target, m, throwOnBindFailure: false);
    }

    static T? FindStatic<T>(Type t, string name) where T : class
    {
        for (var c = t; c != null; c = c.BaseType)
            if (c.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)?.GetValue(null) is T v) return v;
        return null;
    }

    static readonly string[] Namespaces = ["Avalonia.Controls.", "Avalonia.Controls.Primitives.", "Avalonia.Input.", "Avalonia.Layout.", "Avalonia."];

    static Type? ResolveType(string shortName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic || asm.GetName().Name?.StartsWith("Avalonia", StringComparison.Ordinal) != true) continue;
            foreach (var ns in Namespaces)
                if (asm.GetType(ns + shortName) is { } t) return t;
        }
        return null;
    }
}
