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
        if (!OperatingSystem.IsMacOS()) PaletteButton.Parent!.SetValue(MarginProperty, new Thickness(0, 0, 140, 0));   // Windows/Linux caption buttons
        InitPalette();
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

    PaletteController? _palette;

    /// <summary>Command palette: entries are rebuilt when it opens; its gesture and the navigation shortcuts follow Settings.</summary>
    void InitPalette()
    {
        Palette.ItemSelected += (_, e) => { if (e.Item.Id is { Length: > 0 } id) _palette?.Run(id); };
        Palette.PropertyChanged += (_, e) =>
        {
            if (e.Property == TKCode.Arc.CommandPaletteDialog.IsOpenProperty && Palette.IsOpen && _palette is not null) Palette.ItemsSource = BuildPaletteItems();
        };
        void ApplyGesture()
        {
            var g = ShortcutBridge.ToKeyGesture(ShortcutBridge.Current.Get(Spyglass.App.Core.Settings.ShortcutRegistry.PaletteOpen));
            Palette.IsShortcutEnabled = g is not null;
            Palette.OpenGesture = g;
            var hint = ShortcutBridge.Current.Get(Spyglass.App.Core.Settings.ShortcutRegistry.PaletteOpen)?.ToDisplay(OperatingSystem.IsMacOS()) ?? "";
            Palette.ShortcutHint = hint;
            ToolTip.SetTip(PaletteButton, hint.Length > 0 ? Spyglass.App.Localization.Loc.F("Palette.Open", hint) : Spyglass.App.Localization.Loc.T("Palette.Label"));
        }
        ApplyGesture();
        Spyglass.App.Core.Settings.SettingsService.Shared.Changed += _ => ApplyGesture();
        Spyglass.App.Localization.Loc.Changed += ApplyGesture;
        AddHandler(KeyDownEvent, OnNavShortcut, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    List<CommandPaletteItem> BuildPaletteItems() => _palette!.Build()
        .Select(p => new CommandPaletteItem { Id = p.Id, Group = p.Group, Label = p.Label, Description = p.Description, Shortcut = p.Shortcut ?? "", Keywords = p.Keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries) })
        .ToList();

    /// <summary>Mod+1…9 go to a page (configurable in Settings).</summary>
    void OnNavShortcut(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DataContext is not MainViewModel vm || ShortcutBridge.FromEvent(e) is not { } g) return;
        if (ShortcutBridge.Current.Find(g) is not { Id: var id } || !id.StartsWith("nav.", StringComparison.Ordinal)) return;
        _palette?.Run("nav:" + id["nav.".Length..]);
        e.Handled = true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm)
        {
            _palette = new PaletteController(vm, Spyglass.App.Core.Settings.SettingsService.Shared);
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
