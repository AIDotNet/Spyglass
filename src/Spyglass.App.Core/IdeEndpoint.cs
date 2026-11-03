using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;
using Spyglass.XamlLanguage;

namespace Spyglass.App.Core;

/// <summary>A connected IDE extension.</summary>
public sealed class IdeConnection
{
    readonly JsonRpcPeer _peer;
    internal IdeConnection(IdeHelloParams hello, JsonRpcPeer peer) { Hello = hello; _peer = peer; }
    public IdeHelloParams Hello { get; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.Now;
    internal readonly ConcurrentDictionary<string, long> Open = new(HotReloadCoordinator.PathComparer);
    internal JsonRpcPeer Peer => _peer;

    /// <summary>True when the file is under one of the IDE's workspace folders.</summary>
    public bool Owns(string path) => Hello.WorkspaceFolders.Any(f =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(f) + Path.DirectorySeparatorChar, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// ws://127.0.0.1:PORT/ide for IDE extensions (design/hot-reload/README.md §7.1, §10): unsaved XAML buffers in, diagnostics,
/// status and "reveal in editor" out. Hot reload executes code in the app, so this endpoint only accepts loopback
/// connections without an Origin header (no browser pages) and a token the hub writes to ~/.spyglass/hub-PORT.json (mode 600).
/// </summary>
public sealed class IdeEndpoint : IAsyncDisposable
{
    readonly SpyglassHub _hub;
    readonly ConcurrentDictionary<IdeConnection, byte> _ides = new();
    string? _tokenFile;
    int _statusQueued;
    readonly XamlLanguageService _xaml = new();

    internal IdeEndpoint(SpyglassHub hub)
    {
        _hub = hub;
        hub.HotReload.Changed += QueueStatus;
        hub.HotReload.Reloaded += PushDiagnostics;
    }

    /// <summary>Secret an IDE must present in ide/hello.</summary>
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public IReadOnlyList<IdeConnection> Connections => _ides.Keys.OrderBy(i => i.ConnectedAt).ToList();
    public event Action? ConnectionsChanged;

    /// <summary>~/.spyglass (or SPYGLASS_HOME).</summary>
    public static string HomeDirectory =>
        Environment.GetEnvironmentVariable("SPYGLASS_HOME") is { Length: > 0 } h ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".spyglass");

    public static string TokenFilePath(int port) => Path.Combine(HomeDirectory, $"hub-{port}.json");

    internal void WriteTokenFile(int port)
    {
        try
        {
            Directory.CreateDirectory(HomeDirectory);
            var path = TokenFilePath(port);
            var json = JsonSerializer.Serialize(new Dictionary<string, object> { ["port"] = port, ["token"] = Token, ["pid"] = Environment.ProcessId, ["version"] = SpyglassProtocol.Version });
            // Create with owner-only permissions before writing the secret.
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var fs = new FileStream(path, options))
            using (var w = new StreamWriter(fs)) w.Write(json);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _tokenFile = path;
        }
        catch (Exception ex) { Console.Error.WriteLine($"spyglass: cannot write {TokenFilePath(port)}: {ex.Message}; IDE extensions cannot connect."); }
    }

    /// <summary>
    /// Asks a connected IDE to open the file at the position (the one whose workspace contains it, else the most recent).
    /// False when no IDE is connected or it failed.
    /// </summary>
    public async Task<bool> RevealAsync(string path, int line, int column, CancellationToken ct = default)
    {
        var ide = Connections.LastOrDefault(i => i.Owns(path)) ?? Connections.LastOrDefault();
        if (ide is null) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var r = await ide.Peer.RequestAsync<EditorRevealParams, EditorRevealResult>(Methods.EditorReveal, new EditorRevealParams(path, line, column), timeout.Token).ConfigureAwait(false);
            return r.Ok;
        }
        catch { return false; }
    }

    /// <summary>True when some IDE workspace contains the file (the GUI then reveals through it instead of starting an editor).</summary>
    public bool HasEditorFor(string path) => Connections.Any(i => i.Owns(path));

