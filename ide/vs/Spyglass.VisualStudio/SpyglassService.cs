using System.Diagnostics;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Helpers;
using Microsoft.VisualStudio.Extensibility.Languages;
using Microsoft.VisualStudio.Extensibility.Settings;
using Microsoft.VisualStudio.ProjectSystem.Query;
using Microsoft.VisualStudio.RpcContracts.DiagnosticManagement;
using Microsoft.VisualStudio.RpcContracts.Documents;
using Microsoft.VisualStudio.RpcContracts.OpenDocument;
using Microsoft.VisualStudio.Threading;
using Spyglass.VisualStudio.Core;
using Range = Microsoft.VisualStudio.RpcContracts.Utilities.Range;

namespace Spyglass.VisualStudio;

#pragma warning disable VSEXTPREVIEW_SETTINGS // settings API: preview in 17.14
#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW // output window API: preview in 17.14

/// <summary>
/// One per extension: owns the hub connection (<see cref="HubClient"/>), the buffer sync, the Error List entries, the
/// "Spyglass" output pane and the status shown in the Spyglass tool window. Started by the first .axaml/.xaml editor
/// or Spyglass command (VisualStudio.Extensibility loads extensions on demand).
/// </summary>
internal sealed class SpyglassService : DisposableObject, IDocumentEventsListener
{
    readonly VisualStudioExtensibility _vs;
    readonly TraceSource _trace;
    readonly AsyncLazy<bool> _started;
    readonly SemaphoreSlim _diagnosticsGate = new(1, 1);
    readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);   // paths with Error List entries
    readonly Dictionary<string, int> _views = new(StringComparer.OrdinalIgnoreCase); // open text views per file
    readonly List<IDisposable> _subscriptions = [];
    OutputChannel? _output;
    DiagnosticsReporter? _diagnostics;
    HubClient? _client;
    XamlBufferSync? _sync;
    IReadOnlyList<string> _workspaceFolders = [];
    string? _lastSummary;
    bool _lastOk = true;
    bool _enabled = true;

    public SpyglassService(VisualStudioExtensibility extensibility, TraceSource trace)
    {
        _vs = extensibility;
        _trace = trace;
        _started = new AsyncLazy<bool>(StartAsync, null);
    }

    public SpyglassStatusViewModel Status { get; } = new();

    public HubClient Client => _client ?? throw new InvalidOperationException("Spyglass is not started.");

    public XamlBufferSync Sync => _sync ?? throw new InvalidOperationException("Spyglass is not started.");

    public Task EnsureStartedAsync(CancellationToken ct) => _started.GetValueAsync(ct);

    // ---------- start ----------

    async Task<bool> StartAsync()
    {
        var ct = CancellationToken.None;
        try { _output = await _vs.Views().Output.CreateOutputChannelAsync("Spyglass", ct); }
        catch (Exception ex) { _trace.TraceEvent(TraceEventType.Warning, 0, $"Spyglass output pane: {ex.Message}"); }
        _diagnostics = _vs.Languages().GetDiagnosticsReporter("Spyglass");

        var settings = _vs.Settings();
        _enabled = (await settings.ReadEffectiveValueAsync(SpyglassSettings.HotReloadEnabled, ct)).ValueOrDefault(true);
        var delay = (await settings.ReadEffectiveValueAsync(SpyglassSettings.HotReloadDelay, ct)).ValueOrDefault(300);
        var port = (await settings.ReadEffectiveValueAsync(SpyglassSettings.Port, ct)).ValueOrDefault(0);
        var command = (await settings.ReadEffectiveValueAsync(SpyglassSettings.Command, ct)).ValueOrDefault("spyglass");
        var autoStart = (await settings.ReadEffectiveValueAsync(SpyglassSettings.AutoStart, ct)).ValueOrDefault(true);
        _workspaceFolders = await QueryWorkspaceFoldersAsync(ct);

        _client = new HubClient(new HubClientOptions
        {
            Port = port,
            Ide = "vs",
            Version = typeof(SpyglassService).Assembly.GetName().Version?.ToString() ?? "0",
            WorkspaceFolders = () => _workspaceFolders,
            Command = command,
            AutoStart = autoStart,
            Log = Log,
        });
        _sync = new XamlBufferSync(_client) { DelayMs = delay, Enabled = _enabled };

        _client.StateChanged += _ => Render();
        _client.StatusChanged += s =>
        {
            if (s is null) _ = ClearAllDiagnosticsAsync();
            Render();
        };
        _client.DiagnosticsReceived += d => _ = ShowDiagnosticsAsync(d);
        _client.RevealHandler = RevealAsync;
        _client.Connected += () => _ = RefreshWorkspaceFoldersAsync();

        _subscriptions.Add(await _vs.Documents().SubscribeAsync(this, filterRegex: null, ct));
        _subscriptions.Add(await settings.SubscribeAsync([SpyglassSettings.Category], ct, OnSettingsChanged));

        Status.Reconnect = () => Client.Reconnect();
        Status.Toggle = () => SetEnabledAsync(!_enabled, CancellationToken.None);
        Render();
        _client.Start();
        return true;
    }

    async Task<IReadOnlyList<string>> QueryWorkspaceFoldersAsync(CancellationToken ct)
    {
        try
        {
            var solutions = await _vs.Workspaces().QuerySolutionAsync(s => s.With(x => x.Directory), ct);
            return solutions.Select(s => s.Directory).Where(d => !string.IsNullOrEmpty(d)).ToList();
        }
        catch (Exception ex)
        {
            _trace.TraceEvent(TraceEventType.Warning, 0, $"Spyglass: no solution folder: {ex.Message}");
            return [];
        }
    }

    async Task RefreshWorkspaceFoldersAsync()
    {
        var folders = await QueryWorkspaceFoldersAsync(CancellationToken.None);
        if (!folders.SequenceEqual(_workspaceFolders)) _workspaceFolders = folders;   // used by the next ide/hello
    }

    void OnSettingsChanged(SettingValues values)
    {
        _ = Task.Run(async () =>
        {
            var s = _vs.Settings();
            var enabled = (await s.ReadEffectiveValueAsync(SpyglassSettings.HotReloadEnabled, default)).ValueOrDefault(true);
            Sync.DelayMs = (await s.ReadEffectiveValueAsync(SpyglassSettings.HotReloadDelay, default)).ValueOrDefault(300);
            if (enabled != _enabled) await ApplyEnabledAsync(enabled);
        });
    }

    // ---------- commands ----------

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        await EnsureStartedAsync(ct);
        await _vs.Settings().WriteAsync(b => b.WriteSetting(SpyglassSettings.HotReloadEnabled, enabled), "Toggle Spyglass XAML hot reload", ct);
        await ApplyEnabledAsync(enabled);   // also arrives through OnSettingsChanged; applying twice is harmless
    }

    async Task ApplyEnabledAsync(bool enabled)
    {
        _enabled = enabled;
        Sync.Enabled = enabled;
        if (!enabled) await ClearAllDiagnosticsAsync();
        Log(enabled ? "XAML hot reload on." : "XAML hot reload off.");
        Render();
    }

    public bool HotReloadEnabled => _enabled;

    // ---------- editor → hub ----------

    public void ViewOpened(string path, Func<string> text, bool isDirty)
    {
        lock (_views) _views[path] = _views.GetValueOrDefault(path) + 1;
        Sync.Opened(path, text, isDirty);
    }

    public void ViewChanged(string path, Func<string> text, bool isDirty) => Sync.Changed(path, text, isDirty);

    /// <summary>
    /// A view closed. Returns whether it was the file's last one (a split or second window keeps the file open); then the
    /// hub hands the file back to its file watcher.
    /// </summary>
    public bool ViewClosed(string path)
    {
        lock (_views)
        {
            var n = _views.GetValueOrDefault(path) - 1;
            if (n > 0) { _views[path] = n; return false; }
            _views.Remove(path);
        }
        Sync.Closed(path);
        _ = ClearDiagnosticsAsync(path);
        return true;
    }

    Task IDocumentEventsListener.SavedAsync(DocumentEventArgs e, CancellationToken ct)
    {
        if (_sync is not null && e.Moniker.IsFile) _sync.Saved(e.Moniker.LocalPath);
        return Task.CompletedTask;
    }

    Task IDocumentEventsListener.OpenedAsync(DocumentEventArgs e, CancellationToken ct) => Task.CompletedTask;
    Task IDocumentEventsListener.ClosedAsync(DocumentEventArgs e, CancellationToken ct) => Task.CompletedTask;
    Task IDocumentEventsListener.SavingAsync(DocumentEventArgs e, CancellationToken ct) => Task.CompletedTask;
    Task IDocumentEventsListener.RenamedAsync(RenamedDocumentEventArgs e, CancellationToken ct) => Task.CompletedTask;
    Task IDocumentEventsListener.ShownAsync(DocumentEventArgs e, CancellationToken ct) => Task.CompletedTask;
    Task IDocumentEventsListener.HiddenAsync(DocumentEventArgs e, CancellationToken ct) => Task.CompletedTask;

    // ---------- hub → editor ----------

    async Task<bool> RevealAsync(EditorRevealParams r, CancellationToken ct)
    {
        var at = new Range(Math.Max(0, r.Line - 1), Math.Max(0, r.Column - 1), Math.Max(0, r.Line - 1), Math.Max(0, r.Column - 1));
        await _vs.Documents().OpenDocumentAsync(new Uri(r.Path), new OpenDocumentOptions(selection: at, ensureVisible: at, isPreview: false, activate: true), ct);
        Log($"Opened {Path.GetFileName(r.Path)}:{r.Line}:{r.Column} for Spyglass.");
        return true;
    }

    async Task ShowDiagnosticsAsync(XamlDiagnosticsParams d)
    {
        if (_diagnostics is null) return;
        var uri = new Uri(d.Path);
        var text = _sync?.TryGetText(d.Path);
        await _diagnosticsGate.WaitAsync();
        try
        {
            // A result for an older version than the editor has now is still the best we know; show it.
            await _diagnostics.ClearDiagnosticsAsync(uri, CancellationToken.None);
            if (d.Items.Count == 0) _reported.Remove(d.Path);
            else
            {
                _reported.Add(d.Path);
                await _diagnostics.ReportDiagnosticsAsync(d.Items.Select(i =>
                {
                    var span = DiagnosticRange.Compute(text, i.Line, i.Column);
                    return new DocumentDiagnostic(uri, new Range(span.StartLine, span.StartColumn, span.EndLine, span.EndColumn), i.Message)
                    {
                        Severity = i.Severity switch
                        {
                            XamlDiagnosticSeverity.Error => DiagnosticSeverity.Error,
                            XamlDiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
                            _ => DiagnosticSeverity.Information,
                        },
                        ErrorCode = i.Code,
                        ProviderName = "Spyglass hot reload",
                    };
                }), CancellationToken.None);
            }
        }
        catch (Exception ex) { _trace.TraceEvent(TraceEventType.Warning, 0, $"Spyglass diagnostics: {ex.Message}"); }
        finally { _diagnosticsGate.Release(); }

        _lastSummary = d.Summary;
        _lastOk = d.Items.All(i => i.Severity != XamlDiagnosticSeverity.Error);
        Log(d.Summary);
        Render();
    }

    async Task ClearDiagnosticsAsync(string path)
    {
        if (_diagnostics is null) return;
        await _diagnosticsGate.WaitAsync();
        try
        {
            if (_reported.Remove(path)) await _diagnostics.ClearDiagnosticsAsync(new Uri(path), CancellationToken.None);
        }
        catch { /* the document is gone */ }
        finally { _diagnosticsGate.Release(); }
    }

    async Task ClearAllDiagnosticsAsync()
    {
        if (_diagnostics is null) return;
        await _diagnosticsGate.WaitAsync();
        try
        {
            foreach (var p in _reported) try { await _diagnostics.ClearDiagnosticsAsync(new Uri(p), CancellationToken.None); } catch { }
            _reported.Clear();
        }
        finally { _diagnosticsGate.Release(); }
    }

    // ---------- status / log ----------

    void Render()
    {
        if (_client is null) return;
        var view = HubStatusPresenter.Describe(_enabled, _client.State, _client.Port, _client.Status, _lastSummary, _lastOk);
        Status.Update(view, _enabled);
    }

    public void Log(string line)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        Status.AppendLog(stamped);
        _trace.TraceInformation(line);
        if (_output is { } o) _ = o.Writer.WriteLineAsync(stamped);
    }

    protected override void Dispose(bool isDisposing)
    {
        if (isDisposing)
        {
            foreach (var s in _subscriptions) s.Dispose();
            _sync?.Dispose();
            if (_client is not null) _ = _client.DisposeAsync().AsTask();
            _diagnostics?.Dispose();
            _output?.Dispose();
        }
        base.Dispose(isDisposing);
    }
}
