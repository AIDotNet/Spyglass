using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Spyglass.App.Core.Network;

namespace Spyglass.App.Views;

/// <summary>
/// Chrome's Network "Overview" strip: one thin bar per request on a time axis; drag to select a range,
/// double-click to reset (design/network/02-chrome-parity.md §5). Colours come from Arc tokens set in XAML.
/// </summary>
public sealed class NetworkOverviewControl : Control
{
    public static readonly StyledProperty<OverviewModel?> ModelProperty = AvaloniaProperty.Register<NetworkOverviewControl, OverviewModel?>(nameof(Model));
    public static readonly StyledProperty<double?> SelectionStartProperty = AvaloniaProperty.Register<NetworkOverviewControl, double?>(nameof(SelectionStart));
    public static readonly StyledProperty<double?> SelectionEndProperty = AvaloniaProperty.Register<NetworkOverviewControl, double?>(nameof(SelectionEnd));
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<NetworkOverviewControl, IBrush?>(nameof(TrackBrush));
    public static readonly StyledProperty<IBrush?> BarBrushProperty = AvaloniaProperty.Register<NetworkOverviewControl, IBrush?>(nameof(BarBrush));
    public static readonly StyledProperty<IBrush?> SelectionBrushProperty = AvaloniaProperty.Register<NetworkOverviewControl, IBrush?>(nameof(SelectionBrush));
    public static readonly StyledProperty<IBrush?> SelectionEdgeBrushProperty = AvaloniaProperty.Register<NetworkOverviewControl, IBrush?>(nameof(SelectionEdgeBrush));
    public static readonly StyledProperty<IBrush?> TextBrushProperty = AvaloniaProperty.Register<NetworkOverviewControl, IBrush?>(nameof(TextBrush));

    const double LabelHeight = 14, BarHeight = 3, BarGap = 1.5, Pad = 4;

    static NetworkOverviewControl()
    {
        AffectsRender<NetworkOverviewControl>(ModelProperty, SelectionStartProperty, SelectionEndProperty, TrackBrushProperty, BarBrushProperty,
            SelectionBrushProperty, SelectionEdgeBrushProperty, TextBrushProperty);
    }

    public OverviewModel? Model { get => GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public double? SelectionStart { get => GetValue(SelectionStartProperty); set => SetValue(SelectionStartProperty, value); }
    public double? SelectionEnd { get => GetValue(SelectionEndProperty); set => SetValue(SelectionEndProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? BarBrush { get => GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public IBrush? SelectionBrush { get => GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    public IBrush? SelectionEdgeBrush { get => GetValue(SelectionEdgeBrushProperty); set => SetValue(SelectionEdgeBrushProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    /// <summary>Raised on release with the selected range in ms from the model origin, or (null, null) on double click.</summary>
    public event Action<double?, double?>? RangeChanged;

    double? _dragStartX, _dragX;

    public NetworkOverviewControl() { Cursor = new Cursor(StandardCursorType.Cross); }

    double PlotWidth => Math.Max(1, Bounds.Width - 2 * Pad);
    double MsToX(double ms, OverviewModel m) => Pad + ms / m.TotalMs * PlotWidth;
    double XToMs(double x, OverviewModel m) => Math.Clamp((x - Pad) / PlotWidth, 0, 1) * m.TotalMs;

    public override void Render(DrawingContext ctx)
    {
        var rect = new Rect(Bounds.Size);
        ctx.DrawRectangle(TrackBrush, null, rect, 6, 6);
        if (Model is not { Bars.Count: > 0 } m) return;

        var plotTop = Pad;
        var plotHeight = Math.Max(BarHeight, rect.Height - LabelHeight - 2 * Pad);
        var laneCount = Math.Max(1, (int)((plotHeight + BarGap) / (BarHeight + BarGap)));
        var lanes = NetworkOverview.Lanes(m.Bars, laneCount);
        for (var i = 0; i < m.Bars.Count; i++)
        {
            var x1 = MsToX(m.Bars[i].StartMs, m);
            var x2 = Math.Max(x1 + 2, MsToX(m.Bars[i].EndMs, m));
            ctx.DrawRectangle(BarBrush, null, new Rect(x1, plotTop + lanes[i] * (BarHeight + BarGap), x2 - x1, BarHeight), 1.5, 1.5);
        }

        // Selection: a tinted window with edge lines (live while dragging).
        double? a = SelectionStart, b = SelectionEnd;
        if (_dragStartX is { } ds && _dragX is { } dx) { a = XToMs(Math.Min(ds, dx), m); b = XToMs(Math.Max(ds, dx), m); }
        if (a is { } from && b is { } to)
        {
            var x1 = MsToX(from, m);
            var x2 = Math.Max(x1 + 1, MsToX(to, m));
            ctx.DrawRectangle(SelectionBrush, null, new Rect(x1, 0, x2 - x1, rect.Height));
            var edge = new Pen(SelectionEdgeBrush, 1.5);
            ctx.DrawLine(edge, new Point(x1, 0), new Point(x1, rect.Height));
            ctx.DrawLine(edge, new Point(x2, 0), new Point(x2, rect.Height));
        }

        DrawLabel(ctx, "0", Pad, rect.Height - LabelHeight - 1, false);
        DrawLabel(ctx, NetworkCopy.Duration(m.TotalMs), rect.Width - Pad, rect.Height - LabelHeight - 1, true);
    }

    void DrawLabel(DrawingContext ctx, string text, double x, double y, bool rightAlign)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 10, TextBrush);
        ctx.DrawText(ft, new Point(rightAlign ? x - ft.Width : x, y));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetCurrentPoint(this);
        if (!p.Properties.IsLeftButtonPressed) return;
        if (e.ClickCount >= 2) { RangeChanged?.Invoke(null, null); _dragStartX = _dragX = null; return; }
        _dragStartX = _dragX = p.Position.X;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStartX is null) return;
        _dragX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragStartX is not { } ds || _dragX is not { } dx) return;
        _dragStartX = _dragX = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
        if (Model is { Bars.Count: > 0 } m && Math.Abs(dx - ds) >= 3)
            RangeChanged?.Invoke(XToMs(Math.Min(ds, dx), m), XToMs(Math.Max(ds, dx), m));
    }
}
