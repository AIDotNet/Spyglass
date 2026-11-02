using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Spyglass.Sample;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel() };
            var backend = new SampleBackend();   // demo HTTP traffic for the Network panel
            desktop.Exit += (_, _) => backend.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

public sealed class MainViewModel
{
    public string Title => "Spyglass Sample";
    public UserViewModel User { get; } = new();
}

public sealed class UserViewModel
{
    public string FullName => "Ada Lovelace";
}
