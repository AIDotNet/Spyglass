using Avalonia.Remote.Protocol.Input;

namespace Spyglass.VisualStudio.Core.Preview;

public enum PreviewStatusKind { Idle, Starting, Updating, Running, Error, Exited }

/// <summary>
/// What the preview pane shows besides the picture. <see cref="XamlError"/> is the last XAML error (the picture is then
/// the last good frame); <see cref="CanBuild"/> asks the pane to offer a "Build project" button.
/// </summary>
public sealed record PreviewStatus(PreviewStatusKind Kind, string Message, bool CanBuild = false, string? XamlError = null, int? ErrorLine = null, int? ErrorColumn = null);

/// <summary>A XAML error for the editor (1-based line and column when the host knew them).</summary>
public sealed record PreviewDiagnostic(string Path, int? Line, int? Column, string Message);

public sealed class PreviewControllerOptions
{
    public string Dotnet { get; init; } = "dotnet";
    public Action<string>? Log { get; init; }

    /// <summary>Milliseconds without typing before an edit is sent (<c>spyglass.preview.delay</c>).</summary>
    public int DelayMs { get; set; } = 300;

    /// <summary>Replaceable for tests.</summary>
    public Func<string, CancellationToken, Task<PreviewProjectResult>>? ResolveProject { get; init; }

    /// <summary>Quiet time after the last change of the app's assembly before the previewer restarts (the build writes in steps).</summary>
    public TimeSpan RebuildDelay { get; init; } = TimeSpan.FromMilliseconds(600);

    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// The IDE-independent half of the preview pane (design/preview/README.md §4): follows the active .axaml, resolves its
/// project, keeps one previewer host per project, sends the (unsaved) text after a debounce, resizes the viewport,
/// restarts the host when the app was rebuilt, and turns host answers into frames, a status and editor diagnostics.
/// </summary>
public sealed class PreviewController : IAsyncDisposable
{
    const string UpdateKey = "update";
    const string ViewportKey = "viewport";
    const string RebuildKey = "rebuild";

    sealed class Active(string path, Func<string> text)
    {
        public readonly string Path = path;
        public readonly Func<string> Text = text;
    }

    readonly PreviewControllerOptions _options;
    readonly Debouncer _debounce;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly object _state = new();
    readonly CancellationTokenSource _cts = new();
    Active? _active;
    PreviewSession? _session;
    readonly PreviewShadowCopy _shadow = new();
    PreviewProject? _sessionProject;   // the project as resolved; the session itself runs from _shadow
    DateTime _sessionTarget;    // write time of TargetPath when the host started
    string? _pendingPath;         // the .axaml whose UpdateXaml answer is awaited
    PreviewXamlEdit _pendingEdit; // what was added to that XAML before sending (maps error positions back)
    PreviewTheme _theme;
    readonly System.Diagnostics.Stopwatch _updateClock = new();
    FileSystemWatcher? _targetWatcher;     // the app's output: a rebuild restarts the host
    string? _projectDirectory;             // of the active file's project, once known
    PreviewStatus _status = new(PreviewStatusKind.Idle, "Open an .axaml file to see its preview.");
    double _vw = 800, _vh = 600, _dpiX = 96, _dpiY = 96;
    bool _disposed;

    public PreviewController(PreviewControllerOptions? options = null)
    {
        _options = options ?? new();
        _debounce = new Debouncer(key =>
        {
            if (key == ViewportKey) _ = SendViewportAsync();
            else if (key == RebuildKey) _ = UpdateAsync();
            else _ = UpdateAsync();
        });
    }

    public PreviewStatus Status { get { lock (_state) return _status; } }

    public string? ActivePath { get { lock (_state) return _active?.Path; } }

    public int DelayMs { get => _options.DelayMs; set => _options.DelayMs = value; }

    public event Action<PreviewStatus>? StatusChanged;

    public event Action<PreviewFrame>? FrameReady;

    /// <summary>The XAML error of a file (or null when it is fine again). Raised for the file the update was sent for.</summary>
    public event Action<string, PreviewDiagnostic?>? DiagnosticsChanged;

