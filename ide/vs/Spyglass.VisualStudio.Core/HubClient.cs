using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;

namespace Spyglass.VisualStudio.Core;

public enum HubConnectionState { Disconnected, Starting, Connecting, Connected }

public sealed class HubClientOptions
{
    /// <summary>Hub port; 0 = <c>SPYGLASS_PORT</c> or 29500.</summary>
    public int Port { get; init; }

    /// <summary>Where the hub writes hub-PORT.json; null = <c>SPYGLASS_HOME</c> or ~/.spyglass.</summary>
    public string? Home { get; init; }

    /// <summary>Sent in ide/hello ("vs").</summary>
    public string Ide { get; init; } = "vs";

    public string Version { get; init; } = "0";

    /// <summary>Folders the IDE has open; the hub sends editor/reveal for files under them to this IDE.</summary>
    public Func<IReadOnlyList<string>> WorkspaceFolders { get; init; } = () => [];

    /// <summary><c>spyglass</c>, a path to the executable, or a path to Spyglass.dll (run with <c>dotnet</c>).</summary>
    public string Command { get; init; } = "spyglass";

    /// <summary>Start <c>spyglass serve</c> when nothing listens on the port.</summary>
    public bool AutoStart { get; init; } = true;

    public Action<string> Log { get; init; } = _ => { };

    /// <summary>First reconnect delay; doubles up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait for a started <c>spyglass serve</c> to write its token file.</summary>
    public TimeSpan ServeStartTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Connection to the Spyglass hub's /ide endpoint (design/hot-reload/README.md §7.1, §8, §10), mirroring the VS Code
/// extension's HubClient: reads the hub's token from ~/.spyglass/hub-PORT.json, starts <c>spyglass serve</c> when nothing
/// listens (and <see cref="HubClientOptions.AutoStart"/>), reconnects with backoff (1 s → 10 s), and exposes the hub's
/// pushes as events. Events are raised on thread-pool threads.
/// </summary>
public sealed class HubClient : IAsyncDisposable
{
    readonly HubClientOptions _options;
    readonly object _gate = new();
    JsonRpcPeer? _peer;
    bool _stopped = true;
    bool _connecting;
    TimeSpan _retry;
    Timer? _retryTimer;
    DateTime _lastServeStart = DateTime.MinValue;
    HashSet<string> _files = new(StringComparer.Ordinal);

    public HubClient(HubClientOptions options)
    {
        _options = options;
        _retry = options.RetryDelay;
    }

    public HubConnectionState State { get; private set; } = HubConnectionState.Disconnected;

    /// <summary>The newest hotReload/status; null while disconnected.</summary>
    public HotReloadStatusParams? Status { get; private set; }

    public event Action<HubConnectionState>? StateChanged;

    /// <summary>A new ide/hello succeeded (also after a reconnect): announce open documents again.</summary>
    public event Action? Connected;

    /// <summary>hotReload/status; null when the connection ended.</summary>
    public event Action<HotReloadStatusParams?>? StatusChanged;

    public event Action<XamlDiagnosticsParams>? DiagnosticsReceived;

    /// <summary>Handles editor/reveal; returns true when the file was shown. Without a handler the hub gets <c>{ok:false}</c>.</summary>
    public Func<EditorRevealParams, CancellationToken, Task<bool>>? RevealHandler { get; set; }

    public int Port => _options.Port > 0 ? _options.Port : DefaultPort();

    public string TokenFile => Path.Combine(_options.Home ?? DefaultHome(), $"hub-{Port}.json");

    public bool IsConnected => State == HubConnectionState.Connected && _peer?.IsOpen == true;

    public static int DefaultPort() =>
        int.TryParse(Environment.GetEnvironmentVariable("SPYGLASS_PORT"), out var p) && p is > 0 and < 65536 ? p : 29500;

    public static string DefaultHome() =>
        Environment.GetEnvironmentVariable("SPYGLASS_HOME") is { Length: > 0 } h ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".spyglass");

    /// <summary>Paths as the hub compares them: case-insensitive on Windows and macOS.</summary>
    public static string PathKey(string path)
    {
        var full = Path.GetFullPath(path);
        return OperatingSystem.IsLinux() ? full : full.ToLowerInvariant();
    }

