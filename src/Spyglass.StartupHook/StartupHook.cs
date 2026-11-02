using System.Diagnostics;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

/// <summary>
/// Entry point the .NET host calls before <c>Main</c> when this assembly is listed in DOTNET_STARTUP_HOOKS.
/// The type must be called StartupHook, in the global namespace, with a static Initialize().
/// SPYGLASS_ATTACH=0 turns it off, SPYGLASS_ATTACH=advanced also starts the Advanced features (Avalonia 12.1.4 only).
/// </summary>
internal sealed class StartupHook
{
    public static void Initialize()
    {
        try
        {
            var mode = Environment.GetEnvironmentVariable("SPYGLASS_ATTACH")?.Trim().ToLowerInvariant();
            if (mode == "0") return;
            var advanced = mode == "advanced";

            if (FindAvaloniaVersion() is not { } version) return;   // not an Avalonia app (or single-file): stay out of the way

            string folder;
            if (version.Major == 12) folder = "avalonia12";
            else if (version is { Major: 11, Minor: >= 2 }) folder = "avalonia11";
            else
            {
                Console.Error.WriteLine($"[spyglass] Avalonia {version} is not supported (11.2+ and 12.x are); not attaching.");
                return;
            }
            if (advanced && version != new Version(12, 1, 4))
            {
                Console.Error.WriteLine($"[spyglass] Advanced needs Avalonia 12.1.4 exactly, this app has {version}; running the Basic agent.");
                advanced = false;
            }

            var dir = Path.Combine(Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!, folder);
            var agent = new AgentLoadContext(dir).LoadFromAssemblyPath(Path.Combine(dir, "Spyglass.Agent.dll"));
            var attach = agent.GetType("Spyglass.Agent.SpyglassAttach", throwOnError: true)!;
            attach.GetMethod("WhenReady", [typeof(bool)])!.Invoke(null, [advanced]);
        }
        catch (Exception ex)
        {
            // Never break the app being debugged.
            Console.Error.WriteLine($"[spyglass] could not attach: {(ex is TargetInvocationException { InnerException: { } inner } ? inner : ex).Message}");
        }
    }

    /// <summary>
    /// The app's Avalonia version without loading Avalonia: the file version of Avalonia.Base.dll beside the app, else the
    /// (major-only) assembly version the entry assembly references. Null when the app does not use Avalonia.
    /// </summary>
    static Version? FindAvaloniaVersion()
    {
        var entry = Assembly.GetEntryAssembly()?.Location;
        var dirs = new[] { AppContext.BaseDirectory, entry is { Length: > 0 } ? Path.GetDirectoryName(entry) : null };
        foreach (var dir in dirs.Where(d => d is not null).Distinct())
        {
            var file = Path.Combine(dir!, "Avalonia.Base.dll");
            if (!File.Exists(file)) continue;
            var info = FileVersionInfo.GetVersionInfo(file);
            if (Regex.Match(info.ProductVersion ?? "", @"^\d+\.\d+(\.\d+)?") is { Success: true } m && Version.TryParse(m.Value, out var v)) return v;
            return AssemblyName.GetAssemblyName(file).Version;
        }

        if (entry is { Length: > 0 } && File.Exists(entry))
        {
            using var pe = new PEReader(File.OpenRead(entry));
            if (!pe.HasMetadata) return null;
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            foreach (var handle in md.AssemblyReferences)
            {
                var r = md.GetAssemblyReference(handle);
                if (md.GetString(r.Name) is "Avalonia.Base" or "Avalonia") return r.Version;
            }
        }
        return null;
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