    // ---------- editor → controller ----------

    /// <summary>The active document is now this .axaml/.xaml (also when the pane is opened). The text is read lazily.</summary>
    public void Show(string path, Func<string> text)
    {
        lock (_state)
        {
            if (_disposed) return;
            _active = new Active(path, text);
        }
        _debounce.Schedule(UpdateKey, TimeSpan.Zero);
    }

    /// <summary>The active document's text changed (debounced by <see cref="DelayMs"/>).</summary>
    public void TextChanged(string path)
    {
        if (!IsActive(path) && !IsProjectXaml(path)) return;
        _debounce.Schedule(UpdateKey, TimeSpan.FromMilliseconds(Math.Max(0, _options.DelayMs)));
    }

    /// <summary>The document was saved: update now (it may have been reverted or formatted on save).</summary>
    public void Saved(string path)
    {
        if (IsActive(path) || IsProjectXaml(path)) _debounce.Schedule(UpdateKey, TimeSpan.Zero);
    }

    /// <summary>The theme variant to render with (a Window root only; see <see cref="PreviewXaml.ApplyTheme"/>). Re-renders.</summary>
    public PreviewTheme Theme
    {
        get { lock (_state) return _theme; }
        set
        {
            lock (_state) { if (_theme == value) return; _theme = value; }
            Refresh();
        }
    }

    /// <summary>The user asked for a refresh, or the project was built.</summary>
    public void Refresh() => _debounce.Schedule(UpdateKey, TimeSpan.Zero);

    /// <summary>The pane was resized (device-independent pixels); sent after 100 ms of calm.</summary>
    public void SetViewport(double width, double height, double dpiX = 96, double dpiY = 96)
    {
        if (width < 1 || height < 1) return;
        lock (_state) { _vw = width; _vh = height; _dpiX = dpiX; _dpiY = dpiY; }
        _debounce.Schedule(ViewportKey, TimeSpan.FromMilliseconds(100));
    }

    /// <summary>The pane was closed: stop the host.</summary>
    public async Task CloseAsync()
    {
        _debounce.Cancel(UpdateKey);
        _debounce.Cancel(ViewportKey);
        lock (_state) _active = null;
        await StopSessionAsync().ConfigureAwait(false);
        SetStatus(new(PreviewStatusKind.Idle, "Open an .axaml file to see its preview."));
    }

    /// <summary>"Build project": `dotnet build` the active file's project, then update.</summary>
    public async Task BuildAsync(CancellationToken ct = default)
    {
        string? file;
        lock (_state) file = _active?.Path;
        if (file is null) return;
        var project = PreviewProjectResolver.FindProject(file);
        if (project is null) return;
        SetStatus(new(PreviewStatusKind.Starting, $"Building {Path.GetFileName(project)}…"));
        var (ok, output) = await PreviewProjectResolver.BuildAsync(project, _options.Dotnet, ct).ConfigureAwait(false);
        _options.Log?.Invoke(ok ? "Build succeeded." : "Build failed:\n" + output);
        if (!ok) SetStatus(new(PreviewStatusKind.Error, "The build failed; see the Spyglass output.", CanBuild: true));
        else Refresh();
    }

    // ---------- input forwarding ----------

    public Task PointerMovedAsync(double x, double y, InputModifiers[]? m = null) => _session?.PointerMovedAsync(x, y, m) ?? Task.CompletedTask;
    public Task PointerPressedAsync(double x, double y, MouseButton b, InputModifiers[]? m = null) => _session?.PointerPressedAsync(x, y, b, m) ?? Task.CompletedTask;
    public Task PointerReleasedAsync(double x, double y, MouseButton b, InputModifiers[]? m = null) => _session?.PointerReleasedAsync(x, y, b, m) ?? Task.CompletedTask;
    public Task ScrollAsync(double x, double y, double dx, double dy, InputModifiers[]? m = null) => _session?.ScrollAsync(x, y, dx, dy, m) ?? Task.CompletedTask;
    public Task KeyAsync(bool down, Key key, PhysicalKey physical = default, string? symbol = null, InputModifiers[]? m = null) => _session?.KeyAsync(down, key, physical, symbol, m) ?? Task.CompletedTask;
    public Task TextInputAsync(string text, InputModifiers[]? m = null) => _session?.TextInputAsync(text, m) ?? Task.CompletedTask;

