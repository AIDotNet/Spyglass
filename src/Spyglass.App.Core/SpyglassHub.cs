using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spyglass.App.Core.Lan;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.App.Core;

public sealed class SpyglassHubOptions
{
    public IPAddress Address { get; set; } = IPAddress.Loopback;
    /// <summary>0 picks a free port (tests).</summary>
    public int Port { get; set; } = SpyglassProtocol.Port;
    /// <summary>Extra services, e.g. the MCP server (Spyglass.Mcp).</summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }
    /// <summary>Extra endpoints, e.g. <c>app.MapMcp("/mcp")</c>.</summary>
    public Action<WebApplication>? ConfigureApp { get; set; }
    /// <summary>LAN policy (design/protocol §6). Default: loopback only. <c>new LanGate(true)</c> accepts paired agents from other machines on /agent.</summary>
    public LanGate Lan { get; set; } = new();
}

/// <summary>
/// The listener agents connect to (design/protocol §2–3): ws://127.0.0.1:29500/agent. Hosts other endpoints
/// (MCP at /mcp) on the same port. Thread-safe; events are raised on background threads.
/// </summary>
public sealed class SpyglassHub : IAsyncDisposable
{
    readonly SpyglassHubOptions _options;
    readonly ConcurrentDictionary<string, AgentSession> _sessions = new();
    WebApplication? _app;

    public SpyglassHub(SpyglassHubOptions? options = null)
    {
        _options = options ?? new SpyglassHubOptions();
        HotReload = new HotReloadCoordinator(this);
        Ide = new IdeEndpoint(this);
    }

    /// <summary>IDE extensions (ws://…/ide): unsaved XAML buffers, diagnostics, reveal in editor.</summary>
    public IdeEndpoint Ide { get; }

    /// <summary>File watching and routing for XAML hot reload (design/hot-reload/README.md §2).</summary>
    public HotReloadCoordinator HotReload { get; }

    public int Port { get; private set; }
    public LanGate Lan => _options.Lan;
    public Uri BaseUri => new($"http://{_options.Address}:{Port}/");

    public IReadOnlyList<AgentSession> Sessions => _sessions.Values.OrderBy(s => s.ConnectedAt).ToList();

    /// <summary>Most recently connected session, or null.</summary>
    public AgentSession? Current => _sessions.Values.OrderByDescending(s => s.ConnectedAt).FirstOrDefault();

    public event Action<AgentSession>? SessionConnected;
    public event Action<AgentSession>? SessionDisconnected;

    public async Task StartAsync(CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k => k.Listen(_options.Lan.AllowLan ? IPAddress.Any : _options.Address, _options.Port));
        builder.Services.AddSingleton(this);
        _options.ConfigureServices?.Invoke(builder.Services);

        var app = builder.Build();
        _options.Lan.Install(app);
        app.UseWebSockets();
        app.Map(SpyglassProtocol.AgentPath, HandleAgentAsync);
        app.Map(SpyglassProtocol.IdePath, Ide.HandleAsync);
        _options.ConfigureApp?.Invoke(app);

        await app.StartAsync(ct).ConfigureAwait(false);
        _app = app;
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Port = new Uri(address).Port;
        Ide.WriteTokenFile(Port);
    }

    /// <summary>Waits until at least one agent is connected.</summary>
    public async Task<AgentSession> WaitForSessionAsync(TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<AgentSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnConnected(AgentSession s) => tcs.TrySetResult(s);
        SessionConnected += OnConnected;
        try
        {
            if (Current is { } existing) return existing;
            return await tcs.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        finally { SessionConnected -= OnConnected; }
    }

    async Task HandleAgentAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("Spyglass agent endpoint: WebSocket only.");
            return;
        }

        using var admission = _options.Lan.Admit(ctx.Connection.RemoteIpAddress, ctx.RequestAborted);
        if (admission.Refusal is { } refused) { ctx.Response.StatusCode = refused; return; }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        await using var peer = new JsonRpcPeer(ws);
        AgentSession? session = null;

        peer.Handle<HelloParams, HelloResult>(Methods.SessionHello, (hello, _) =>
        {
            admission.Authorize(hello.PairingCode);
            if (!SpyglassProtocol.IsCompatible(hello.ProtocolVersion))
                throw new SpyglassProtocolException(ErrorCodes.VersionMismatch,
                    $"Agent speaks protocol {hello.ProtocolVersion}; this Spyglass supports {SpyglassProtocol.Version}. Update the older side.");
            session = new AgentSession(Guid.NewGuid().ToString("n"), hello, peer);
            _sessions[session.SessionId] = session;
            SessionConnected?.Invoke(session);
            return Task.FromResult(new HelloResult(session.SessionId, SpyglassProtocol.Version));
        });
        peer.OnNotification<PickHoverParams>(Methods.PickHover, p => session?.RaisePickHover(p.Id));
        peer.OnNotification<TreeChangedParams>(Methods.TreeChanged, p => session?.RaiseTreeChanged(p));
        peer.OnNotification<ActivateParams>(Methods.SessionActivate, p => session?.RaiseActivate(p));
        peer.OnNotification<Empty>(Methods.PickStarted, _ => session?.RaisePickStarted());
        peer.OnNotification<BindingErrorsParams>(Methods.BindingsError, p => session?.RaiseBindingErrors(p.Errors));
        foreach (var m in new[] { Methods.NetworkRequestStarted, Methods.NetworkResponseReceived, Methods.NetworkFinished, Methods.NetworkFailed })
            peer.OnNotification<NetworkRequestsParams>(m, p => session?.RaiseNetwork(p.Requests));
        peer.OnNotification<LogEntriesParams>(Methods.LogsEntries, p => session?.RaiseLogEntries(p));
        peer.OnNotification<EventsRaisedParams>(Methods.EventsRaised, p => session?.RaiseEventsRaised(p));
        peer.OnNotification<MetricsSampleParams>(Methods.MetricsSample, p => session?.RaiseMetricsSample(p));
        peer.OnNotification<PickSelectedParams>(Methods.PickSelected, p => session?.RaisePickSelected(p));
        peer.OnNotification<HotReloadManifest>(Methods.HotReloadManifest, p => session?.RaiseHotReloadManifest(p));
        peer.OnNotification<HotReloadResult>(Methods.HotReloadApplied, p => session?.RaiseHotReloadApplied(p));
        peer.OnNotification<HotReloadCodeUpdate>(Methods.HotReloadCodeUpdated, p => session?.RaiseHotReloadCodeUpdated(p));

        try
        {
            await peer.RunAsync(admission.Token);
        }
        finally
        {
            if (session is not null && _sessions.TryRemove(session.SessionId, out _))
                SessionDisconnected?.Invoke(session);
        }
    }

    public async ValueTask DisposeAsync()
    {
        HotReload.Dispose();
        await Ide.DisposeAsync().ConfigureAwait(false);
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