    /// <summary>True when some connected app can hot-reload this file (it is in hotReload/status.files).</summary>
    public bool CanReload(string path)
    {
        lock (_gate) return _files.Contains(PathKey(path));
    }

    public void Start()
    {
        lock (_gate) _stopped = false;
        _ = ConnectLoopAsync();
    }

    public async Task StopAsync()
    {
        JsonRpcPeer? peer;
        lock (_gate)
        {
            _stopped = true;
            _retryTimer?.Dispose();
            _retryTimer = null;
            peer = _peer;
        }
        if (peer is not null) await peer.CloseAsync().ConfigureAwait(false);
        SetState(HubConnectionState.Disconnected);
    }

    /// <summary>Drops the connection (if any) and connects again now.</summary>
    public void Reconnect()
    {
        JsonRpcPeer? peer;
        lock (_gate)
        {
            _stopped = false;
            _retryTimer?.Dispose();
            _retryTimer = null;
            _retry = _options.RetryDelay;
            peer = _peer;
        }
        if (peer?.IsOpen == true) _ = peer.CloseAsync();   // its Closed handler schedules the reconnect
        else _ = ConnectLoopAsync();
    }

    public void DidOpen(string path, long version, string text) => _peer?.Notify(IdeMethods.DidOpen, new XamlDocumentParams(path, version, text));

    public void DidChange(string path, long version, string text) => _peer?.Notify(IdeMethods.DidChange, new XamlDocumentParams(path, version, text));

