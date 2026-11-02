using System.Reflection;
using System.Runtime.Loader;

/// <summary>
/// Entry point the .NET host calls before <c>Main</c> when this assembly is listed in DOTNET_STARTUP_HOOKS.
/// The type must be called StartupHook, in the global namespace, with a static Initialize().
/// Set SPYGLASS_ATTACH=0 to turn it off without removing it from the environment.
/// </summary>
internal sealed class StartupHook
{
    public static void Initialize()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("SPYGLASS_ATTACH") == "0") return;
            var dir = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!;
            var agent = new AgentLoadContext(dir).LoadFromAssemblyPath(Path.Combine(dir, "Spyglass.Agent.dll"));
            var attach = agent.GetType("Spyglass.Agent.SpyglassAttach", throwOnError: true)!;
            attach.GetMethod("WhenReady")!.Invoke(null, [null]);
        }
        catch (Exception ex)
        {
            // Never break the app being debugged.
            Console.Error.WriteLine($"[spyglass] could not attach: {(ex is TargetInvocationException { InnerException: { } inner } ? inner : ex).Message}");
        }
    }

    /// <summary>Loads Spyglass's own assemblies from the hook folder; everything else (Avalonia, the framework) comes from the app.</summary>
    sealed class AgentLoadContext(string directory) : AssemblyLoadContext("Spyglass.Agent")
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is null || name.Name.StartsWith("Avalonia", StringComparison.Ordinal)) return null;
            var path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
