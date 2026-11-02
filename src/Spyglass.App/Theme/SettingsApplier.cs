using Spyglass.App.Core.Settings;
using TKCode.Arc;

namespace Spyglass.App.Theme;

/// <summary>Applies settings that have a live effect on the whole client (theme, accent, motion).</summary>
public static class SettingsApplier
{
    static (AppearanceMode, string, MotionMode)? _applied;

    public static ArcAppearance Map(AppearanceMode m) => m switch
    {
        AppearanceMode.Light => ArcAppearance.Light,
        AppearanceMode.Dark => ArcAppearance.Dark,
        _ => ArcAppearance.System,
    };

    /// <summary>Subscribes once; theme changes happen only when appearance, accent or motion actually changed.</summary>
    public static void Attach(SettingsService service)
    {
        _applied = (service.Current.Appearance, service.Current.Accent, service.Current.Motion);
        service.Changed += s =>
        {
            var now = (s.Appearance, s.Accent, s.Motion);
            if (now == _applied) return;
            _applied = now;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ArcTheme.Apply(t => SpyglassTheme.Glass(t.Appearance(Map(s.Appearance)), s)));
        };
    }
}
