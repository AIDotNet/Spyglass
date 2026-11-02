using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Spyglass.Protocol.Messages;

namespace Spyglass.Agent.Internal;

/// <summary>Builds <see cref="NodeDto"/>s and enumerates roots. UI-thread only.</summary>
internal sealed class TreeWalker(NodeRegistry registry)
{
    public NodeRegistry Registry { get; } = registry;

    // ---------- roots ----------

    /// <summary>
    /// Every TopLevel the app shows: all desktop windows (including owned dialogs and tool windows),
    /// the single-view MainView, and popups hosted in their own PopupRoot. Owner is the owner Window
    /// for windows and the Popup element for popups.
    /// </summary>
    public IReadOnlyList<(TopLevel TopLevel, RootKind Kind, string? Title, StyledElement? Owner)> Roots()
    {
        var roots = new List<(TopLevel, RootKind, string?, StyledElement?)>();
        switch (Application.Current?.ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                foreach (var w in desktop.Windows) roots.Add((w, RootKind.Window, w.Title, w.Owner as Window));
                break;
            case ISingleViewApplicationLifetime { MainView: { } mv } when TopLevel.GetTopLevel(mv) is { } tl:
                roots.Add((tl, RootKind.MainView, null, null));
                break;
        }
        // Windows the lifetime does not list (custom/no lifetime, headless, embedding).
        foreach (var w in OpenWindows.Current)
            if (!roots.Any(r => ReferenceEquals(r.Item1, w)))
                roots.Add((w, RootKind.Window, w.Title, w.Owner as Window));

        // Open popups hosted in their own PopupRoot (desktop). Overlay popups are already inside the window tree.
        // IPopupHostProvider is internal in Avalonia 12, so go through Popup.Child (spike S1, U1).
        var seen = roots.Select(r => r.Item1).ToHashSet();
        foreach (var (tl, _, _, _) in roots.ToList())
            foreach (var popup in tl.GetVisualDescendants().OfType<Popup>())
                if (popup is { IsOpen: true, Child: { } child } && TopLevel.GetTopLevel(child) is { } ptl && seen.Add(ptl))
                    roots.Add((ptl, RootKind.Popup, null, popup));
        return roots;
    }

    public TopLevel? TopLevelOf(object o) => o is Visual v ? TopLevel.GetTopLevel(v) : null;

    // ---------- children / parents ----------

    public static IEnumerable<object> Children(object o, TreeKind tree) => tree switch
    {
        TreeKind.Logical when o is ILogical l => l.LogicalChildren,
        TreeKind.Visual when o is Visual v => v.GetVisualChildren().Where(c => c is not Highlighter.Adorner),
        _ => [],
    };

    /// <summary>Parent in the given tree. A TopLevel is a root: in Avalonia 12 its visual parent is an internal TopLevelHost.</summary>
    public static object? Parent(object o, TreeKind tree) => o is TopLevel ? null : tree switch
    {
        TreeKind.Logical when o is ILogical l => l.LogicalParent,
        TreeKind.Visual when o is Visual v => v.GetVisualParent(),
        _ => null,
    };

    public List<int> PathOf(object o, TreeKind tree)
    {
        var path = new List<int>();
        for (var cur = o; cur is not null; cur = Parent(cur, tree)) path.Add(Registry.IdOf(cur));
        path.Reverse();
        return path;
    }

    // ---------- DTOs ----------

    public NodeDto Build(object o, TreeKind tree, int depth)
    {
        var children = Children(o, tree).ToList();
        return Describe(o) with
        {
            ChildCount = children.Count,
            Children = depth == 0 ? null : children.Select(c => Build(c, tree, depth < 0 ? -1 : depth - 1)).ToList(),
        };
    }

    public NodeDto Describe(object o)
    {
        var se = o as StyledElement;
        var classes = se?.Classes;
        return new NodeDto
        {
            Id = Registry.IdOf(o),
            Type = o.GetType().Name,
            TypeFull = o.GetType().FullName,
            Name = se?.Name,
            Classes = classes is { Count: > 0 } ? classes.Where(c => !c.StartsWith(':')).ToList() is { Count: > 0 } cl ? cl : null : null,
            PseudoClasses = classes is { Count: > 0 } ? classes.Where(c => c.StartsWith(':')).ToList() is { Count: > 0 } pc ? pc : null : null,
            IsTemplatePart = se?.TemplatedParent is not null,
            IsVisible = o is not Visual v || v.IsEffectivelyVisible,
            IsEnabled = o is not InputElement ie || ie.IsEffectivelyEnabled,
            IsFocused = o is InputElement { IsFocused: true },
            Text = Truncate(TextOf(o)),
            Placeholder = Truncate(PlaceholderOf(o)),
            Bounds = BoundsOf(o),
        };
    }

    public static string? TextOf(object o) => o switch
    {
        TextBlock tb => tb.Text,
        TextBox t => t.Text,
        HeaderedContentControl { Header: string h } => h,
        HeaderedItemsControl { Header: string h } => h,
        ContentControl { Content: string s } => s,
        ContentControl { Content: TextBlock { Text: { } t } } => t,
        ComboBox { SelectedItem: ContentControl { Content: string s } } => s,
        ComboBox { SelectedItem: string s } => s,
        _ => null,
    };

    /// <summary>TextBox hint text: PlaceholderText in Avalonia 12, Watermark in 11.</summary>
    static string? PlaceholderOf(object o) =>
#if AVALONIA11
        (o as TextBox)?.Watermark;
#else
        (o as TextBox)?.PlaceholderText;
#endif

    static string? Truncate(string? s) =>
        string.IsNullOrEmpty(s) ? null : s!.Length <= 80 ? s : s.Substring(0, 79) + "…";

    public static RectData? BoundsOf(object o)
    {
        if (o is not Visual v) return null;
        var tl = TopLevel.GetTopLevel(v);
        if (tl is null || ReferenceEquals(tl, v)) return new RectData(0, 0, v.Bounds.Width, v.Bounds.Height);
        var p = v.TranslatePoint(default, tl);
        return p is { } pt ? new RectData(Math.Round(pt.X, 1), Math.Round(pt.Y, 1), v.Bounds.Width, v.Bounds.Height) : null;
    }

    /// <summary>CSS-like path, e.g. <c>Window#Main > StackPanel > Button#Save.primary</c>, skipping template parts.</summary>
    public string SelectorOf(object o)
    {
        var parts = new List<string>();
        for (var cur = o; cur is not null; cur = Parent(cur, TreeKind.Visual))
        {
            if (cur is StyledElement { TemplatedParent: not null } && !ReferenceEquals(cur, o)) continue;
            parts.Add(Compound(cur));
        }
        parts.Reverse();
        return string.Join(" > ", parts);
    }

    public static string Compound(object o)
    {
        var s = o.GetType().Name;
        if (o is StyledElement se)
        {
            if (!string.IsNullOrEmpty(se.Name)) s += "#" + se.Name;
            foreach (var c in se.Classes.Where(c => !c.StartsWith(':'))) s += "." + c;
        }
        return s;
    }

    public IEnumerable<object> DescendantsAndSelf(object root, TreeKind tree)
    {
        var stack = new Stack<object>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            yield return cur;
            var kids = Children(cur, tree).ToList();
            for (var i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
        }
    }
}
