using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Spyglass.App.Theme;

/// <summary>
/// "Liquid Glass" brushes (D8). Derived at runtime from Arc tokens (Background, Surface, Foreground) so there are no
/// hard-coded colours and light/dark switches follow the theme. Published as application resources:
/// <c>Spy.WindowTint</c> (over the OS window blur), <c>Spy.GlassPanel</c> (floating cards),
/// <c>Spy.GlassChrome</c> (sidebar / status capsule) and <c>Spy.GlassStroke</c> (specular rim).
/// When the OS gives no window blur, surfaces become (nearly) opaque so text stays readable.
/// </summary>
public static class GlassMaterial
{
    public static void Attach(Window window)
    {
        void Update() => Dispatcher.UIThread.Post(() => Publish(window));
        window.Opened += (_, _) => Update();
        window.ActualThemeVariantChanged += (_, _) => Update();
        window.PropertyChanged += (_, e) => { if (e.Property == TopLevel.ActualTransparencyLevelProperty) Update(); };
        if (ArcThemeManager.Current is { } manager) manager.StateChanged += (_, _) => Update();
        Publish(window);
    }

    static void Publish(Window w)
    {
        if (Application.Current is not { } app) return;
        var dark = ArcThemeManager.Current?.IsDark ?? w.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        var blurred = w.ActualTransparencyLevel != WindowTransparencyLevel.None || Environment.GetEnvironmentVariable("SPYGLASS_SELFSHOT") is not null;

        var background = ArcTokens.GetColor(w, ArcDesignTokenKind.Background);
        var surface = ArcTokens.GetColor(w, ArcDesignTokenKind.Surface);
        var foreground = ArcTokens.GetColor(w, ArcDesignTokenKind.Foreground);

        app.Resources["Spy.WindowTint"] = Solid(background, blurred ? (dark ? 0.50 : 0.42) : 1);
        app.Resources["Spy.GlassPanel"] = Solid(surface, blurred ? (dark ? 0.58 : 0.62) : (dark ? 0.92 : 0.88));
        app.Resources["Spy.GlassChrome"] = Solid(surface, blurred ? (dark ? 0.42 : 0.48) : (dark ? 0.85 : 0.80));
        // Specular rim: bright along the top edge, fading towards the bottom, like light caught by a glass edge.
        var rimTop = dark ? WithAlpha(foreground, 0.22) : WithAlpha(background, 0.95);
        var rimBottom = WithAlpha(foreground, dark ? 0.05 : 0.08);
        app.Resources["Spy.GlassStroke"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(rimTop, 0), new GradientStop(rimBottom, 0.6) },
        };
    }

    static SolidColorBrush Solid(Color c, double alpha) => new(WithAlpha(c, alpha));
    static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Round(255 * alpha), c.R, c.G, c.B);
}
