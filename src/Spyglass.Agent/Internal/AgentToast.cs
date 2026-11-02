using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Spyglass.Agent.Internal;

/// <summary>
/// A small, theme-independent message shown inside the app (e.g. "Starting Spyglass…") on the window's
/// OverlayLayer. Hard-coded colours because the host theme is unknown (design/ux/03 §3). UI-thread only.
/// </summary>
internal static class AgentToast
{
    static Control? _current;

    public static void Show(TopLevel? topLevel, string message, bool warning = false, int durationMs = 3500)
    {
        if (topLevel is null || OverlayLayer.GetOverlayLayer(topLevel) is not { } layer) return;
        Hide();
        var toast = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 0x1F, 0x1F, 0x1F)),
            BorderBrush = new SolidColorBrush(warning ? Color.FromRgb(0xF3, 0xAD, 0x20) : Color.FromRgb(0x3D, 0x7D, 0xF2)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8),
            Margin = new Thickness(16),
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Child = new TextBlock { Text = "Spyglass · " + message, Foreground = Brushes.White, FontSize = 12, TextWrapping = TextWrapping.Wrap },
        };
        // OverlayLayer is a Canvas-like panel: stretch the host to the window so alignment works.
        var host = new Panel { Width = layer.Bounds.Width, Height = layer.Bounds.Height, IsHitTestVisible = false, Children = { toast } };
        layer.Children.Add(host);
        _current = host;
        DispatcherTimer.RunOnce(() => { if (ReferenceEquals(_current, host)) Hide(); }, TimeSpan.FromMilliseconds(durationMs));
    }

    /// <summary>True when <paramref name="o"/> is part of the toast currently shown (excluded from event tracing).</summary>
    public static bool Contains(object o) =>
        _current is { } c && o is Visual v && (ReferenceEquals(v, c) || c.IsVisualAncestorOf(v));

    public static void Hide()
    {
        if (_current?.Parent is Panel p) p.Children.Remove(_current);
        _current = null;
    }
}
