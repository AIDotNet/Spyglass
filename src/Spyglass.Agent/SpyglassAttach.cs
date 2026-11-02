using System.Runtime.Loader;
using Avalonia;
using Avalonia.Threading;

namespace Spyglass.Agent;

/// <summary>
/// Starts the agent in an app that does not call <c>UseSpyglass()</c>: used by the startup hook
/// (<c>DOTNET_STARTUP_HOOKS=…/Spyglass.StartupHook.dll</c>), which runs before <c>Main</c>, when there is no
/// <see cref="AppBuilder"/> to extend yet. Waits for the Avalonia application, then starts the agent on the UI thread.
/// </summary>
public static class SpyglassAttach
{
    /// <summary>Gives up when no Avalonia application appears (a console tool, a non-Avalonia process).</summary>
    static readonly TimeSpan GiveUp = TimeSpan.FromMinutes(2);

    public static void WhenReady(Action<SpyglassAgentOptions>? configure = null)
    {
        var options = new SpyglassAgentOptions();
        configure?.Invoke(options);
        if (options.EnableAvaloniaDiagnostics)
            AppContext.SetSwitch("Avalonia.Diagnostics.Diagnostic.IsEnabled", true);   // must precede app start

        new Thread(() =>
        {
            var until = DateTime.UtcNow + GiveUp;
            while (DateTime.UtcNow < until)
            {
                if (Application.Current is not null)
                {
                    // Background priority: runs once the app's own startup is done, inside the running loop.
                    Dispatcher.UIThread.Post(() => StartOnce(options), DispatcherPriority.Background);
                    return;
                }
                Thread.Sleep(50);
            }
        }) { IsBackground = true, Name = "Spyglass attach" }.Start();
    }

    static void StartOnce(SpyglassAgentOptions options)
    {
        // The app references Spyglass.Agent itself (UseSpyglass): that copy lives in the default load context; leave it alone.
        if (AssemblyLoadContext.Default.Assemblies.Any(a => a.GetName().Name == "Spyglass.Agent" && a != typeof(SpyglassAttach).Assembly)) return;
        SpyglassAgent.Start(options);
    }
}
