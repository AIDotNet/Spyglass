using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace Spyglass.VisualStudio;

/// <summary>
/// Visual Studio's .NET 8 extension host has WPF (PresentationFramework, PresentationCore, WindowsBase…) but not all of the
/// Microsoft.WindowsDesktop.App assemblies WPF itself depends on, e.g. System.Windows.Extensions; opening the preview
/// window then failed with FileNotFoundException. The extension ships them (Spyglass.VisualStudio.csproj,
/// WpfDependencyPackage) and this loads them from the extension's folder whenever the host cannot find them itself.
/// </summary>
internal static class HostAssemblies
{
    static readonly string Directory = Path.GetDirectoryName(typeof(HostAssemblies).Assembly.Location) ?? AppContext.BaseDirectory;
    static readonly ConcurrentDictionary<string, Assembly?> Loaded = new(StringComparer.OrdinalIgnoreCase);
    static int _installed;

    /// <summary>Installs the fallback (once). Call before any WPF type is used.</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;
        // AppDomain.AssemblyResolve is the last chance for every load context, including the default one that WPF lives in.
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) => Resolve(new AssemblyName(args.Name));
    }

    static Assembly? Resolve(AssemblyName name)
    {
        if (string.IsNullOrEmpty(name.Name)) return null;
        return Loaded.GetOrAdd(name.Name, n =>
        {
            var path = Path.Combine(Directory, n + ".dll");
            if (!File.Exists(path)) return null;
            try
            {
                // One copy, in this extension's load context, whoever asks for it.
                var context = AssemblyLoadContext.GetLoadContext(typeof(HostAssemblies).Assembly) ?? AssemblyLoadContext.Default;
                return context.LoadFromAssemblyPath(path);
            }
            catch { return null; }
        });
    }
}
