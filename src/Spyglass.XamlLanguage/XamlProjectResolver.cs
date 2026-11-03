using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Spyglass.XamlLanguage;

/// <summary>What a .axaml file can use: its project's resolved references and the project's own build output.</summary>
/// <param name="TargetPath">The project's assembly (own controls, view models for x:DataType); may not exist before a build.</param>
public sealed record XamlProjectReferences(string ProjectFile, string? TargetPath, IReadOnlyList<string> References);

/// <summary>
/// Finds the .csproj of a XAML file and asks MSBuild for its references: <c>ResolveReferences</c> with
/// <c>BuildProjectReferences=false</c> lists framework reference assemblies, every NuGet package (Avalonia and component
/// libraries) and referenced projects' outputs, without building anything. Works for apps and class libraries.
/// </summary>
public static class XamlProjectResolver
{
    /// <summary>Nearest .csproj in the file's directory or an ancestor.</summary>
    public static string? FindProjectFile(string xamlPath)
    {
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(xamlPath)); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            try
            {
                var hit = Directory.EnumerateFiles(dir, "*.csproj").Order(StringComparer.Ordinal).FirstOrDefault();
                if (hit is not null) return hit;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    public static async Task<XamlProjectReferences> ResolveAsync(string projectFile, string dotnet = "dotnet", CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(projectFile)!,
        };
        foreach (var a in new[] { "msbuild", projectFile, "-nologo", "-target:ResolveReferences", "-p:BuildProjectReferences=false",
                     "-getItem:ReferencePath", "-getProperty:TargetPath" })
            psi.ArgumentList.Add(a);
        psi.Environment["MSBUILDNOINPROCNODE"] = "1";   // keep the hub free of long-lived MSBuild nodes
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Cannot start dotnet.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { } throw; }
        return Parse(projectFile, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    internal static XamlProjectReferences Parse(string projectFile, string stdout, string stderr = "")
    {
        var start = stdout.IndexOf('{');
        if (start < 0)
        {
            var why = (stderr + "\n" + stdout).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
                      ?? "no output";
            throw new XamlLanguageException($"MSBuild could not resolve the references of {Path.GetFileName(projectFile)} ({why}). Restore the project (dotnet restore).");
        }
        using var doc = JsonDocument.Parse(stdout[start..]);
        var root = doc.RootElement;
        string? target = root.TryGetProperty("Properties", out var props) && props.TryGetProperty("TargetPath", out var tp) ? tp.GetString() : null;
        var refs = new List<string>();
        if (root.TryGetProperty("Items", out var items) && items.TryGetProperty("ReferencePath", out var rp))
            foreach (var r in rp.EnumerateArray())
                if (r.TryGetProperty("FullPath", out var fp) && fp.GetString() is { Length: > 0 } f) refs.Add(f);
                else if (r.TryGetProperty("Identity", out var id) && id.GetString() is { Length: > 0 } i) refs.Add(i);
        return new XamlProjectReferences(projectFile, string.IsNullOrWhiteSpace(target) ? null : target, refs);
    }
}

/// <summary>A problem the user can fix (shown in the IDE), not a bug.</summary>
public sealed class XamlLanguageException(string message) : Exception(message);

/// <summary>
/// Per project: the references (re-resolved when the project file or obj/project.assets.json changes) and the type system
/// built from them (rebuilt when the project's own assembly is rebuilt). Loads in the background; callers that cannot wait
/// get "loading" and ask again.
/// </summary>
internal sealed class XamlProjectCache : IDisposable
{
    readonly ConcurrentDictionary<string, Entry> _projects = new(StringComparer.OrdinalIgnoreCase);
    readonly Func<string, CancellationToken, Task<XamlProjectReferences>> _resolve;

    public XamlProjectCache(Func<string, CancellationToken, Task<XamlProjectReferences>>? resolve = null) =>
        _resolve = resolve ?? ((p, ct) => XamlProjectResolver.ResolveAsync(p, ct: ct));

    sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);   // the type system (MetadataLoadContext) is used by one request at a time
        public Task<XamlProjectReferences>? References;
        public string ReferencesStamp = "";
        public Task<XamlTypeSystem>? Types;
        public string TypesStamp = "";
    }

    /// <summary>
    /// The project's type system, or null when it is still loading after <paramref name="wait"/>. The returned lease holds
    /// the project's gate; dispose it when done.
    /// </summary>
    public async Task<Lease?> AcquireAsync(string projectFile, TimeSpan wait, CancellationToken ct)
    {
        var e = _projects.GetOrAdd(projectFile, _ => new Entry());
        Task<XamlTypeSystem> types;
        lock (e)
        {
            var refStamp = Stamp(projectFile) + Stamp(Path.Combine(Path.GetDirectoryName(projectFile)!, "obj", "project.assets.json"));
            if (e.References is null || refStamp != e.ReferencesStamp || e.References.IsFaulted)
            {
                e.References = _resolve(projectFile, CancellationToken.None);
                e.ReferencesStamp = refStamp;
                e.Types = null;
            }
            var refs = e.References;
            // The project's own assembly: known once references are in; until then, the stamp is the references task.
            var typesStamp = refs.IsCompletedSuccessfully ? Stamp(refs.Result.TargetPath) : "pending";
            if (e.Types is null || (refs.IsCompletedSuccessfully && typesStamp != e.TypesStamp) || e.Types.IsFaulted)
            {
                var previous = e.Types;
                e.Types = refs.ContinueWith(t => XamlTypeSystem.Load(t.Result.References, t.Result.TargetPath),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
                e.TypesStamp = typesStamp;
                if (previous is { IsCompletedSuccessfully: true }) _ = DisposeLaterAsync(e, previous.Result);
            }
            types = e.Types;
        }

        // References failed: report it (ContinueWith above was cancelled, so look at the references task).
        var refsTask = e.References!;
        var done = await Task.WhenAny(types, Task.Delay(wait, ct)).ConfigureAwait(false);
        if (done != types)
        {
            if (refsTask.IsFaulted) refsTask.GetAwaiter().GetResult();
            return null;
        }
        if (refsTask.IsFaulted) refsTask.GetAwaiter().GetResult();
        var ts = await types.ConfigureAwait(false);
        await e.Gate.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(ts, e.Gate);
    }

    /// <summary>Starts loading without waiting (an .axaml was opened).</summary>
    public void Prepare(string projectFile) => _ = AcquireAsync(projectFile, TimeSpan.Zero, CancellationToken.None)
        .ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result?.Dispose(); }, TaskScheduler.Default);

    static async Task DisposeLaterAsync(Entry e, XamlTypeSystem old)
    {
        await e.Gate.WaitAsync().ConfigureAwait(false);   // nobody is using it any more
        try { old.Dispose(); } finally { e.Gate.Release(); }
    }

    static string Stamp(string? path)
    {
        if (path is null) return "-";
        try { var i = new FileInfo(path); return i.Exists ? $"{i.LastWriteTimeUtc.Ticks}:{i.Length};" : "-;"; }
        catch { return "?;"; }
    }

    public sealed class Lease(XamlTypeSystem types, SemaphoreSlim gate) : IDisposable
    {
        public XamlTypeSystem Types { get; } = types;
        int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release(); }
    }

    public void Dispose()
    {
        foreach (var e in _projects.Values)
            if (e.Types is { IsCompletedSuccessfully: true } t) t.Result.Dispose();
        _projects.Clear();
    }
}
