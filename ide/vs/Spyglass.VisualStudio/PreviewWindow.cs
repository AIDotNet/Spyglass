using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Threading;
using Avalonia.Remote.Protocol.Input;
using Spyglass.VisualStudio.Core.Preview;
using AvaloniaKey = Avalonia.Remote.Protocol.Input.Key;
using AvaloniaMouseButton = Avalonia.Remote.Protocol.Input.MouseButton;
using WpfMouseButton = System.Windows.Input.MouseButton;

namespace Spyglass.VisualStudio;

/// <summary>
/// The Spyglass Preview window: plain WPF, built in code, living on its own STA thread in the extension host process
/// (VisualStudio.Extensibility's Remote UI cannot show a dynamic bitmap, nor report the pane size or pointer positions).
/// It shows the newest frame, scaled (fit / percent), forwards mouse, wheel, keyboard and text input to the previewer,
/// tells the controller the pane size and DPI, and offers theme, zoom, refresh, build and "go to error".
/// </summary>
internal sealed class PreviewWindow : Window
{
    readonly PreviewController _controller;
    readonly Action<string, int?, int?> _reveal;
    readonly Action<PreviewZoom, PreviewTheme> _persist;

    readonly Ellipse _dot = new() { Width = 9, Height = 9, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly ProgressBar _busy = new() { IsIndeterminate = true, Width = 60, Height = 6, Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    readonly ComboBox _zoomBox = new() { Width = 78, ToolTip = "Zoom" };
    readonly ComboBox _themeBox = new() { Width = 78, ToolTip = "Theme variant (Window roots only)", Margin = new Thickness(6, 0, 0, 0) };
    readonly Button _refresh = new() { Content = "Refresh", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0) };
    readonly Button _build = new() { Content = "Build project", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed };
    readonly Image _image = new() { Stretch = Stretch.Fill, Focusable = true, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, SnapsToDevicePixels = true, UseLayoutRounding = true };
    readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    readonly Border _errorBar = new() { Padding = new Thickness(8, 6, 8, 6), Background = new SolidColorBrush(Color.FromArgb(0x33, 0xE5, 0x53, 0x4B)), Visibility = Visibility.Collapsed, Cursor = Cursors.Hand };
    readonly TextBlock _errorText = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), MaxHeight = 120 };

    WriteableBitmap? _bitmap;
    PreviewFrame? _pendingFrame;
    bool _frameScheduled;
    PreviewZoom _zoom;
    double _logicalW, _logicalH;   // the picture's size in DIPs
    PreviewStatus? _lastStatus;
    bool _suppressBoxEvents;

    public PreviewWindow(PreviewController controller, PreviewZoom zoom, PreviewTheme theme, Action<string, int?, int?> reveal, Action<PreviewZoom, PreviewTheme> persist)
    {
        _controller = controller;
        _reveal = reveal;
        _persist = persist;
        _zoom = zoom;

        Title = "Spyglass Preview";
        Width = 560; Height = 640;
        MinWidth = 280; MinHeight = 200;
        ShowInTaskbar = false;
        Background = SystemColors.WindowBrush;
        Foreground = SystemColors.WindowTextBrush;
        UseLayoutRounding = true;

        foreach (var item in new[] { "Fit", "25%", "50%", "75%", "100%", "125%", "150%", "200%", "300%", "400%" }) _zoomBox.Items.Add(item);
        foreach (var item in new[] { "Default", "Light", "Dark" }) _themeBox.Items.Add(item);
        _themeBox.SelectedIndex = (int)theme;

        var zoomOut = new Button { Content = "−", Width = 24, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Zoom out" };
        var zoomIn = new Button { Content = "+", Width = 24, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Zoom in" };
        zoomOut.Click += (_, _) => SetZoom(PreviewZoom.Out(CurrentScale()));
        zoomIn.Click += (_, _) => SetZoom(PreviewZoom.In(CurrentScale()));
        _zoomBox.SelectionChanged += (_, _) =>
        {
            if (_suppressBoxEvents || _zoomBox.SelectedItem is not string s) return;
            SetZoom(PreviewZoom.Parse(s));
        };
        _themeBox.SelectionChanged += (_, _) =>
        {
            if (_suppressBoxEvents || _themeBox.SelectedIndex < 0) return;
            _controller.Theme = (PreviewTheme)_themeBox.SelectedIndex;
            _persist(_zoom, _controller.Theme);
        };
        _refresh.Click += (_, _) => _controller.Refresh();
        _build.Click += (_, _) => _ = _controller.BuildAsync();

        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        tools.Children.Add(_zoomBox); tools.Children.Add(zoomOut); tools.Children.Add(zoomIn);
        tools.Children.Add(_themeBox); tools.Children.Add(_build); tools.Children.Add(_refresh);

        var statusLine = new StackPanel { Orientation = Orientation.Horizontal };
        statusLine.Children.Add(_dot); statusLine.Children.Add(_status); statusLine.Children.Add(_busy);

        var bar = new DockPanel { Margin = new Thickness(8, 6, 8, 6), LastChildFill = true };
        DockPanel.SetDock(tools, Dock.Right);
        bar.Children.Add(tools);
        bar.Children.Add(statusLine);
        var barBorder = new Border { Child = bar, Background = SystemColors.ControlBrush, BorderBrush = SystemColors.ControlDarkBrush, BorderThickness = new Thickness(0, 0, 0, 1) };

        _errorBar.Child = _errorText;
        _errorBar.ToolTip = "Click to go to the error in the editor";
        _errorBar.MouseLeftButtonUp += (_, _) =>
        {
            var path = _controller.ActivePath;
            if (path is not null && _lastStatus?.XamlError is not null) _reveal(path, _lastStatus.ErrorLine, _lastStatus.ErrorColumn);
        };

        var surface = new Grid { Margin = new Thickness(12), Background = Brushes.Transparent };
        surface.Children.Add(_image);
        _scroll.Content = surface;
        _scroll.Background = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)) { Opacity = 0.08 };

        var root = new DockPanel();
        DockPanel.SetDock(barBorder, Dock.Top);
        DockPanel.SetDock(_errorBar, Dock.Bottom);
        root.Children.Add(barBorder);
        root.Children.Add(_errorBar);
        root.Children.Add(_scroll);
        Content = root;

        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        WireInput();
        SizeChanged += (_, _) => PaneChanged();
        _scroll.SizeChanged += (_, _) => PaneChanged();
        Loaded += (_, _) => PaneChanged();
        SelectZoomItem();
        SetStatus(_controller.Status);
    }

    // ---------- the picture ----------

    /// <summary>Called from any thread with each frame; only the newest one is drawn, at most once per render pass.</summary>
    public void SubmitFrame(PreviewFrame frame)
    {
        lock (this)
        {
            _pendingFrame = frame;
            if (_frameScheduled) return;
            _frameScheduled = true;
        }
        PreviewWindowHost.PostRender(Dispatcher, DrawPending);
    }

    void DrawPending()
    {
        PreviewFrame? f;
        lock (this) { f = _pendingFrame; _pendingFrame = null; _frameScheduled = false; }
        if (f is null || f.Width <= 0 || f.Height <= 0) return;

        var dpiX = f.DpiX > 0 ? f.DpiX : 96;
        var dpiY = f.DpiY > 0 ? f.DpiY : 96;
        if (_bitmap is null || _bitmap.PixelWidth != f.Width || _bitmap.PixelHeight != f.Height
            || Math.Abs(_bitmap.DpiX - dpiX) > 0.01 || Math.Abs(_bitmap.DpiY - dpiY) > 0.01)
        {
            _bitmap = new WriteableBitmap(f.Width, f.Height, dpiX, dpiY, PixelFormats.Pbgra32, null);
            _image.Source = _bitmap;
        }

        var data = f.Data;
        var stride = f.Stride;
        if (!f.IsBgra)   // RGBA → BGRA (the previewer is asked for BGRA first, so this is the unusual case)
        {
            data = (byte[])data.Clone();
            for (var y = 0; y < f.Height; y++)
                for (var x = 0; x < f.Width; x++)
                {
                    var i = y * stride + x * 4;
                    (data[i], data[i + 2]) = (data[i + 2], data[i]);
                }
        }
        if (data.Length >= stride * f.Height)
            _bitmap.WritePixels(new Int32Rect(0, 0, f.Width, f.Height), data, stride, 0);

        _logicalW = f.Width * 96.0 / dpiX;
        _logicalH = f.Height * 96.0 / dpiY;
        ApplyScale();
    }

    double PaneWidth => Math.Max(1, _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : _scroll.ActualWidth) - 24;
    double PaneHeight => Math.Max(1, _scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : _scroll.ActualHeight) - 24;

    double CurrentScale() => _zoom.Scale(_logicalW, _logicalH, PaneWidth, PaneHeight);

    void ApplyScale()
    {
        var scale = CurrentScale();
        _image.Width = Math.Max(1, _logicalW * scale);
        _image.Height = Math.Max(1, _logicalH * scale);
        RenderOptions.SetBitmapScalingMode(_image, scale >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    // ---------- zoom, pane size, DPI ----------

    void SetZoom(PreviewZoom zoom)
    {
        _zoom = zoom;
        SelectZoomItem();
        ApplyScale();
        PaneChanged();
        _persist(_zoom, _controller.Theme);
    }

    void SelectZoomItem()
    {
        _suppressBoxEvents = true;
        var text = _zoom.Fit ? "Fit" : $"{_zoom.Percent:0.##}%";
        _zoomBox.SelectedItem = _zoomBox.Items.Contains(text) ? text : null;
        _zoomBox.Text = text;
        _suppressBoxEvents = false;
    }

    /// <summary>The pane (or the DPI, or the zoom) changed: tell the previewer (the controller debounces by 100 ms).</summary>
    void PaneChanged()
    {
        ApplyScale();
        if (!IsLoaded) return;
        var (w, h) = _zoom.Viewport(PaneWidth, PaneHeight);
        _controller.SetViewport(w, h, PreviewInput.Dpi(VisualTreeHelper.GetDpi(this).DpiScaleX), PreviewInput.Dpi(VisualTreeHelper.GetDpi(this).DpiScaleY));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        PaneChanged();
    }

    // ---------- status ----------

    public void SetStatus(PreviewStatus s)
    {
        _lastStatus = s;
        _status.Text = s.Message;
        _dot.Fill = s.Kind switch
        {
            PreviewStatusKind.Running => s.XamlError is null ? Brushes.SeaGreen : Brushes.Goldenrod,
            PreviewStatusKind.Starting or PreviewStatusKind.Updating => Brushes.Goldenrod,
            PreviewStatusKind.Error or PreviewStatusKind.Exited => Brushes.IndianRed,
            _ => Brushes.Gray,
        };
        _busy.Visibility = s.Kind is PreviewStatusKind.Starting or PreviewStatusKind.Updating ? Visibility.Visible : Visibility.Collapsed;
        _build.Visibility = s.CanBuild ? Visibility.Visible : Visibility.Collapsed;
        _refresh.Content = s.Kind == PreviewStatusKind.Exited ? "Restart" : "Refresh";
        _errorBar.Visibility = s.XamlError is null ? Visibility.Collapsed : Visibility.Visible;
        _errorText.Text = s.XamlError is null ? "" : (s.ErrorLine is { } l ? $"({l}{(s.ErrorColumn is { } c ? "," + c : "")}) " : "") + s.XamlError;
        Title = _controller.ActivePath is { } p ? $"Spyglass Preview — {Path.GetFileName(p)}" : "Spyglass Preview";
    }

    public void SetThemeBox(PreviewTheme theme)
    {
        _suppressBoxEvents = true;
        _themeBox.SelectedIndex = (int)theme;
        _suppressBoxEvents = false;
    }

    // ---------- input → previewer ----------

    void WireInput()
    {
        _image.MouseMove += (_, e) =>
        {
            var (x, y) = Logical(e);
            _ = _controller.PointerMovedAsync(x, y, Modifiers(e));
        };
        _image.MouseDown += (_, e) =>
        {
            if (Map(e.ChangedButton) is not { } b) return;
            _image.Focus();
            Keyboard.Focus(_image);
            _image.CaptureMouse();
            var (x, y) = Logical(e);
            _ = _controller.PointerPressedAsync(x, y, b, Modifiers(e));
            e.Handled = true;
        };
        _image.MouseUp += (_, e) =>
        {
            if (Map(e.ChangedButton) is not { } b) return;
            if (e.ChangedButton == WpfMouseButton.Left || !Mouse.LeftButton.HasFlag(MouseButtonState.Pressed)) _image.ReleaseMouseCapture();
            var (x, y) = Logical(e);
            _ = _controller.PointerReleasedAsync(x, y, b, Modifiers(e));
            e.Handled = true;
        };
        _image.MouseWheel += (_, e) =>
        {
            var (x, y) = Logical(e);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                // Ctrl+wheel zooms the picture, like the VS editor.
                SetZoom(e.Delta > 0 ? PreviewZoom.In(CurrentScale()) : PreviewZoom.Out(CurrentScale()));
            }
            else _ = _controller.ScrollAsync(x, y, 0, PreviewInput.WheelNotches(e.Delta), Modifiers(e));
            e.Handled = true;
        };
        _image.PreviewKeyDown += (_, e) => Key(e, true);
        _image.PreviewKeyUp += (_, e) => Key(e, false);
        _image.TextInput += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Text) && !char.IsControl(e.Text[0]))
                _ = _controller.TextInputAsync(e.Text, Modifiers(null));
            e.Handled = true;
        };
    }

    void Key(KeyEventArgs e, bool down)
    {
        var wpfKey = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (PreviewInput.MapKey(wpfKey.ToString()) is { } key)
            _ = _controller.KeyAsync(down, key, default, null, Modifiers(null));
        e.Handled = true;   // keys typed into the preview belong to the previewed UI (Tab, arrows, Enter…), not to this window
    }

    (double X, double Y) Logical(MouseEventArgs e)
    {
        var p = e.GetPosition(_image);
        return PreviewInput.ToLogical(p.X, p.Y, CurrentScale());
    }

    static AvaloniaMouseButton? Map(WpfMouseButton b) => b switch
    {
        WpfMouseButton.Left => AvaloniaMouseButton.Left,
        WpfMouseButton.Right => AvaloniaMouseButton.Right,
        WpfMouseButton.Middle => AvaloniaMouseButton.Middle,
        _ => null,
    };

    static InputModifiers[]? Modifiers(MouseEventArgs? e)
    {
        var k = Keyboard.Modifiers;
        return PreviewInput.Modifiers(k.HasFlag(ModifierKeys.Control), k.HasFlag(ModifierKeys.Shift), k.HasFlag(ModifierKeys.Alt),
            e?.LeftButton == MouseButtonState.Pressed, e?.RightButton == MouseButtonState.Pressed, e?.MiddleButton == MouseButtonState.Pressed);
    }
}
