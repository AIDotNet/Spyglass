using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Spyglass.Protocol.Messages;

namespace Spyglass.Agent.Internal;

/// <summary>
/// layout/explain: how an element got its size and, if it cannot be seen, why (design/mcp/01-tools.md #10).
/// Uses LayoutInformation (public) for the constraint the parent offered. UI-thread only.
/// </summary>
internal sealed class LayoutExplainer(NodeRegistry registry, PropertyInspector props)
{
    public LayoutExplainResult Explain(int id)
    {
        var v = registry.Get<Visual>(id);
        var layout = props.Layout(id);
        var l = v as Layoutable;
        var parent = v.GetVisualAncestors().OfType<Layoutable>().FirstOrDefault();

        SizeConstraintData? constraint = null;
        RectData? slot = null;
        if (l is not null && parent is not null)
        {
            if (LayoutInformation.GetPreviousMeasureConstraint(l) is { } c) constraint = new SizeConstraintData(Finite(c.Width), Finite(c.Height));
            if (LayoutInformation.GetPreviousArrangeBounds(l) is { } a) slot = new RectData(a.X, a.Y, a.Width, a.Height);
        }

        return new LayoutExplainResult(layout,
            parent is null ? null : registry.IdOf(parent), parent?.GetType().Name,
            constraint, slot, Visibility(v, layout), Notes(v, l, parent, layout, constraint, slot));
    }

    static double? Finite(double d) => double.IsFinite(d) ? d : null;

    // ---------------------------------------------------------------- visibility

    List<VisibilityIssue> Visibility(Visual v, LayoutResult layout)
    {
        var issues = new List<VisibilityIssue>();
        VisibilityIssue Issue(string code, string message, Visual culprit) =>
            new(code, message, registry.IdOf(culprit), culprit.GetType().Name);

        var topLevel = TopLevel.GetTopLevel(v);
        if (topLevel is null)
            issues.Add(new("detached", "Not attached to a window: the element is not in a visual tree that is shown (never added, removed, or inside a closed Popup/flyout)."));

        // Ancestors from the root down so the most fundamental reason comes first.
        var chain = v.GetSelfAndVisualAncestors().Reverse().ToList();
        foreach (var node in chain)
        {
            var who = ReferenceEquals(node, v) ? "The element" : $"Ancestor {Describe(node)}";
            if (!node.IsVisible)
                issues.Add(Issue("not-visible", $"{who} has IsVisible = false" + (ReferenceEquals(node, v) ? "." : ", which hides all of its descendants."), node));
            if (node.Opacity <= 0)
                issues.Add(Issue("transparent", $"{who} has Opacity = 0" + (ReferenceEquals(node, v) ? "." : ", which makes all of its descendants fully transparent."), node));
            if (node is Visual { IsVisible: true } && node is Layoutable && node.Bounds is { } b && (b.Width <= 0 || b.Height <= 0)
                && !ReferenceEquals(node, v) && !IsZeroSizeContainerNormal(node) && node.GetVisualAncestors().All(a => a.IsVisible))
                issues.Add(Issue("zero-size-ancestor", $"{who} has zero size ({Num(b.Width)}×{Num(b.Height)}), so nothing inside it is visible.", node));
        }

        // A hidden ancestor makes everything below it zero-size: that is a consequence, not a separate reason.
        if (v.IsEffectivelyVisible && (v.Bounds.Width <= 0 || v.Bounds.Height <= 0))
            issues.Add(Issue("zero-size", $"The element has zero size ({Num(v.Bounds.Width)}×{Num(v.Bounds.Height)}). {ZeroSizeReason(v, layout)}", v));

        // Clipped / scrolled out of view: compare against each clipping ancestor's viewport.
        if (topLevel is not null && v.IsVisible && v.Bounds.Width > 0 && v.Bounds.Height > 0)
            foreach (var anc in v.GetVisualAncestors())
            {
                var clips = anc.ClipToBounds || anc is ScrollViewer || anc is ScrollContentPresenter;
                if (!clips || anc is TopLevel) continue;
                var viewport = anc is ScrollViewer sv ? new Rect(sv.Bounds.Size) : new Rect(anc.Bounds.Size);
                var origin = v.TranslatePoint(default, anc);
                if (origin is not { } o) continue;
                var rect = new Rect(o, v.Bounds.Size);
                if (!viewport.Intersects(rect) && viewport.Width > 0 && viewport.Height > 0)
                {
                    var scrolled = anc is ScrollViewer || anc is ScrollContentPresenter;
                    issues.Add(Issue(scrolled ? "scrolled-out" : "clipped",
                        scrolled
                            ? $"The element is outside the visible area of the scroll viewer {Describe(anc)}: scroll to bring it into view."
                            : $"The element lies outside {Describe(anc)}, which has ClipToBounds = true, so it is clipped away.", anc));
                    break;
                }
            }
        return issues;
    }

