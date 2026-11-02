using System.Diagnostics;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Spyglass.Agent.Internal;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Agent;

/// <summary>
/// Process-wide agent. Connects out to the Spyglass client (design/protocol §1, §3) and reconnects with
/// exponential backoff (2 s → 10 s). Each connection is a new session.
/// </summary>
public sealed class SpyglassAgent
{
    public static SpyglassAgent? Current { get; private set; }

    public SpyglassAgentOptions Options { get; }
    public IReadOnlyList<string> Capabilities { get; }

    /// <summary>True while a client session is established.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>Raised (on a background thread) when a session starts or ends.</summary>
    public event Action<bool>? ConnectionChanged;

    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _wake = new(0, 1);
    AgentConnection? _connection;
    DateTime _fastRetryUntil;
    bool _pendingInspect;
    bool _pendingActivate;

    SpyglassAgent(SpyglassAgentOptions options)
    {
        Options = options;
        var caps = new List<string>
        {
            Protocol.Capabilities.Tree, Protocol.Capabilities.Props, Protocol.Capabilities.Pick,
            Protocol.Capabilities.Highlight, Protocol.Capabilities.Screenshot, Protocol.Capabilities.PseudoClasses,
            Protocol.Capabilities.BindingErrors, Protocol.Capabilities.Network, Protocol.Capabilities.Logs, Protocol.Capabilities.Events,
            Protocol.Capabilities.Resources, Protocol.Capabilities.Input,
        };
        caps.Add(Protocol.Capabilities.Overlays);
        if (Internal.PerfMonitor.DiagnosticsSwitch) caps.AddRange([Protocol.Capabilities.Metrics, Protocol.Capabilities.Activities]);
        if (IsDebugBuild()) caps.Add(Protocol.Capabilities.XamlSource);
        caps.AddRange(options.Features.SelectMany(f => f.Capabilities));
        Capabilities = caps.Distinct().ToList();
    }

    static readonly List<IAgentFeature> GlobalFeatures = [];

    /// <summary>
    /// Registers a feature for every agent, without the app calling <c>AddFeature</c>. Used by Spyglass packages whose
    /// generated module initializer runs before <c>Main</c> (Spyglass.Agent.HotReload). Takes effect for agents started later.
    /// </summary>
    internal static void AddGlobalFeature(IAgentFeature feature)
    {
        lock (GlobalFeatures)
            if (!GlobalFeatures.Contains(feature)) GlobalFeatures.Add(feature);
    }

    internal static void Start(SpyglassAgentOptions options)
    {
        if (Current is not null) return;
        lock (GlobalFeatures)
            foreach (var f in GlobalFeatures)
                if (!options.Features.Contains(f)) options.Features.Add(f);
        BindingErrorCollector.Install();
        LogCollector.Install();   // wraps the binding error sink; both chain to the app's original sink
        OpenWindows.Install();
        Current = new SpyglassAgent(options);
        Hotkeys.Install(Current);
        _ = Task.Run(() => Current.ConnectLoopAsync(Current._cts.Token));
    }

    /// <summary>Stops reconnecting and closes the current session (used by tests and app shutdown).</summary>
    public static void Stop()
    {
        Current?._cts.Cancel();
        Current = null;
    }

    /// <summary>Requests to the Spyglass client itself (agent socket, MCP) are never shown in the Network panel.</summary>
    bool IsSpyglassTraffic(Uri uri) =>
        uri.Port == Options.Port && (uri.Host == Options.Host || (ClientLauncher.IsLoopback(Options.Host) && uri.IsLoopback));

    Uri Endpoint => new($"ws://{Options.Host}:{Options.Port}{SpyglassProtocol.AgentPath}");

