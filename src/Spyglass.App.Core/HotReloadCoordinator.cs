using System.Collections.Concurrent;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.App.Core;

/// <summary>One reload as the hub saw it: which file, which app, what came back.</summary>
public sealed record HotReloadEvent(DateTimeOffset Time, string Path, string App, string SessionShortId, HotReloadResult Result);

/// <summary>
/// Hub side of hot reload (design/hot-reload/README.md §2, §6): knows which source file belongs to which connected app
/// (from each agent's manifest), watches those files, and pushes changed text to every app that compiled the file.
/// Editors with a Spyglass extension push unsaved text through <see cref="ApplyTextAsync"/> instead (HR2).
/// Thread-safe; events are raised on background threads.
/// </summary>
public sealed class HotReloadCoordinator : IDisposable
{
    /// <summary>Debounce for file system events: editors write a temp file and rename it, often several events per save.</summary>
    public static TimeSpan FileDebounce { get; set; } = TimeSpan.FromMilliseconds(100);

    const int HistorySize = 100;

    readonly SpyglassHub _hub;
    readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new(PathComparer);
    readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(PathComparer);
    readonly ConcurrentDictionary<string, string> _lastText = new(PathComparer);
    readonly ConcurrentQueue<HotReloadEvent> _history = new();
    readonly Dictionary<string, int> _editorOwned = new(PathComparer);   // path → number of editors that have it open
    bool _disposed;

    public HotReloadCoordinator(SpyglassHub hub)
    {
        _hub = hub;
        hub.SessionConnected += OnSessionConnected;
        hub.SessionDisconnected += _ => Changed?.Invoke();
    }

    /// <summary>
    /// False: do not watch source files (e.g. the app runs under <c>dotnet watch</c>, which delivers saved XAML itself
    /// through the stamp channel, design §6.1). Editor buffers are still applied.
    /// </summary>
    public bool WatchFiles { get; set; } = true;

    /// <summary>Path used in <see cref="HotReloadEvent"/> for C# deltas.</summary>
    public const string CodePath = "C#";

    /// <summary>Raised after a reload, or when the set of hot-reloadable apps changes.</summary>
    public event Action? Changed;

    /// <summary>Raised for every reload result (file watcher, editor pushes, and reloads the app started itself).</summary>
    public event Action<HotReloadEvent>? Reloaded;

    /// <summary>Newest last.</summary>
    public IReadOnlyList<HotReloadEvent> History => _history.ToList();

    /// <summary>Directories currently watched.</summary>
    public IReadOnlyCollection<string> WatchedDirectories => _watchers.Keys.ToList();

    public static StringComparer PathComparer { get; } = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>Sessions whose app compiled <paramref name="path"/>, with the document's URI there.</summary>
    public IReadOnlyList<(AgentSession Session, XamlDocumentInfo Document)> Targets(string path)
    {
        var full = Normalize(path);
        return _hub.Sessions
            .Where(s => s.IsConnected && s.HotReload is { Available: true })
            .SelectMany(s => s.HotReload!.Documents.Where(d => d.Path is not null && PathComparer.Equals(Normalize(d.Path), full)).Select(d => (s, d)))
            .ToList();
    }

    /// <summary>
    /// An editor took over this file: its unsaved text is pushed with <see cref="ApplyTextAsync"/>, so file system events for it
    /// are ignored until <see cref="ReleaseFromEditor"/> (design §6.2, the editor wins).
    /// </summary>
    public void ClaimForEditor(string path)
    {
        var full = Normalize(path);
        lock (_editorOwned) _editorOwned[full] = _editorOwned.GetValueOrDefault(full) + 1;
    }

    /// <summary>The editor closed the file: from now on the file on disk is the truth again; re-apply it if it differs.</summary>
    public void ReleaseFromEditor(string path)
    {
        var full = Normalize(path);
        lock (_editorOwned)
        {
            if (!_editorOwned.TryGetValue(full, out var n)) return;
            if (n > 1) { _editorOwned[full] = n - 1; return; }
            _editorOwned.Remove(full);
        }
        Schedule(full);
    }

