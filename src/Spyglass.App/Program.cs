using Avalonia;
using Spyglass.App.Theme;
using TKCode.Arc;
using TKCode.Arc.Fonts;
using TKCode.Arc.Themes.Corners;

namespace Spyglass.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // `spyglass mcp ...`: the MCP server without the GUI (stdio / HTTP, for coding agents, CI, remote machines).
        if (args is ["mcp", ..])
            return Spyglass.Mcp.McpCli.RunAsync(args[1..]).GetAwaiter().GetResult();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .WithArcFonts()
            .UseArc(arc => arc
                .UseArcControls()
                .UseArcBlocks()      // Sidebar + CommandPaletteDialog only (D4)
                .UseArcCorners()
                .WithTheme(t =>
                {
                    var s = Spyglass.App.Core.Settings.SettingsService.Shared.Current;
                    SpyglassTheme.Glass(t.Appearance(SettingsApplier.Map(s.Appearance)), s);
                }));
}
