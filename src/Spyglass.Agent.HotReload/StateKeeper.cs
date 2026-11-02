using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Spyglass.Agent.HotReload;

/// <summary>
/// Captures view state that a re-populate would reset, keyed by the element's path in the reloaded instance's logical tree
/// (design §4.4): selection, unbound text and caret, expanders/toggles, focus, and scroll offsets (including the template
/// ScrollViewer of lists). Values that are bound after the reload are left to the binding.
/// The logical tree exists right after populate; the visual tree only after the next layout, so scroll offsets are
/// restored later.
/// </summary>
internal static class StateKeeper
{
    public sealed class Snapshot
    {
        public readonly List<(string Path, Type Owner, AvaloniaProperty Property, object? Value)> Values = [];
        public readonly List<(string Path, Vector Offset)> Scroll = [];
        public string? FocusPath;
        public bool IsEmpty => Values.Count == 0 && Scroll.Count == 0 && FocusPath is null;
    }

    static readonly (Type Owner, AvaloniaProperty Property)[] Tracked =
    [
        (typeof(SelectingItemsControl), SelectingItemsControl.SelectedIndexProperty),
        (typeof(TextBox), TextBox.TextProperty),
        (typeof(TextBox), TextBox.CaretIndexProperty),
        (typeof(Expander), Expander.IsExpandedProperty),
        (typeof(ToggleButton), ToggleButton.IsCheckedProperty),
    ];

    public static Snapshot Capture(StyledElement root)
    {
        var s = new Snapshot();
        Visual? focused = TopLevel.GetTopLevel(root as Visual)?.FocusManager?.GetFocusedElement() as Visual;
        foreach (var (e, path) in Walk(root))
        {
            foreach (var (owner, p) in Tracked)
                if (owner.IsInstanceOfType(e) && !IsBound(e, p))
                    s.Values.Add((path, owner, p, e.GetValue(p)));
            if (ScrollerOf(e) is { Offset: var o } && o != default)
                s.Scroll.Add((path, o));
            if (focused is not null && (ReferenceEquals(e, focused) || e is Visual v && v.IsVisualAncestorOf(focused) && e is TemplatedControl))
                s.FocusPath = path;
        }
        return s;
    }

    public static void Restore(StyledElement root, Snapshot s)
    {
        if (s.IsEmpty) return;
        var byPath = Walk(root).GroupBy(x => x.Path).ToDictionary(g => g.Key, g => g.First().Element);
        foreach (var (path, owner, p, value) in s.Values)
        {
            // Not p.OwnerType: shared properties (TextBox.Text is TextBlock.Text.AddOwner) report the first owner.
            if (!byPath.TryGetValue(path, out var e) || !owner.IsInstanceOfType(e) || IsBound(e, p)) continue;
            try { e.SetValue(p, value); } catch { /* the new XAML changed the element: skip */ }
        }
        if (s.FocusPath is { } fp && byPath.TryGetValue(fp, out var f) && f is InputElement ie)
            Dispatcher.UIThread.Post(() => ie.Focus(), DispatcherPriority.Loaded);
        if (s.Scroll.Count > 0)
            // Offsets are clamped to the extent, which is only known after layout.
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var (path, offset) in s.Scroll)
                    if (byPath.TryGetValue(path, out var e) && ScrollerOf(e) is { } sv) sv.Offset = offset;
            }, DispatcherPriority.Background);
    }

    static ScrollViewer? ScrollerOf(StyledElement e) => e switch
    {
        ScrollViewer sv => sv,
        ItemsControl ic => ic.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(),
        _ => null,
    };

    static bool IsBound(AvaloniaObject o, AvaloniaProperty p) => BindingOperations.GetBindingExpressionBase(o, p) is not null;

    /// <summary>Logical descendants with a stable path: #Name when set, else Type[index among same-type siblings].</summary>
    static IEnumerable<(StyledElement Element, string Path)> Walk(StyledElement root)
    {
        var stack = new Stack<(StyledElement, string)>();
        stack.Push((root, ""));
        while (stack.Count > 0)
        {
            var (e, path) = stack.Pop();
            yield return (e, path);
            var counts = new Dictionary<string, int>();
            foreach (var c in ((ILogical)e).LogicalChildren.OfType<StyledElement>())
            {
                var type = c.GetType().Name;
                counts[type] = counts.TryGetValue(type, out var n) ? n + 1 : 0;
                var seg = c.Name is { Length: > 0 } name ? "#" + name : $"{type}[{counts[type]}]";
                stack.Push((c, path + "/" + seg));
            }
        }
    }
}