    // ---------- the update loop ----------

    /// <summary>
    /// Another .axaml of the active file's project (App.axaml, styles, resource dictionaries, other views) was edited or
    /// saved: the active file is re-rendered. (The previewer reads styles and resources from the built assembly, so such
    /// changes show once the project is rebuilt, which restarts the previewer by itself.)
    /// </summary>
    bool IsProjectXaml(string path)
    {
        string? dir, active;
        lock (_state) { dir = _projectDirectory; active = _active?.Path; }
        dir ??= active is null ? null : (PreviewProjectResolver.FindProject(active) is { } p ? Path.GetDirectoryName(p) : null);
        return PreviewPolicy.IsProjectXaml(dir, path);
    }

    bool IsActive(string path)
    {
        lock (_state) return _active is { } a && string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase);
    }

    async Task UpdateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Active? active;
            lock (_state) active = _disposed ? null : _active;
            if (active is null) return;
            var ct = _cts.Token;

            var resolve = _options.ResolveProject ?? ((p, token) => PreviewProjectResolver.ResolveAsync(p, _options.Dotnet, token));
            var resolved = await resolve(active.Path, ct).ConfigureAwait(false);
            if (resolved.Project is not { } project)
            {
                await StopSessionAsync().ConfigureAwait(false);
                SetStatus(new(PreviewStatusKind.Error, resolved.Error ?? "No preview available.", resolved.CanBuild));
                return;
            }

            lock (_state) _projectDirectory = project.ProjectDirectory;
            if (!await EnsureSessionAsync(project, ct).ConfigureAwait(false)) return;

            string xaml;
            try { xaml = active.Text(); }
            catch (Exception ex) { _options.Log?.Invoke($"Could not read {active.Path}: {ex.Message}"); return; }

