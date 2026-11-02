using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Spyglass.App.Theme;
using Spyglass.App.ViewModels;
using TKCode.Arc;

namespace Spyglass.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        GlassMaterial.Attach(this);
        GlassDividers.HideEdge(Nav);
        GlassDividers.Ghost(ThemeSwitch);
        if (!OperatingSystem.IsMacOS()) ThemeSwitch.Margin = new Thickness(0, 0, 140, 0);   // Windows/Linux caption buttons
        TitleBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        TitleBar.DoubleTapped += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        // The switcher toggles light/dark; the choice is remembered (Settings › Appearance shows it).
        ThemeSwitch.ThemeChangeRequested += (_, e) =>
            Spyglass.App.Core.Settings.SettingsService.Shared.Update(s =>
                s.Appearance = e.Next == ThemeSwitcherAppearance.Dark ? Spyglass.App.Core.Settings.AppearanceMode.Dark : Spyglass.App.Core.Settings.AppearanceMode.Light);

#if DEBUG
        if (Environment.GetEnvironmentVariable("SPYGLASS_SELFSHOT") is not null)
        {
            SelfShotBackdrop.IsVisible = true;
            SelfShotBackdrop.Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#3B82F6"), 0), new GradientStop(Color.Parse("#A855F7"), 0.45),
                    new GradientStop(Color.Parse("#F97316"), 0.8), new GradientStop(Color.Parse("#14B8A6"), 1),
                },
            };
        }
#endif
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm)
        {
            vm.PropertyChanged += OnVmChanged;
            UpdateBadge(vm);
        }
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.BindingsBadge) or nameof(MainViewModel.LogsBadge) && sender is MainViewModel vm) UpdateBadge(vm);
    }

    /// <summary>SidebarItem is not a control (no DataContext), so its badge is set from code.</summary>
    void UpdateBadge(MainViewModel vm)
    {
        foreach (var section in Nav.Sections)
            foreach (var item in section.Items)
                if (item.Key == "bindings") item.Badge = vm.BindingsBadge;
                else if (item.Key == "logs") item.Badge = vm.LogsBadge;
    }
}
