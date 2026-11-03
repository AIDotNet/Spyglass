using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Spyglass.VisualStudio.Core;

namespace Spyglass.VisualStudio.Core.Tests;

/// <summary>
/// A stand-in for the hub's /ide endpoint (src/Spyglass.App.Core/IdeEndpoint.cs): checks the token in ide/hello, refuses
/// requests with an Origin header, records what the IDE sends, and lets a test push status/diagnostics and ask for reveal.
/// </summary>
sealed class FakeHub : IAsyncDisposable
{
    readonly WebApplication _app;
    readonly ConcurrentDictionary<JsonRpcPeer, byte> _peers = new();

    public string Token { get; }
    public string Home { get; }
    public int Port { get; }
    public string TokenFile => Path.Combine(Home, $"hub-{Port}.json");

    /// <summary>Sent right after every accepted ide/hello (like the real hub).</summary>
    public HotReloadStatusParams? InitialStatus { get; set; }

    public Channel<IdeHelloParams> Hellos { get; } = Channel.CreateUnbounded<IdeHelloParams>();
    public Channel<(string Method, XamlDocumentParams Params)> Documents { get; } = Channel.CreateUnbounded<(string, XamlDocumentParams)>();
    public int Accepted => _accepted;
    int _accepted;

    FakeHub(WebApplication app, int port, string home, string token)
    {
        _app = app;
        Port = port;
        Home = home;
        Token = token;
    }

    public static async Task<FakeHub> StartAsync(int port, string home, string token = "secret-token", bool writeTokenFile = true)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.UseWebSockets();
        var hub = new FakeHub(app, port, home, token);
        app.Map(IdeMethods.Path, hub.HandleAsync);
        await app.StartAsync();
        if (writeTokenFile) hub.WriteTokenFile();
        return hub;
    }

    public void WriteTokenFile()
    {
        Directory.CreateDirectory(Home);
        File.WriteAllText(TokenFile, JsonSerializer.Serialize(new { port = Port, token = Token, pid = Environment.ProcessId, version = "test" }));
    }

    async Task HandleAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest || ctx.Request.Headers.Origin.Count > 0)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        await using var peer = new JsonRpcPeer(ws);
        var hello = false;
        peer.OnRequest<IdeHelloParams, IdeHelloResult>(IdeMethods.Hello, (h, ct) =>
        {
            if (h.Token != Token) throw new JsonRpcException(RpcErrorCodes.Unauthorized, "Wrong token");
            hello = true;
            Interlocked.Increment(ref _accepted);
            _peers[peer] = 0;
            Hellos.Writer.TryWrite(h);
            if (InitialStatus is { } s)
                _ = Task.Run(async () => { await Task.Delay(50); peer.Notify(IdeMethods.Status, s); });
            return Task.FromResult(new IdeHelloResult("test", Port, ["hotReload"]));
        });
        foreach (var m in new[] { IdeMethods.DidOpen, IdeMethods.DidChange, IdeMethods.DidClose })
            peer.OnNotification<XamlDocumentParams>(m, p => { if (hello) Documents.Writer.TryWrite((m, p)); });
        try { await peer.RunAsync(); }
        finally { _peers.TryRemove(peer, out _); }
    }

    public int Connections => _peers.Count;

    public void SendStatus(HotReloadStatusParams s) { foreach (var p in _peers.Keys) p.Notify(IdeMethods.Status, s); }

    public void SendDiagnostics(XamlDiagnosticsParams d) { foreach (var p in _peers.Keys) p.Notify(IdeMethods.Diagnostics, d); }

    public void SendRaw(string method, object payload) { foreach (var p in _peers.Keys) p.Notify(method, payload); }

    public Task<EditorRevealResult> RevealAsync(EditorRevealParams r) =>
        _peers.Keys.Last().RequestAsync<EditorRevealResult>(IdeMethods.Reveal, r, TimeSpan.FromSeconds(5));

    public Task<T> RequestAsync<T>(string method, object? p) => _peers.Keys.Last().RequestAsync<T>(method, p, TimeSpan.FromSeconds(5));

    /// <summary>Closes every IDE connection (the IDE should reconnect).</summary>
    public async Task DropAllAsync()
    {
        foreach (var p in _peers.Keys) await p.CloseAsync();
    }

    /// <summary>Next didOpen/didChange/didClose, skipping none.</summary>
    public async Task<(string Method, XamlDocumentParams Params)> NextDocumentAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        return await Documents.Reader.ReadAsync(cts.Token);
    }

    /// <summary>True when nothing arrives within the window.</summary>
    public async Task<bool> QuietAsync(int ms = 500)
    {
        await Task.Delay(ms);
        return !Documents.Reader.TryPeek(out _);
    }

    public async ValueTask DisposeAsync()
    {
        await DropAllAsync();
        try { File.Delete(TokenFile); } catch { }
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"timed out waiting for: {what}");
            await Task.Delay(25);
        }
    }

    public static async Task UntilAsync(Func<Task<bool>> condition, string what, int timeoutMs = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"timed out waiting for: {what}");
            await Task.Delay(100);
        }
    }

    public static string TempHome()
    {
        var dir = Path.Combine(Path.GetTempPath(), "spyglass-vs-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
