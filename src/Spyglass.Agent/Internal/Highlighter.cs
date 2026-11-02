using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Spyglass.Protocol.Messages;

namespace Spyglass.Agent.Internal;

/// <summary>
/// Draws the in-app highlight on the element's AdornerLayer (spike S1, H1). The host app's theme is unknown,
/// so this is the one place colours are hard-coded (design/ux/03 §3). UI-thread only.
/// </summary>
internal sealed class Highlighter
{
    Adorner? _current;
    DispatcherTimer? _timer;

    public void Show(Visual target, HighlightStyle style, int durationMs)
    {
        Hide();
        if (target is TopLevel || AdornerLayer.GetAdornerLayer(target) is not { } layer) return;
        var adorner = new Adorner(target, style);
        AdornerLayer.SetAdornedElement(adorner, target);
        AdornerLayer.SetIsClipEnabled(adorner, false);
        layer.Children.Add(adorner);
        _current = adorner;
        if (durationMs > 0)
        {
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(durationMs), DispatcherPriority.Normal, (_, _) => Hide());
            _timer.Start();
        }
    }

    public void Hide()
    {
        _timer?.Stop();
        _timer = null;
        if (_current is { } a && a.Parent is AdornerLayer layer) layer.Children.Remove(a);
        _current = null;
    }

    /// <summary>The highlight visual. Excluded from tree walks and hit tests.</summary>
    internal sealed class Adorner : Control
    {
        static readonly IBrush ContentFill = new SolidColorBrush(Color.FromArgb(64, 0x3D, 0x7D, 0xF2));
        static readonly IBrush PaddingFill = new SolidColorBrush(Color.FromArgb(64, 0x53, 0xDC, 0xA6));
        static readonly IBrush MarginFill = new SolidColorBrush(Color.FromArgb(64, 0xF3, 0xAD, 0x20));
        static readonly IPen Outline = new Pen(new SolidColorBrush(Color.FromRgb(0x3D, 0x7D, 0xF2)), 1);
        static readonly IBrush LabelBack = new SolidColorBrush(Color.FromArgb(230, 0x1F, 0x1F, 0x1F));

        readonly Visual _target;
        readonly HighlightStyle _style;

        public Adorner(Visual target, HighlightStyle style)
        {
            _target = target;
            _style = style;
            IsHitTestVisible = false;
            ClipToBounds = false;
        }

        public override void Render(DrawingContext ctx)
        {
            var box = new Rect(Bounds.Size);
            if (_style == HighlightStyle.Layout && _target is Layoutable l)
            {
                var m = l.Margin;
                var outer = new Rect(-m.Left, -m.Top, box.Width + m.Left + m.Right, box.Height + m.Top + m.Bottom);
                ctx.DrawRectangle(MarginFill, null, outer);
                var pad = _target switch { TemplatedControl tc => tc.Padding, Decorator d => d.Padding, _ => default };
                ctx.DrawRectangle(PaddingFill, null, box);
                ctx.DrawRectangle(ContentFill, null, box.Deflate(pad));
            }
            else
            {
                ctx.DrawRectangle(ContentFill, null, box);
            }
            ctx.DrawRectangle(null, Outline, box);

            var text = $"{TreeWalker.Compound(_target)}  {box.Width:0.#}×{box.Height:0.#}";
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.White);
            // Label above the element, or below it when there is no room above (element near the window's top edge).
            var top = TopLevel.GetTopLevel(_target) is { } tl ? _target.TranslatePoint(default, tl)?.Y ?? 0 : 0;
            var labelY = top >= ft.Height + 8 ? -ft.Height - 6 : box.Height + 2;
            var label = new Rect(0, labelY, ft.Width + 8, ft.Height + 4);
            ctx.DrawRectangle(LabelBack, null, label, 4, 4);
            ctx.DrawText(ft, new Point(4, labelY + 2));
        }
    }
}