    /// <summary>A panel that has not been measured yet is normal during startup; only report settled layouts.</summary>
    static bool IsZeroSizeContainerNormal(Visual node) => node is Popup or AdornerLayer;

    static string ZeroSizeReason(Visual v, LayoutResult layout)
    {
        if (v is not Layoutable l) return "";
        if (l.Width == 0 || l.Height == 0) return $"Width/Height is explicitly set to 0.";
        if (l.MaxWidth == 0 || l.MaxHeight == 0) return "MaxWidth/MaxHeight is 0.";
        if (layout.DesiredSize.W == 0 && layout.DesiredSize.H == 0)
            return "Its desired size is 0×0: it has no content (empty text, no children, or no Width/Height) and the parent does not stretch it.";
        return "The parent arranged it into a zero-size slot (e.g. a Grid row/column that is Auto with no content, or a collapsed star track).";
    }

    // ---------------------------------------------------------------- how the size came about

    static List<string> Notes(Visual v, Layoutable? l, Layoutable? parent, LayoutResult layout, SizeConstraintData? constraint, RectData? slot)
    {
        var notes = new List<string>();
        if (l is null) return notes;
        var b = layout.Bounds;

        notes.Add(double.IsNaN(l.Width) && double.IsNaN(l.Height)
            ? "Width and Height are not set: the size comes from the content and the parent."
            : $"{(double.IsNaN(l.Width) ? "" : $"Width is fixed at {Num(l.Width)}. ")}{(double.IsNaN(l.Height) ? "" : $"Height is fixed at {Num(l.Height)}.")}".Trim());

        if (constraint is not null)
        {
            var offered = $"{(constraint.W is { } w ? Num(w) : "∞")}×{(constraint.H is { } h ? Num(h) : "∞")}";
            notes.Add($"{parent?.GetType().Name} offered {offered} during measure; the element asked for {Num(layout.DesiredSize.W)}×{Num(layout.DesiredSize.H)}.");
        }
        if (slot is not null)
            notes.Add($"{parent?.GetType().Name} arranged it into a {Num(slot.W)}×{Num(slot.H)} slot; final size {Num(b.W)}×{Num(b.H)}.");

        if (layout.HorizontalAlignment == "Stretch" && Math.Abs(b.W - layout.DesiredSize.W) > 0.5)
            notes.Add($"HorizontalAlignment = Stretch: it fills the available width ({Num(b.W)}) instead of its desired width ({Num(layout.DesiredSize.W)}).");
        if (layout.VerticalAlignment == "Stretch" && Math.Abs(b.H - layout.DesiredSize.H) > 0.5)
            notes.Add($"VerticalAlignment = Stretch: it fills the available height ({Num(b.H)}) instead of its desired height ({Num(layout.DesiredSize.H)}).");
        foreach (var c in layout.ActiveConstraints.Where(c => c.StartsWith("min") || c.StartsWith("max")))
            notes.Add($"{c} is limiting the final size.");

        if (parent is StackPanel sp)
            notes.Add($"Parent is a {sp.Orientation} StackPanel: along its orientation children get their desired size, across it they stretch.");
        else if (parent is Grid grid)
            notes.Add($"Parent is a Grid{(l is Control c ? $" (row {Grid.GetRow(c)}, column {Grid.GetColumn(c)})" : "")}: the cell size comes from the row/column definitions (Auto, *, or fixed).");
        else if (parent is DockPanel)
            notes.Add("Parent is a DockPanel: the last child fills the remaining space; docked children take what they need on their side.");
        else if (parent is Canvas)
            notes.Add("Parent is a Canvas: children are placed at Canvas.Left/Top with their desired size.");
        if (b.W < layout.DesiredSize.W - 0.5 || b.H < layout.DesiredSize.H - 0.5)
            notes.Add("It got less room than it wanted: content may be cut off or wrapped.");
        return notes;
    }

    static string Describe(Visual v) => v.GetType().Name + (v is StyledElement { Name: { Length: > 0 } n } ? "#" + n : "");
    static string Num(double d) => Math.Round(d, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