    async Task ConnectLoopAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        var unauthorized = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(Endpoint, ct).ConfigureAwait(false);
                await using var connection = new AgentConnection(ws, Options.Features, IsSpyglassTraffic);
                var loop = connection.Peer.RunAsync(ct);
                await connection.SendHelloAsync(BuildHello()).ConfigureAwait(false);
                _connection = connection;
                SetConnected(true);
                delay = TimeSpan.FromSeconds(2);
                await RunPendingHotkeyAsync(connection).ConfigureAwait(false);
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (SpyglassProtocolException ex) when (ex.Code == ErrorCodes.Unauthorized)
            {
                // Retrying cannot help (the code is fixed in this process) and would feed the client's throttle:
                // say so once, then only try again when the user presses the open hotkey.
                Debug.WriteLine($"[Spyglass] {Endpoint} rejected the pairing code: {ex.Message} Not retrying; restart the app with the right PairingCode / SPYGLASS_PAIRING_CODE.");
                unauthorized = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Spyglass] connection to {Endpoint} failed: {ex.Message}");
            }
            finally
            {
                _connection = null;
                SetConnected(false);
            }

            // Retry quickly right after a hotkey started the client; otherwise back off. A hotkey also wakes us up.
            var wait = unauthorized ? Timeout.InfiniteTimeSpan : DateTime.UtcNow < _fastRetryUntil ? TimeSpan.FromMilliseconds(500) : delay;
            unauthorized = false;
            try { await _wake.WaitAsync(wait, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 10));
        }
    }

    // ---------- hotkeys (design/ux/03 §1) ----------

    /// <summary>
    /// Open (or inspect) hotkey pressed. Connected: bring the client to the front, or start picking.
    /// Not connected: connect now if the client is running, otherwise start it, then do the same once connected.
    /// UI thread.
    /// </summary>
    internal void RequestOpen(TopLevel origin, bool inspect)
    {
        if (_connection is { } c)
        {
            if (inspect) c.StartPickFromApp();
            else _ = c.Peer.NotifyAsync(Methods.SessionActivate, new ActivateParams("hotkey"));
            return;
        }
        if (inspect) _pendingInspect = true;
        else _pendingActivate = true;
        _ = Task.Run(() => EnsureClient(origin));
    }

    void EnsureClient(TopLevel origin)
    {
        void Toast(string message, bool warning = false) =>
            Dispatcher.UIThread.Post(() => AgentToast.Show(origin, message, warning));

        if (ClientLauncher.IsListening(Options.Host, Options.Port))
        {
            Toast("Connecting…");
            Wake();
            return;
        }
        if (!ClientLauncher.IsLoopback(Options.Host) || !Options.LaunchClient)
        {
            Toast($"Spyglass is not running at {Options.Host}:{Options.Port}. Start it to inspect this app.", warning: true);
            return;
        }
        var path = ClientLauncher.Resolve(Options.ClientPath);
        if (path is null)
        {
            Toast("Spyglass is not running and was not found. Install it with `dotnet tool install -g TKCode.Spyglass`, or set SPYGLASS_CLIENT to its path.", warning: true);
            return;
        }
        if (!ClientLauncher.TryStart(path, out var error))
        {
            Toast($"Could not start Spyglass ({path}): {error}", warning: true);
            return;
        }
        Toast("Starting Spyglass…");
        _fastRetryUntil = DateTime.UtcNow.AddSeconds(20);
        Wake();
    }

    void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    async Task RunPendingHotkeyAsync(AgentConnection connection)
    {
        if (_pendingInspect)
        {
            _pendingInspect = false;
            await Dispatcher.UIThread.InvokeAsync(connection.StartPickFromApp);
        }
        else if (_pendingActivate)
        {
            _pendingActivate = false;
            await connection.Peer.NotifyAsync(Methods.SessionActivate, new ActivateParams("hotkey")).ConfigureAwait(false);
        }
        Dispatcher.UIThread.Post(AgentToast.Hide);
    }

    void SetConnected(bool value)
    {
        if (IsConnected == value) return;
        IsConnected = value;
        ConnectionChanged?.Invoke(value);
    }

    HelloParams BuildHello()
    {
        using var process = Process.GetCurrentProcess();
        var exe = Environment.ProcessPath ?? process.ProcessName;
        var lifetime = Application.Current?.ApplicationLifetime switch
        {
            IClassicDesktopStyleApplicationLifetime => AppLifetimeKind.ClassicDesktop,
            ISingleViewApplicationLifetime => AppLifetimeKind.SingleView,
            _ => AppLifetimeKind.Other,
        };
        var av = typeof(AvaloniaObject).Assembly.GetName().Version;
        return new HelloParams(
            SpyglassProtocol.Version,
            new AgentInfo("Spyglass.Agent", typeof(SpyglassAgent).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                Options.Features.Any(f => f.IsAdvanced) ? AgentFlavor.Advanced : AgentFlavor.Basic),
            new AppInfo(
                Options.ApplicationName ?? Assembly.GetEntryAssembly()?.GetName().Name ?? process.ProcessName,
                Environment.ProcessId,
                $"{exe}|{process.StartTime.ToUniversalTime():O}",
                av is null ? "?" : $"{av.Major}.{av.Minor}.{av.Build}",
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                lifetime,
                IsDebugBuild()),
            Capabilities,
            Options.PairingCode);
    }

    static bool IsDebugBuild() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<DebuggableAttribute>()?.IsJITTrackingEnabled == true;
}
