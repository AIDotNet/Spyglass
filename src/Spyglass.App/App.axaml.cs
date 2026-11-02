using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Spyglass.App.Core;
using Spyglass.App.Core.Settings;
using Spyglass.App.Theme;
using Spyglass.App.ViewModels;
using Spyglass.App.Views;
using Spyglass.Mcp;
using Spyglass.Protocol;

namespace Spyglass.App;

public partial class App : Application
{
    SpyglassHub? _hub;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            SettingsApplier.Attach(SettingsService.Shared);
            _hub = new SpyglassHub(new SpyglassHubOptions { Port = SettingsService.Shared.EffectivePort, Lan = new Core.Lan.LanGate(SettingsService.Shared.Current.AllowLan) }.WithMcp());
            var vm = new MainViewModel(_hub);
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;
            vm.BringToFrontRequested += () => BringToFront(window);
#if DEBUG
            DebugSelfShot.Schedule(desktop.MainWindow, vm);
#endif
            _ = StartHubAsync(vm);
            desktop.ShutdownRequested += async (_, _) => await _hub.DisposeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    static void BringToFront(Avalonia.Controls.Window w)
    {
        if (w.WindowState == Avalonia.Controls.WindowState.Minimized) w.WindowState = Avalonia.Controls.WindowState.Normal;
        w.Show();
        w.Activate();
        // Some window managers ignore Activate() from a background app; a Topmost pulse raises the window anyway.
        w.Topmost = true;
        w.Topmost = false;
    }

    async Task StartHubAsync(MainViewModel vm)
    {
        try
        {
            await _hub!.StartAsync();
            Avalonia.Threading.Dispatcher.UIThread.Post(vm.HubStarted);
        }
        catch (Exception ex)
        {
            // Typically: port 29500 already used by another Spyglass (or `spyglass mcp`) instance.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                vm.HubError = $"Cannot listen on 127.0.0.1:{SettingsService.Shared.EffectivePort}: {ex.GetBaseException().Message}. Is another Spyglass running?");
        }
    }
}