    /// <summary>Pushes <paramref name="text"/> for <paramref name="path"/> to every app that compiled it.</summary>
    public async Task<IReadOnlyList<HotReloadEvent>> ApplyTextAsync(string path, string text, long version = 0, CancellationToken ct = default)
    {
        var full = Normalize(path);
        _lastText[full] = text;
        var results = new List<HotReloadEvent>();
        foreach (var (session, doc) in Targets(full))
        {
            HotReloadResult r;
            var failedBeforeApply = false;
            try { r = await session.ApplyXamlAsync(doc.Uri, text, version, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unknown document, hot reload unavailable, connection lost: the agent never reported it, so record it here.
                // Everything the agent did attempt comes back as hotReload/applied and is recorded in OnSessionConnected.
                failedBeforeApply = true;
                var code = (ex as SpyglassProtocolException)?.Code.ToString();
                r = new HotReloadResult(doc.Uri, false, 0, 0, [new XamlDiagnostic(XamlDiagnosticSeverity.Error, ex.Message, Code: code)], Version: version);
            }
            var e = new HotReloadEvent(DateTimeOffset.Now, full, session.App.Name, session.ShortId, r);
            results.Add(e);
            // The agent only notifies reloads it performed; record failures it never saw, and skips ("same content": e.g. an
            // error was fixed by going back to the text that is already live) so status shows the error is gone.
            // A superseded text was never looked at; the newer one reports for it.
            if (failedBeforeApply || r.Skipped is not null and not "superseded") Record(e);
        }
        return results;
    }

    void OnSessionConnected(AgentSession session)
    {
        if (!session.Has(Capabilities.HotReload)) return;
        session.HotReloadManifestChanged += _ => { UpdateWatchers(); Changed?.Invoke(); };
        session.HotReloadApplied += r =>
        {
            var path = Normalize(session.HotReload?.Documents.FirstOrDefault(d => d.Uri == r.Uri)?.Path ?? r.Uri);
            Record(new HotReloadEvent(DateTimeOffset.Now, path, session.App.Name, session.ShortId, r));
            // A stamp carries the saved file; if an editor holds newer unsaved text, put that back (design §6.2).
            bool owned;
            lock (_editorOwned) owned = _editorOwned.ContainsKey(path);
            if (r.Source == HotReloadSource.Stamp && owned && _lastText.TryGetValue(path, out var buffer))
                _ = ApplyTextAsync(path, buffer);
        };
        session.HotReloadCodeUpdated += u =>
        {
            var info = new List<XamlDiagnostic> { new(XamlDiagnosticSeverity.Info, $"Updated: {string.Join(", ", u.Types)}") };
            info.AddRange(u.Warnings.Select(w => new XamlDiagnostic(XamlDiagnosticSeverity.Warning, w)));
            Record(new HotReloadEvent(DateTimeOffset.Now, CodePath, session.App.Name, session.ShortId,
                new HotReloadResult("", true, u.Refreshed, u.Ms, info, HotReloadSource.Code)));
        };
        _ = Task.Run(async () =>
        {
            try { await session.GetHotReloadManifestAsync().ConfigureAwait(false); }
            catch { /* the session went away */ }
        });
    }

    void Record(HotReloadEvent e)
    {
        _history.Enqueue(e);
        while (_history.Count > HistorySize) _history.TryDequeue(out _);
        Reloaded?.Invoke(e);
        Changed?.Invoke();
    }

    // ---------- file watching ----------

    void UpdateWatchers()
    {
        if (_disposed || !WatchFiles) return;
        var dirs = _hub.Sessions
            .Where(s => s.HotReload is { Available: true })
            .SelectMany(s => s.HotReload!.Documents)
            .Where(d => d.Path is not null)
            .Select(d => Path.GetDirectoryName(Normalize(d.Path!)))
            .OfType<string>()
            .Where(Directory.Exists)
            .ToHashSet(PathComparer);

        foreach (var dir in dirs)
        {
            if (_watchers.ContainsKey(dir)) continue;
            var w = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            w.Filters.Add("*.axaml");
            w.Filters.Add("*.xaml");
            w.Changed += (_, e) => Schedule(e.FullPath);
            w.Created += (_, e) => Schedule(e.FullPath);
            w.Renamed += (_, e) => Schedule(e.FullPath);   // atomic save: write temp, rename over the original
            w.EnableRaisingEvents = true;
            if (!_watchers.TryAdd(dir, w)) w.Dispose();
        }
    }

    void Schedule(string path)
    {
        var full = Normalize(path);
        lock (_editorOwned) if (_editorOwned.ContainsKey(full)) return;
        if (Targets(full).Count == 0) return;

        var cts = new CancellationTokenSource();
        if (_pending.TryGetValue(full, out var previous)) previous.Cancel();
        _pending[full] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FileDebounce, cts.Token).ConfigureAwait(false);
                var text = await ReadAsync(full, cts.Token).ConfigureAwait(false);
                if (text is null) return;
                if (_lastText.TryGetValue(full, out var last) && last == text) return;   // touched, not changed
                await ApplyTextAsync(full, text, ct: cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            finally { _pending.TryRemove(new KeyValuePair<string, CancellationTokenSource>(full, cts)); }
        });
    }

    static async Task<string?> ReadAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false); }
            catch (FileNotFoundException) { return null; }
            catch (IOException) { await Task.Delay(50, ct).ConfigureAwait(false); }   // still being written
        }
        return null;
    }

    internal static string Normalize(string path) => Path.GetFullPath(path);

    /// <summary>Every source file some connected app can hot-reload.</summary>
    public IReadOnlyList<string> Files() => _hub.Sessions
        .Where(s => s.IsConnected && s.HotReload is { Available: true })
        .SelectMany(s => s.HotReload!.Documents)
        .Where(d => d.Path is not null)
        .Select(d => Normalize(d.Path!))
        .Distinct(PathComparer)
        .ToList();

    public void Dispose()
    {
        _disposed = true;
        foreach (var w in _watchers.Values) w.Dispose();
        _watchers.Clear();
        foreach (var c in _pending.Values) c.Cancel();
    }
}
