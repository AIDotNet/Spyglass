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

    public static void WhenReady(Action<SpyglassAgentOptions>? configure = null) => WhenReady(configure, advanced: false);

    /// <summary>
    /// Entry point of the startup hook. <paramref name="advanced"/> adds Spyglass.Agent.Advanced (value frames, binding
    /// details) when that assembly is next to this one and Avalonia grants it access to its private APIs; otherwise one
    /// line goes to stderr and the Basic agent runs.
    /// </summary>
    public static void WhenReady(bool advanced) => WhenReady(null, advanced);

    static void WhenReady(Action<SpyglassAgentOptions>? configure, bool advanced)
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
                    Dispatcher.UIThread.Post(() => StartOnce(options, advanced), DispatcherPriority.Background);
                    return;
                }
                Thread.Sleep(50);
            }
        }) { IsBackground = true, Name = "Spyglass attach" }.Start();
    }

    static void StartOnce(SpyglassAgentOptions options, bool advanced)
    {
        // The app references Spyglass.Agent itself (UseSpyglass): that copy lives in the default load context; leave it alone.
        if (AssemblyLoadContext.Default.Assemblies.Any(a => a.GetName().Name == "Spyglass.Agent" && a != typeof(SpyglassAttach).Assembly)) return;
        if (advanced) TryAddAdvanced(options);
        SpyglassAgent.Start(options);
    }

    /// <summary>
    /// Adds the Advanced feature if it can work. It calls Avalonia [PrivateApi] members, which the runtime only lets
    /// through when the app was built with AvaloniaAccessUnstablePrivateApis; JIT-compiling its inspector up front turns a
    /// missing grant (MethodAccessException) into a note instead of an error on the first request.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Startup hook only; the app is not trimmed.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2072:UnrecognizedReflectionPattern", Justification = "Startup hook only; the app is not trimmed.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075:UnrecognizedReflectionPattern", Justification = "Startup hook only; the app is not trimmed.")]
    static void TryAddAdvanced(SpyglassAgentOptions options)
    {
        try
        {
            var context = AssemblyLoadContext.GetLoadContext(typeof(SpyglassAttach).Assembly) ?? AssemblyLoadContext.Default;
            var assembly = context.LoadFromAssemblyName(new System.Reflection.AssemblyName("Spyglass.Agent.Advanced"));
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            foreach (var type in assembly.GetTypes())
                foreach (var method in type.GetMethods(all))
                    if (!method.IsAbstract && !method.ContainsGenericParameters)
                        System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle);
            var feature = assembly.GetType("Spyglass.Agent.Advanced.AdvancedFeature", throwOnError: true)!;
            options.AddFeature((IAgentFeature)Activator.CreateInstance(feature, nonPublic: true)!);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[spyglass] Advanced is not available (" + Describe(ex) + "); running the Basic agent. " +
                "Advanced needs Avalonia 12.1.4 and <AvaloniaAccessUnstablePrivateApis>true</AvaloniaAccessUnstablePrivateApis> in the app.");
        }
    }

    static string Describe(Exception ex) => (ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex).GetType().Name;
}
