using System.Collections.Concurrent;
using Spyglass.Protocol.Messages;

namespace Spyglass.XamlLanguage;

/// <summary>
/// Avalonia XAML IntelliSense for IDE extensions (xaml/completion, xaml/hover, xaml/documentation, xaml/prepare). Everything
/// comes from the file's project: Avalonia, every component library it references (their [XmlnsDefinition] URIs), and its
/// own build output (controls, x:DataType view models, code-behind handlers). Thread-safe.
/// </summary>
public sealed class XamlLanguageService : IDisposable
{
    readonly XamlProjectCache _cache;
    readonly ConcurrentDictionary<string, (string Stamp, string[] Keys)> _keys = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="resolve">Reference resolution; default runs MSBuild (tests can substitute it).</param>
    public XamlLanguageService(Func<string, CancellationToken, Task<XamlProjectReferences>>? resolve = null) => _cache = new XamlProjectCache(resolve);

    /// <summary>How long a request waits for a project that is still loading before answering "incomplete".</summary>
    public TimeSpan LoadWait { get; set; } = TimeSpan.FromMilliseconds(1500);

    public void Prepare(string path)
    {
        if (XamlProjectResolver.FindProjectFile(path) is { } project) _cache.Prepare(project);
    }

    public async Task<XamlCompletionResult> CompleteAsync(XamlCompletionParams p, CancellationToken ct = default)
    {
        var (lease, message, project) = await AcquireAsync(p.Path, ct).ConfigureAwait(false);
        if (lease is null) return new XamlCompletionResult(p.Offset, [], Incomplete: message is null, Message: message ?? "Loading Avalonia types…");
        using (lease)
            return new XamlCompleter(lease.Types, p.Text, Math.Clamp(p.Offset, 0, p.Text.Length), () => ProjectKeys(project!, p.Path)).Complete();
    }

    public async Task<XamlHoverResult> HoverAsync(XamlHoverParams p, CancellationToken ct = default)
    {
        var (lease, _, project) = await AcquireAsync(p.Path, ct).ConfigureAwait(false);
        if (lease is null) return new XamlHoverResult(null);
        using (lease)
            return new XamlCompleter(lease.Types, p.Text, Math.Clamp(p.Offset, 0, p.Text.Length), () => ProjectKeys(project!, p.Path)).Hover() ?? new XamlHoverResult(null);
    }

    public async Task<XamlDocumentationResult> DocumentationAsync(XamlDocumentationParams p, CancellationToken ct = default)
    {
        var (lease, _, _) = await AcquireAsync(p.Path, ct).ConfigureAwait(false);
        if (lease is null) return new XamlDocumentationResult(null);
        using (lease) return new XamlDocumentationResult(lease.Types.Documentation(p.DocId));
    }

    /// <summary>The project's type system (a lease), or a message for the user, or neither (still loading).</summary>
    async Task<(XamlProjectCache.Lease? Lease, string? Message, string? Project)> AcquireAsync(string path, CancellationToken ct)
    {
        var project = XamlProjectResolver.FindProjectFile(path);
        if (project is null) return (null, "This file is not inside a .NET project.", null);
        try { return (await _cache.AcquireAsync(project, LoadWait, ct).ConfigureAwait(false), null, project); }
        catch (XamlLanguageException ex) { return (null, ex.Message, project); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return (null, $"Cannot read the project's types: {ex.Message}", project); }
    }

    /// <summary>x:Key values of the project's other .axaml files (App.axaml, resource dictionaries), cached by file stamp.</summary>
    IEnumerable<string> ProjectKeys(string projectFile, string current)
    {
        var dir = Path.GetDirectoryName(projectFile)!;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, "*.axaml", SearchOption.AllDirectories)
                .Where(f => !IsBuildOutput(Path.GetRelativePath(dir, f)) && !string.Equals(f, current, StringComparison.OrdinalIgnoreCase))
                .Take(2000).ToList();
        }
        catch { yield break; }
        foreach (var f in files)
        {
            string[] keys;
            try
            {
                var info = new FileInfo(f);
                var stamp = $"{info.LastWriteTimeUtc.Ticks}:{info.Length}";
                if (_keys.TryGetValue(f, out var c) && c.Stamp == stamp) keys = c.Keys;
                else _keys[f] = (stamp, keys = XamlScanner.Keys(File.ReadAllText(f)).ToArray());
            }
            catch { continue; }
            foreach (var k in keys) yield return k;
        }
    }

    static bool IsBuildOutput(string relative)
    {
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first is "bin" or "obj" or "node_modules" || first.StartsWith('.');
    }

    public void Dispose() => _cache.Dispose();
}
