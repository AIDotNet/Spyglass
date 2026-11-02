using System.Diagnostics;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Helpers;
using Microsoft.VisualStudio.Extensibility.Languages;
using Microsoft.VisualStudio.Extensibility.Settings;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.RpcContracts.DiagnosticManagement;
using Microsoft.VisualStudio.RpcContracts.Documents;
using Microsoft.VisualStudio.RpcContracts.OpenDocument;
using Spyglass.VisualStudio.Core;
using Spyglass.VisualStudio.Core.Preview;
using Range = Microsoft.VisualStudio.RpcContracts.Utilities.Range;

namespace Spyglass.VisualStudio;

#pragma warning disable VSEXTPREVIEW_SETTINGS // settings API: preview in 17.14

/// <summary>
/// Design-time preview of the active .axaml (design/preview/README.md): everything IDE-independent is in
/// <see cref="PreviewController"/>; this class adapts it to Visual Studio. The preview runs while the Spyglass Preview
/// window is open (<see cref="PreviewWindow"/>, WPF in this process); closing the window stops the previewer process.
/// </summary>
internal sealed class SpyglassPreviewService : DisposableObject, IDocumentEventsListener
{
    readonly VisualStudioExtensibility _vs;
    readonly TraceSource _trace;
    readonly SpyglassService _spyglass;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly SemaphoreSlim _diagnosticsGate = new(1, 1);
    readonly Dictionary<string, Func<string>> _texts = new(StringComparer.OrdinalIgnoreCase);   // open .axaml text views
    readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);
    readonly PreviewWindowHost _host = new();
    PreviewController? _controller;
    PreviewWindow? _window;
    DiagnosticsReporter? _diagnostics;
    IDisposable? _documents;
    IDisposable? _settingsSubscription;
    int _open;

    public SpyglassPreviewService(VisualStudioExtensibility extensibility, TraceSource trace, SpyglassService spyglass)
    {
        _vs = extensibility;
        _trace = trace;
        _spyglass = spyglass;
    }

    public bool IsOpen => Volatile.Read(ref _open) != 0;

    // ---------- window lifetime ----------

    /// <summary>"Open Preview": open the window (or bring it up) and show <paramref name="activePath"/> in it.</summary>
    public async Task OpenAsync(string? activePath, CancellationToken ct, bool activate = true)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_window is not null)
            {
                if (activate) _host.Activate();   // already open: Open Preview focuses the existing window
            }
            else
            {
                var settings = _vs.Settings();
                var enabled = (await settings.ReadEffectiveValueAsync(SpyglassSettings.PreviewEnabled, ct)).ValueOrDefault(true);
                if (!enabled)
                {
                    await _vs.Shell().ShowPromptAsync("The Spyglass preview is turned off (Tools › Options › Spyglass › Enable the design-time preview).", PromptOptions.OK, ct);
                    return;
                }
                var delay = (await settings.ReadEffectiveValueAsync(SpyglassSettings.PreviewDelay, ct)).ValueOrDefault(300);
                var zoom = PreviewZoom.Parse((await settings.ReadEffectiveValueAsync(SpyglassSettings.PreviewZoom, ct)).ValueOrDefault("fit"));
                var theme = ParseTheme((await settings.ReadEffectiveValueAsync(SpyglassSettings.PreviewTheme, ct)).ValueOrDefault("default"));

                _diagnostics = _vs.Languages().GetDiagnosticsReporter("Spyglass Preview");
                _controller = new PreviewController(new PreviewControllerOptions { DelayMs = delay, Log = Log }) { Theme = theme };
                _controller.DiagnosticsChanged += (p, d) => _ = ShowDiagnosticsAsync(p, d);

                var controller = _controller;
                try
                {
                    _window = await _host.OpenAsync(
                        () => new PreviewWindow(controller, zoom, theme, (p, l, c) => _ = RevealAsync(p, l, c), (z, t) => _ = SaveViewSettingsAsync(z, t)) { ShowActivated = activate },
                        () => _ = DetachAsync());
                }
                catch (Exception ex)
                {
                    Log($"Could not open the preview window: {ex.Message}");
                    _trace.TraceEvent(TraceEventType.Error, 0, $"Spyglass preview window: {ex}");
                    await DetachCoreAsync();
                    await _vs.Shell().ShowPromptAsync("The Spyglass preview window could not be opened: " + ex.Message, PromptOptions.OK, ct);
                    return;
                }

                var window = _window;
                controller.StatusChanged += s => PreviewWindowHost.Post(window.Dispatcher, () => window.SetStatus(s));
                controller.FrameReady += window.SubmitFrame;
                _documents = await _vs.Documents().SubscribeAsync(this, filterRegex: null, ct);
                _settingsSubscription = await settings.SubscribeAsync([SpyglassSettings.Category], ct, OnSettingsChanged);
                Volatile.Write(ref _open, 1);
            }
        }
        finally { _gate.Release(); }

        if (activePath is not null) ShowActive(activePath);
    }

    /// <summary>The window closed: stop the previewer and remove its Error List entries.</summary>
    async Task DetachAsync()
    {
        await _gate.WaitAsync();
        try { await DetachCoreAsync(); }
        finally { _gate.Release(); }
    }

    async Task DetachCoreAsync()
    {
        Volatile.Write(ref _open, 0);
        _documents?.Dispose(); _documents = null;
        _settingsSubscription?.Dispose(); _settingsSubscription = null;
        var controller = _controller;
        _controller = null;
        _window = null;
        if (controller is not null) await controller.DisposeAsync();
        await ClearAllDiagnosticsAsync();
        _diagnostics?.Dispose(); _diagnostics = null;
        _host.Close();
    }

    static PreviewTheme ParseTheme(string? s) => Enum.TryParse<PreviewTheme>(s, ignoreCase: true, out var t) ? t : PreviewTheme.Default;

    void OnSettingsChanged(SettingValues values)
    {
        _ = Task.Run(async () =>
        {
            var s = _vs.Settings();
            var delay = (await s.ReadEffectiveValueAsync(SpyglassSettings.PreviewDelay, default)).ValueOrDefault(300);
            if (_controller is { } c) c.DelayMs = delay;
        });
    }

    async Task SaveViewSettingsAsync(PreviewZoom zoom, PreviewTheme theme)
    {
        try
        {
            await _vs.Settings().WriteAsync(b =>
            {
                b.WriteSetting(SpyglassSettings.PreviewZoom, zoom.ToString());
                b.WriteSetting(SpyglassSettings.PreviewTheme, theme.ToString().ToLowerInvariant());
            }, "Spyglass preview view", CancellationToken.None);
        }
        catch (Exception ex) { _trace.TraceEvent(TraceEventType.Warning, 0, $"Spyglass preview settings: {ex.Message}"); }
    }

    // ---------- editor → preview ----------

    public void ViewOpened(string path, Func<string> text)
    {
        lock (_texts) _texts[path] = text;
        // A text view opens when its document is opened or brought up: it is the one being looked at.
        if (IsOpen) ShowIfPreviewable(path, text);
        else _ = AutoOpenAsync(path);
    }

    /// <summary>previewAutoOpen: opening an .axaml shows the preview window (without taking the keyboard from the editor).</summary>
    async Task AutoOpenAsync(string path)
    {
        try
        {
            var settings = _vs.Settings();
            var auto = (await settings.ReadEffectiveValueAsync(SpyglassSettings.PreviewAutoOpen, default)).ValueOrDefault(false);
            if (!auto) return;
            var enabled = (await settings.ReadEffectiveValueAsync(SpyglassSettings.PreviewEnabled, default)).ValueOrDefault(true);
            if (PreviewPolicy.ShouldAutoOpen(path, auto, enabled, IsOpen)) await OpenAsync(path, CancellationToken.None, activate: false);
        }
        catch (Exception ex) { _trace.TraceEvent(TraceEventType.Warning, 0, $"Spyglass preview auto-open: {ex.Message}"); }
    }

    /// <summary>Views are shown by themselves; App.axaml, resource dictionaries and styles only refresh the active view.</summary>
    void ShowIfPreviewable(string path, Func<string> text)
    {
        string xaml;
        try { xaml = text(); } catch { return; }
        if (PreviewPolicy.IsPreviewable(xaml)) _controller?.Show(path, text);
        else _controller?.Saved(path);   // a project file: re-render the active view
    }

    public void ViewChanged(string path, Func<string> text)
    {
        lock (_texts) _texts[path] = text;
        if (IsOpen) _controller?.TextChanged(path);
    }

    public void ViewClosed(string path)
    {
        lock (_texts) _texts.Remove(path);
        _ = ClearDiagnosticsAsync(path);
    }

    /// <summary>Shows this file in the preview (it must have an open text view, which supplies the unsaved text).</summary>
    public void ShowActive(string path)
    {
        Func<string>? text;
        lock (_texts) _texts.TryGetValue(path, out text);
        if (text is not null && IsOpen) ShowIfPreviewable(path, text);
    }

    Task IDocumentEventsListener.ShownAsync(DocumentEventArgs e, CancellationToken ct)
    {
        if (e.Moniker.IsFile && XamlBufferSync.IsXaml(e.Moniker.LocalPath)) ShowActive(e.Moniker.LocalPath);
        return Task.CompletedTask;
    }

    Task IDocumentEventsListener.SavedAsync(DocumentEventArgs e, CancellationToken ct)
    {
        if (e.Moniker.IsFile && IsOpen) _controller?.Saved(e.Moniker.LocalPath);
        return Task.CompletedTask;
    }

    Task IDocumentEventsListener.OpenedAsync(DocumentEventArgs e, CancellationToken ct) => Task.CompletedTask;
    Task IDocumentEventsListener.ClosedAsync(DocumentEventArgs e, CancellationToken ct) => Task.CompletedTask;
    Task IDocumentEventsListener.SavingAsync(DocumentEventArgs e, CancellationToken ct) => Task.CompletedTask;
    Task IDocumentEventsListener.RenamedAsync(RenamedDocumentEventArgs e, CancellationToken ct) => Task.CompletedTask;
    Task IDocumentEventsListener.HiddenAsync(DocumentEventArgs e, CancellationToken ct) => Task.CompletedTask;

    // ---------- preview → editor ----------

    /// <summary>The error bar was clicked: open the file at the error (1-based line/column from the previewer).</summary>
    async Task RevealAsync(string path, int? line, int? column)
    {
        try
        {
            var at = new Range(Math.Max(0, (line ?? 1) - 1), Math.Max(0, (column ?? 1) - 1), Math.Max(0, (line ?? 1) - 1), Math.Max(0, (column ?? 1) - 1));
            await _vs.Documents().OpenDocumentAsync(new Uri(path), new OpenDocumentOptions(selection: at, ensureVisible: at, isPreview: false, activate: true), CancellationToken.None);
        }
        catch (Exception ex) { Log($"Could not open {Path.GetFileName(path)}: {ex.Message}"); }
    }

    // ---------- diagnostics (Error List, provider "Spyglass Preview") ----------

    async Task ShowDiagnosticsAsync(string path, PreviewDiagnostic? d)
    {
        if (_diagnostics is not { } reporter) return;
        var uri = new Uri(path);
        string? text = null;
        lock (_texts) if (_texts.TryGetValue(path, out var t)) try { text = t(); } catch { }
        await _diagnosticsGate.WaitAsync();
        try
        {
            await reporter.ClearDiagnosticsAsync(uri, CancellationToken.None);
            if (d is null) { _reported.Remove(path); return; }
            _reported.Add(path);
            var span = DiagnosticRange.Compute(text, d.Line, d.Column);
            await reporter.ReportDiagnosticsAsync(
            [
                new DocumentDiagnostic(uri, new Range(span.StartLine, span.StartColumn, span.EndLine, span.EndColumn), d.Message)
                {
                    Severity = DiagnosticSeverity.Error,
                    ProviderName = "Spyglass Preview",
                },
            ], CancellationToken.None);
        }
        catch (Exception ex) { _trace.TraceEvent(TraceEventType.Warning, 0, $"Spyglass preview diagnostics: {ex.Message}"); }
        finally { _diagnosticsGate.Release(); }
    }

    async Task ClearDiagnosticsAsync(string path)
    {
        if (_diagnostics is not { } reporter) return;
        await _diagnosticsGate.WaitAsync();
        try { if (_reported.Remove(path)) await reporter.ClearDiagnosticsAsync(new Uri(path), CancellationToken.None); }
        catch { /* the document is gone */ }
        finally { _diagnosticsGate.Release(); }
    }

    async Task ClearAllDiagnosticsAsync()
    {
        if (_diagnostics is not { } reporter) return;
        await _diagnosticsGate.WaitAsync();
        try
        {
            foreach (var p in _reported) try { await reporter.ClearDiagnosticsAsync(new Uri(p), CancellationToken.None); } catch { }
            _reported.Clear();
        }
        finally { _diagnosticsGate.Release(); }
    }

    void Log(string line) => _spyglass.Log("[preview] " + line);

    protected override void Dispose(bool isDisposing)
    {
        if (isDisposing) _ = DetachAsync();
        base.Dispose(isDisposing);
    }
}
