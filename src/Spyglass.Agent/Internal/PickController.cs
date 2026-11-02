using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Spyglass.Protocol.Messages;

namespace Spyglass.Agent.Internal;

/// <summary>
/// In-app element picking (design/ux/03 §2.2). While active, pointer input to the app is swallowed:
/// hover highlights, click selects. Default picks the "user element" (nearest element whose TemplatedParent is null);
/// holding Alt keeps the deepest template part (spike S1 finding). Esc cancels. UI-thread only.
/// </summary>
internal sealed class PickController(TreeWalker walker, Highlighter highlighter)
{
    readonly List<(TopLevel TopLevel, Cursor? Cursor)> _hooked = new();
    Visual? _hovered;
    DateTime _lastHover;
    bool _swallowRelease;

    public event Action<int>? Hover;
    public event Action<int?, IReadOnlyList<int>?>? Selected;

    public bool IsActive => _hooked.Count > 0;

    public void Start()
    {
        if (IsActive) return;
        HookNewRoots();
    }

    /// <summary>Hooks windows/popups that are not hooked yet (also called when one opens during picking).</summary>
    public void HookNewRoots()
    {
        foreach (var (tl, _, _, _) in walker.Roots())
        {
            if (_hooked.Any(h => ReferenceEquals(h.TopLevel, tl))) continue;
            tl.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            tl.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            tl.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            tl.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
            _hooked.Add((tl, tl.Cursor));
            tl.Cursor = new Cursor(StandardCursorType.Cross);
        }
    }

    public void Stop()
    {
        foreach (var (tl, cursor) in _hooked)
        {
            tl.RemoveHandler(InputElement.PointerMovedEvent, OnMoved);
            tl.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
            tl.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            tl.Cursor = cursor;
            // keep the release handler until the click that ended picking has been swallowed
            if (!_swallowRelease) tl.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
        }
        _hooked.Clear();
        _hovered = null;
    }

    void OnMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not TopLevel tl) return;
        e.Handled = true;
        var target = HitTest(tl, e.GetPosition(tl), e.KeyModifiers.HasFlag(KeyModifiers.Alt));
        if (target is null || ReferenceEquals(target, _hovered)) return;
        _hovered = target;
        highlighter.Show(target, HighlightStyle.Box, 0);
        if ((DateTime.UtcNow - _lastHover).TotalMilliseconds >= 50)
        {
            _lastHover = DateTime.UtcNow;
            Hover?.Invoke(walker.Registry.IdOf(target));
        }
    }

    void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TopLevel tl) return;
        e.Handled = true;
        var target = HitTest(tl, e.GetPosition(tl), e.KeyModifiers.HasFlag(KeyModifiers.Alt));
        _swallowRelease = true;
        Stop();
        if (target is null) { Selected?.Invoke(null, null); return; }
        highlighter.Show(target, HighlightStyle.Box, 1500);
        Selected?.Invoke(walker.Registry.IdOf(target), walker.PathOf(target, TreeKind.Visual));
    }

    void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_swallowRelease) return;
        e.Handled = true;
        _swallowRelease = false;
        if (sender is TopLevel tl && !IsActive) tl.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Stop();
        highlighter.Hide();
        Selected?.Invoke(null, null);
    }

    static Visual? HitTest(TopLevel tl, Point p, bool deepest)
    {
        var hit = tl.GetVisualsAt(p, v => v is not Highlighter.Adorner && v.IsVisible).FirstOrDefault();
        if (hit is null || deepest) return hit;
        // Climb to the element the developer wrote: out of control templates, and out of the TextBlock/AccessText a
        // ContentPresenter generates for string content (Content="Save"). Content built from the developer's own
        // DataTemplate is kept, since that XAML is theirs.
        Visual cur = hit;
        while (true)
        {
            if (cur is StyledElement { TemplatedParent: Visual owner }) { cur = owner; continue; }
            if (cur.GetVisualParent() is ContentPresenter { Content: string } presenter && presenter.TemplatedParent is not null)
            {
                cur = presenter;
                continue;
            }
            return cur;
        }
    }
}
