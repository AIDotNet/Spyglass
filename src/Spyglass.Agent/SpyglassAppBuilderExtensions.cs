using Avalonia;

namespace Spyglass.Agent;

public static class SpyglassAppBuilderExtensions
{
    /// <summary>
    /// Attaches the Spyglass agent. Recommended inside <c>#if DEBUG</c>.
    /// The agent connects out to the Spyglass client (design/protocol §1) after the app has started.
    /// </summary>
    public static AppBuilder UseSpyglass(this AppBuilder builder, Action<SpyglassAgentOptions>? configure = null)
    {
        var options = new SpyglassAgentOptions();
        configure?.Invoke(options);

        if (options.EnableAvaloniaDiagnostics)
            AppContext.SetSwitch("Avalonia.Diagnostics.Diagnostic.IsEnabled", true); // must precede app start (spike S1, M2/M3)

        return builder.AfterSetup(_ => SpyglassAgent.Start(options));
    }
}