            PreviewTheme theme;
            lock (_state) theme = _theme;
            var (toSend, edit) = PreviewXaml.ApplyTheme(xaml, theme);
            _pendingPath = active.Path;
            _pendingEdit = edit;
            _updateClock.Restart();
            SetStatus(new(PreviewStatusKind.Updating, "Updating…"));
            await _session!.UpdateXamlAsync(toSend, active.Path).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _options.Log?.Invoke($"Preview failed: {ex}");
            SetStatus(new(PreviewStatusKind.Error, ex.Message));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Keeps the host of this project running; restarts it after a rebuild or a crash. False = failed to start.</summary>
    async Task<bool> EnsureSessionAsync(PreviewProject project, CancellationToken ct)
    {
        var stamp = File.GetLastWriteTimeUtc(project.TargetPath);
        var session = _session;
        if (session is not null && !session.HasExited && _sessionProject == project && stamp == _sessionTarget) return true;

        if (session is not null)
        {
            _options.Log?.Invoke(session.HasExited ? "Restarting the previewer (it exited)." :
                _sessionProject != project ? "Switching the previewer to another project." : "The app was rebuilt; restarting the previewer.");
            await StopSessionAsync().ConfigureAwait(false);
        }

        SetStatus(new(PreviewStatusKind.Starting, $"Starting the previewer for {Path.GetFileNameWithoutExtension(project.TargetPath)}…"));
        PreviewProject copy;
        try { copy = await _shadow.SyncAsync(project, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke($"Could not copy the build output: {ex.Message}");
            SetStatus(new(PreviewStatusKind.Error, "Could not copy the build output: " + ex.Message, CanBuild: true));
            return false;
        }
        var created = new PreviewSession(copy, new PreviewSessionOptions { Dotnet = _options.Dotnet, Log = _options.Log, DpiX = _dpiX, DpiY = _dpiY });
        created.Frame += f => { if (ReferenceEquals(_session, created)) FrameReady?.Invoke(f); };
        created.XamlResult += r => { if (ReferenceEquals(_session, created)) OnXamlResult(r); };
        created.Exited += code =>
        {
            if (!ReferenceEquals(_session, created)) return;   // stopped on purpose
            _options.Log?.Invoke($"The previewer exited (code {code?.ToString() ?? "?"}).");
            SetStatus(new(PreviewStatusKind.Exited, "The preview process exited. Edit the file or press Refresh to restart it."));
        };
        _session = created;
        try
        {
            await created.StartAsync(_options.StartTimeout, ct).ConfigureAwait(false);
            _sessionTarget = stamp;
            _sessionProject = project;
            WatchTarget(project);   // the real output, not the copy the previewer runs from
            double w, h, dx, dy;
            lock (_state) { w = _vw; h = _vh; dx = _dpiX; dy = _dpiY; }
            await created.SetViewportAsync(w, h, dx, dy).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { _session = null; await created.DisposeAsync().ConfigureAwait(false); throw; }
        catch (Exception ex)
        {
            _session = null;
            await created.DisposeAsync().ConfigureAwait(false);
            _options.Log?.Invoke($"Previewer start failed: {ex.Message}");
            SetStatus(new(PreviewStatusKind.Error, "Could not start the previewer: " + ex.Message, CanBuild: true));
            return false;
        }
    }

    void OnXamlResult(PreviewXamlResult r)
    {
        var path = _pendingPath;
        if (r.Error is null)
        {
            SetStatus(new(PreviewStatusKind.Running, $"Updated · {(long)_updateClock.Elapsed.TotalMilliseconds} ms"));
            if (path is not null) DiagnosticsChanged?.Invoke(path, null);
            return;
        }
        var message = PreviewErrorText.Summary(r.Error);
        SetStatus(new(PreviewStatusKind.Running, $"XAML error · {(long)_updateClock.Elapsed.TotalMilliseconds} ms", XamlError: message, ErrorLine: _pendingEdit.MapBack(r.Line, r.Column).Line, ErrorColumn: _pendingEdit.MapBack(r.Line, r.Column).Column));
        var (line, column) = _pendingEdit.MapBack(r.Line, r.Column);
        if (path is not null) DiagnosticsChanged?.Invoke(path, new PreviewDiagnostic(path, line, column, message));
    }

    async Task SendViewportAsync()
    {
        var s = _session;
        if (s is null || s.HasExited) return;
        double w, h, dx, dy;
        lock (_state) { w = _vw; h = _vh; dx = _dpiX; dy = _dpiY; }
        await s.SetViewportAsync(w, h, dx, dy).ConfigureAwait(false);
    }

    /// <summary>A rebuild of the app (its assembly changes) re-renders the active file; EnsureSession restarts the host.</summary>
    void WatchTarget(PreviewProject project)
    {
        _targetWatcher?.Dispose();
        _targetWatcher = null;
        try
        {
            var w = new FileSystemWatcher(Path.GetDirectoryName(project.TargetPath)!, Path.GetFileName(project.TargetPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            void OnChange(object? s, FileSystemEventArgs e) => _debounce.Schedule(RebuildKey, _options.RebuildDelay);
            w.Changed += OnChange;
            w.Created += OnChange;
            w.Renamed += (s, e) => OnChange(s, e);
            _targetWatcher = w;
        }
        catch (Exception ex) { _options.Log?.Invoke($"Not watching the build output: {ex.Message}"); }
    }

    async Task StopSessionAsync()
    {
        _targetWatcher?.Dispose();
        _targetWatcher = null;
        var s = _session;
        _session = null;
        _pendingPath = null;
        if (s is not null) await s.DisposeAsync().ConfigureAwait(false);
    }

    void SetStatus(PreviewStatus status)
    {
        lock (_state)
        {
            if (_status == status) return;
            _status = status;
        }
        StatusChanged?.Invoke(status);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_state)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _debounce.Dispose();
        _cts.Cancel();
        await StopSessionAsync().ConfigureAwait(false);
        await _shadow.DeleteAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
