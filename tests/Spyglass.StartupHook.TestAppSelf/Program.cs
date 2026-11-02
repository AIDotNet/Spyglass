using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Spyglass.Agent;

// A normal desktop-lifetime app (headless so tests need no display) that exits after TESTAPP_SECONDS (default 30).
// This one DOES call UseSpyglass(): launched with the hook it must still yield exactly one session.
AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSpyglass(o => { o.LaunchClient = false; })
    .StartWithClassicDesktopLifetime(args);

sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Window { Width = 300, Height = 200, Content = new Button { Name = "HookedButton", Content = "Hooked" } };
            var seconds = int.TryParse(Environment.GetEnvironmentVariable("TESTAPP_SECONDS"), out var s) ? s : 30;
            DispatcherTimer.RunOnce(() => desktop.Shutdown(), TimeSpan.FromSeconds(seconds));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
