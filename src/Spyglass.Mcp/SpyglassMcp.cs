using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Spyglass.App.Core;

namespace Spyglass.Mcp;

/// <summary>Wires the MCP server into a <see cref="SpyglassHub"/>.</summary>
public static class SpyglassMcp
{
    public const string Path = "/mcp";

    public const string Instructions = """
        Spyglass inspects running Avalonia UI apps (like browser DevTools). The app must reference Spyglass.Agent
        (`.UseSpyglass()`) and be running. Start with `apps_list`, then `ui_snapshot` to see the UI as an outline.
        Every element in tool output has a reference like [e42]; pass it as `element` to other tools. `element` also
        accepts selectors (`Button#Save`, `Window#Main StackPanel > Button.primary`, `ListBox > ListBoxItem:nth(3)`),
        `text=Save` (visible text) and `xaml=MainView.axaml:23` (Debug builds).
        Use `element_inspect` first when diagnosing an element: it includes the XAML location, non-default properties
        and binding errors. Tools never modify source files; edit the XAML/C# yourself, then inspect again. When the app
        uses Spyglass.HotReload, saved .axaml changes apply to the running app without a restart: call
        `hot_reload_status` to confirm, or to read the XAML compile error.
        """;

    public static void AddServices(IServiceCollection services) =>
        services.AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "spyglass", Version = typeof(SpyglassMcp).Assembly.GetName().Version?.ToString(3) ?? "0.1.0" };
                o.ServerInstructions = Instructions;
            })
            .WithHttpTransport()
            .WithTools<SpyglassTools>()
            .WithTools<SpyglassNetworkTools>()
            .WithTools<SpyglassLogTools>()
            .WithTools<SpyglassEventTools>()
            .WithTools<SpyglassResourceTools>()
            .WithTools<SpyglassPerfTools>()
            .WithTools<SpyglassActionTools>()
            .WithTools<SpyglassHotReloadTools>();

    public static void MapEndpoint(WebApplication app) => app.MapMcp(Path);

    /// <summary>Convenience: hub options with MCP mounted.</summary>
    public static SpyglassHubOptions WithMcp(this SpyglassHubOptions options)
    {
        var services = options.ConfigureServices;
        var configure = options.ConfigureApp;
        options.ConfigureServices = s => { services?.Invoke(s); AddServices(s); };
        options.ConfigureApp = a => { configure?.Invoke(a); MapEndpoint(a); };
        return options;
    }
}