    internal async Task HandleAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("Spyglass IDE endpoint: WebSocket only.");
            return;
        }
        // Browsers always send Origin; IDE extensions (Node) do not. Never accept a web page, never accept the network.
        if (ctx.Request.Headers.Origin.Count > 0 || ctx.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        await using var peer = new JsonRpcPeer(ws);
        IdeConnection? ide = null;

        peer.Handle<IdeHelloParams, IdeHelloResult>(Methods.IdeHello, (hello, ct) =>
        {
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(hello.Token ?? ""), System.Text.Encoding.UTF8.GetBytes(Token)))
                throw new SpyglassProtocolException(ErrorCodes.Unauthorized, $"Wrong token; read it from {TokenFilePath(_hub.Port)}.");
            var conn = new IdeConnection(hello, peer);
            ide = conn;
            _ides[conn] = 0;
            ConnectionsChanged?.Invoke();
            _ = SendStatusSoonAsync(conn);   // after the hello response is on the wire
            return Task.FromResult(new IdeHelloResult(SpyglassProtocol.Version, _hub.Port, [Capabilities.HotReload, Capabilities.XamlLanguage]));
        });
        // Avalonia XAML IntelliSense (design/xaml-language/README.md): any .axaml the IDE has, hot-reloadable or not.
        peer.OnNotification<XamlPrepareParams>(Methods.XamlPrepare, p => { if (ide is not null) _xaml.Prepare(p.Path); });
        peer.Handle<XamlCompletionParams, XamlCompletionResult>(Methods.XamlCompletion, (p, ct) => { Authorized(ide); return _xaml.CompleteAsync(p, ct); });
        peer.Handle<XamlHoverParams, XamlHoverResult>(Methods.XamlHover, (p, ct) => { Authorized(ide); return _xaml.HoverAsync(p, ct); });
        peer.Handle<XamlDocumentationParams, XamlDocumentationResult>(Methods.XamlDocumentation, (p, ct) => { Authorized(ide); return _xaml.DocumentationAsync(p, ct); });
        peer.OnNotification<XamlDocumentParams>(Methods.XamlDidOpen, p =>
        {
            if (ide is null) return;
            var path = HotReloadCoordinator.Normalize(p.Path);
            if (ide.Open.TryAdd(path, p.Version)) _hub.HotReload.ClaimForEditor(path);
        });
        peer.OnNotification<XamlDocumentParams>(Methods.XamlDidChange, p =>
        {
            if (ide is null || p.Text is null) return;
            var path = HotReloadCoordinator.Normalize(p.Path);
            if (ide.Open.TryAdd(path, p.Version)) _hub.HotReload.ClaimForEditor(path);
            ide.Open[path] = p.Version;
            _ = _hub.HotReload.ApplyTextAsync(path, p.Text, p.Version);
        });
        peer.OnNotification<XamlDocumentParams>(Methods.XamlDidClose, p =>
        {
            if (ide is null) return;
            var path = HotReloadCoordinator.Normalize(p.Path);
            if (ide.Open.TryRemove(path, out _)) _hub.HotReload.ReleaseFromEditor(path);
        });

        try { await peer.RunAsync(ctx.RequestAborted); }
        finally
        {
            if (ide is not null && _ides.TryRemove(ide, out _))
            {
                foreach (var path in ide.Open.Keys) _hub.HotReload.ReleaseFromEditor(path);
                ConnectionsChanged?.Invoke();
            }
        }
    }

    static void Authorized(IdeConnection? ide)
    {
        if (ide is null) throw new SpyglassProtocolException(ErrorCodes.Unauthorized, "Send ide/hello first.");
    }

    // ---------- push ----------

    void PushDiagnostics(HotReloadEvent e)
    {
        if (e.Path == HotReloadCoordinator.CodePath) { QueueStatus(); return; }
        var items = e.Result.Ok ? e.Result.Diagnostics.Where(d => d.Severity != XamlDiagnosticSeverity.Info).ToList() : e.Result.Diagnostics.ToList();
        var p = new XamlDiagnosticsParams(e.Path, e.Result.Version, items, Describe(e));
        // To the IDEs that have the file open or in their workspace (Rider/VS connect once per project); nobody: all of them.
        var targets = _ides.Keys.Where(i => i.Open.ContainsKey(e.Path) || i.Owns(e.Path)).ToList();
        foreach (var ide in targets.Count > 0 ? targets : _ides.Keys.ToList()) _ = ide.Peer.NotifyAsync(Methods.XamlDiagnostics, p);
    }

    void QueueStatus()
    {
        if (Interlocked.Exchange(ref _statusQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(150).ConfigureAwait(false);
            Volatile.Write(ref _statusQueued, 0);
            foreach (var ide in _ides.Keys) await SendStatusAsync(ide).ConfigureAwait(false);
        });
    }

    async Task SendStatusSoonAsync(IdeConnection ide)
    {
        await Task.Delay(50).ConfigureAwait(false);
        await SendStatusAsync(ide).ConfigureAwait(false);
    }

    Task SendStatusAsync(IdeConnection ide)
    {
        var apps = _hub.Sessions.Where(s => s.IsConnected).Select(s => new HotReloadAppStatus(
            s.ShortId, s.App.Name, s.App.Pid, s.Has(Capabilities.HotReload),
            s.HotReload?.Available == true, s.HotReload?.Reason, s.HotReload?.Documents.Count ?? 0)).ToList();
        var last = _hub.HotReload.History.LastOrDefault();
        return ide.Peer.NotifyAsync(Methods.HotReloadStatus,
            new HotReloadStatusParams(apps, _hub.HotReload.Files(), last is null ? null : Describe(last), last?.Result.Ok ?? true));
    }

    /// <summary>"⚡ MainView.axaml → Sample: 2 instance(s), 9 ms", "✗ MainView.axaml:8:33 → Sample: …", "✓ … already live".</summary>
    public static string Describe(HotReloadEvent e)
    {
        var file = Path.GetFileName(e.Path);
        var r = e.Result;
        if (r.Source == HotReloadSource.Code)
            return $"⚡ C# → {e.App}: {r.Diagnostics.FirstOrDefault()?.Message ?? "updated"}{(r.Instances > 0 ? $", {r.Instances} binding scope(s) refreshed" : "")}"
                   + (r.Diagnostics.Any(d => d.Severity == XamlDiagnosticSeverity.Warning) ? " ⚠ restart recommended" : "");
        if (r.Ok && r.Skipped is not null) return $"✓ {file} → {e.App}: already live ({r.Skipped})";
        if (r.Ok) return $"⚡ {file} → {e.App}: {r.Instances} instance(s), {r.Ms:0} ms";
        var d = r.Diagnostics.FirstOrDefault(x => x.Severity == XamlDiagnosticSeverity.Error) ?? r.Diagnostics.FirstOrDefault();
        var at = d?.Line is { } l ? $":{l}{(d.Column is { } c ? $":{c}" : "")}" : "";
        var tail = r.RestartRequired ? " (restart the app)" : r.RolledBack ? " (rolled back)" : "";
        return $"✗ {file}{at} → {e.App}: {d?.Message ?? "failed"}{tail}";
    }

    public ValueTask DisposeAsync()
    {
        _xaml.Dispose();
        if (_tokenFile is not null) try { File.Delete(_tokenFile); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }
}
