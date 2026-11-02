using Avalonia;
using Spyglass.Agent.Advanced;

namespace Spyglass.Sample;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
#if DEBUG
            .UseSpyglassAdvanced(o => o.ApplicationName = "Spyglass Sample")
#endif
            ;
}