    public void DidClose(string path) => _peer?.Notify(IdeMethods.DidClose, new XamlDocumentParams(path, 0));

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        JsonRpcPeer? peer;
        lock (_gate) peer = _peer;
        if (peer is not null) await peer.DisposeAsync().ConfigureAwait(false);
    }

    // ---------- connection ----------

    void SetState(HubConnectionState s)
    {
        lock (_gate)
        {
            if (State == s) return;
            State = s;
        }
        StateChanged?.Invoke(s);
    }

    void ScheduleRetry(bool backoff)
    {
        lock (_gate)
        {
            if (_stopped) return;
            var delay = _retry;
            if (backoff) _retry = TimeSpan.FromTicks(Math.Min(_retry.Ticks * 2, _options.MaxRetryDelay.Ticks));
            _retryTimer?.Dispose();
            _retryTimer = new Timer(_ => _ = ConnectLoopAsync(), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    async Task ConnectLoopAsync()
    {
        lock (_gate)
        {
            if (_stopped || _connecting || State == HubConnectionState.Connected) return;
            _connecting = true;
        }
        try
        {
            await ConnectOnceAsync().ConfigureAwait(false);
            lock (_gate) _retry = _options.RetryDelay;
        }
        catch (Exception ex)
        {
            SetState(HubConnectionState.Disconnected);
            if (ex is JsonRpcException rpc) _options.Log($"Spyglass refused the connection: {rpc.Message}");
            lock (_gate) _connecting = false;
            ScheduleRetry(backoff: true);
            return;
        }
        lock (_gate) _connecting = false;
    }

    async Task ConnectOnceAsync()
    {
        SetState(HubConnectionState.Connecting);
        var token = ReadToken();
        ClientWebSocket socket;
        try
        {
            if (token is null) throw new NoHubException();
            socket = await OpenAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NoHubException || IsConnectionRefused(ex))
        {
            // Nothing listens (no token file, or a stale one from a hub that exited): start one if allowed, at most every 30 s.
            if (!_options.AutoStart || DateTime.UtcNow - _lastServeStart < TimeSpan.FromSeconds(30)) throw;
            StartServe();
            token = await WaitForTokenAsync(token).ConfigureAwait(false);
            socket = await OpenAsync().ConfigureAwait(false);
        }

        var peer = new JsonRpcPeer(socket);
        peer.OnNotification<HotReloadStatusParams>(IdeMethods.Status, s =>
        {
            lock (_gate)
            {
                if (_peer != peer) return;
                Status = s;
                _files = new HashSet<string>(s.Files.Select(PathKey), StringComparer.Ordinal);
            }
            StatusChanged?.Invoke(s);
        });
        peer.OnNotification<XamlDiagnosticsParams>(IdeMethods.Diagnostics, d => DiagnosticsReceived?.Invoke(d));
        peer.OnRequest<EditorRevealParams, EditorRevealResult>(IdeMethods.Reveal, async (r, ct) =>
        {
            var handler = RevealHandler;
            if (handler is null) return new EditorRevealResult(false);
            try { return new EditorRevealResult(await handler(r, ct).ConfigureAwait(false)); }
            catch (Exception ex)
            {
                _options.Log($"Cannot open {r.Path}: {ex.Message}");
                return new EditorRevealResult(false);
            }
        });
        peer.Closed += () => OnClosed(peer);
        lock (_gate) _peer = peer;   // status may arrive right after the hello response
        _ = peer.RunAsync();

        try
        {
            await peer.RequestAsync<IdeHelloResult>(IdeMethods.Hello,
                new IdeHelloParams(_options.Ide, _options.Version, _options.WorkspaceFolders(), token!)).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate) if (_peer == peer) _peer = null;
            await peer.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        SetState(HubConnectionState.Connected);
        _options.Log($"Connected to Spyglass on port {Port}.");
        Connected?.Invoke();
    }

    void OnClosed(JsonRpcPeer peer)
    {
        bool wasCurrent;
        lock (_gate)
        {
            wasCurrent = _peer == peer;
            if (wasCurrent)
            {
                _peer = null;
                _files = new HashSet<string>(StringComparer.Ordinal);
                Status = null;
            }
        }
        _ = peer.DisposeAsync().AsTask();
        if (!wasCurrent) return;   // a failed hello: ConnectLoopAsync retries
        SetState(HubConnectionState.Disconnected);
        StatusChanged?.Invoke(null);
        _options.Log("Disconnected from Spyglass.");
        ScheduleRetry(backoff: false);
    }

    async Task<ClientWebSocket> OpenAsync()
    {
        var ws = new ClientWebSocket();   // sends no Origin header: the hub refuses browser pages
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}{IdeMethods.Path}"), cts.Token).ConfigureAwait(false);
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    internal static bool IsConnectionRefused(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is SocketException { SocketErrorCode: SocketError.ConnectionRefused }) return true;
        return false;
    }

    sealed class NoHubException() : Exception("No Spyglass hub is running.");

    internal string? ReadToken()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(TokenFile));
            return doc.RootElement.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        }
        catch { return null; }
    }

    async Task<string> WaitForTokenAsync(string? stale)
    {
        var until = DateTime.UtcNow + _options.ServeStartTimeout;
        while (DateTime.UtcNow < until)
        {
            if (ReadToken() is { } t && t != stale) return t;
            await Task.Delay(200).ConfigureAwait(false);
        }
        return ReadToken() ?? throw new TimeoutException($"spyglass serve did not start (no {TokenFile}).");
    }

    /// <summary>The process that runs <c>spyglass serve</c>: a <c>.dll</c> runs with <c>dotnet</c>.</summary>
    public static (string FileName, IReadOnlyList<string> Arguments) ServeCommand(string command, int port)
    {
        command = string.IsNullOrWhiteSpace(command) ? "spyglass" : command.Trim();
        var serve = new[] { "serve", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--quiet" };
        return command.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? ("dotnet", [command, .. serve])
            : (command, serve);
    }

    void StartServe()
    {
        _lastServeStart = DateTime.UtcNow;
        SetState(HubConnectionState.Starting);
        var (file, args) = ServeCommand(_options.Command, Port);
        _options.Log($"Starting {file} {string.Join(" ", args)}");
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (_options.Home is not null) psi.Environment["SPYGLASS_HOME"] = _options.Home;
        try
        {
            // Not awaited and not killed with Visual Studio: the hub outlives the IDE (other IDEs, the GUI, MCP use it).
            using var p = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _options.Log(ex is System.ComponentModel.Win32Exception
                ? $"Cannot start Spyglass: '{file}' not found. Install it with `dotnet tool install -g Spyglass`, or set the Spyglass command in Tools › Options."
                : $"Cannot start Spyglass: {ex.Message}");
        }
    }
}
