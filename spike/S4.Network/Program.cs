using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace S4.Network;

public static class Program
{
    static readonly List<(string Id, string Name, bool Ok, string Detail)> Results = new();

    static async Task Check(string id, string name, Func<Task<(bool, string)>> body)
    {
        try { var (ok, d) = await body(); Results.Add((id, name, ok, d)); }
        catch (Exception ex) { Results.Add((id, name, false, $"{ex.GetType().Name}: {ex.Message}")); }
    }

    public static async Task Main(string[] args)
    {
        if (args.Contains("s5")) { await S5.RunAsync(); return; }
        using var server = new TestServer();
        var baseUri = server.BaseUri;
        // The agent's own WebSocket to the Spyglass client must never show up in the panel.
        var spyglassPort = server.Port;
        var capture = new NetworkCapture(u => u.AbsolutePath.StartsWith("/agent"));

        using var defaultClient = new HttpClient { BaseAddress = baseUri };
        using var socketsClient = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(1) }) { BaseAddress = baseUri };
        using var handlerClient = new HttpClient(new HttpClientHandler()) { BaseAddress = baseUri };
        using var wrappedClient = new HttpClient(new LoggingHandler(new SocketsHttpHandler())) { BaseAddress = baseUri };

        // ---- 1. Disabled: nothing captured ----
        await defaultClient.GetStringAsync("/json");
        await Check("N1", "Disabled → nothing captured", async () =>
        {
            await defaultClient.GetStringAsync("/json");
            return (capture.Captured.IsEmpty, $"captured={capture.Captured.Count}");
        });

        capture.Enable();

        await Check("N2", "GET JSON (buffered) → status, headers, body, timings", async () =>
        {
            var text = await defaultClient.GetStringAsync("/json");
            var c = await Next(capture);
            var body = Encoding.UTF8.GetString(c.ResponseBody!.Bytes);
            return (c.Status == 200 && body == text && c.ResponseHeaders.ContainsKey("Content-Type"),
                $"{c.Method} {c.Uri.PathAndQuery} → {c.Status} HTTP/{c.HttpVersion} type={string.Join(",", c.ResponseHeaders["Content-Type"])} body={c.ResponseBody.TotalBytes}B match={body == text} ttfb={c.Ms(c.HeadersTs):F1}ms total={c.Ms(c.EndTs):F1}ms initiator={c.Initiator}");
        });

        await Check("N3", "POST JSON → request body captured, server still receives it intact", async () =>
        {
            var payload = """{"name":"Ada","tags":["a","b"]}""";
            var resp = await defaultClient.PostAsync("/echo", new StringContent(payload, Encoding.UTF8, "application/json"));
            var echoed = await resp.Content.ReadAsStringAsync();
            var c = await Next(capture);
            var reqBody = Encoding.UTF8.GetString(c.RequestBody!.Bytes);
            return (reqBody == payload && echoed == payload && c.RequestHeaders.ContainsKey("Content-Type"),
                $"request body={reqBody.Length}B match={reqBody == payload}; server echoed intact={echoed == payload}; req Content-Type={string.Join(",", c.RequestHeaders.GetValueOrDefault("Content-Type") ?? [])}");
        });

        await Check("N4", "Streaming (ResponseHeadersRead) → first chunk not delayed, full body captured", async () =>
        {
            var sw = Stopwatch.StartNew();
            using var resp = await defaultClient.GetAsync("/stream", HttpCompletionOption.ResponseHeadersRead);
            await using var s = await resp.Content.ReadAsStreamAsync();
            var buf = new byte[64];
            var first = await s.ReadAsync(buf);
            var firstMs = sw.Elapsed.TotalMilliseconds;
            var total = first;
            int n;
            while ((n = await s.ReadAsync(buf)) > 0) total += n;
            var c = await Next(capture);
            return (firstMs < 300 && c.ResponseBody!.TotalBytes == total && c.ResponseBody.Completed,
                $"first chunk after {firstMs:F0}ms (server sends 5 chunks 200ms apart); total read {total}B, captured {c.ResponseBody!.TotalBytes}B, completed={c.ResponseBody.Completed}, body end at {c.Ms(c.EndTs):F0}ms");
        });

        await Check("N5", "Custom SocketsHttpHandler / HttpClientHandler / DelegatingHandler chain are all captured", async () =>
        {
            await socketsClient.GetStringAsync("/json?h=sockets");
            await handlerClient.GetStringAsync("/json?h=clienthandler");
            await wrappedClient.GetStringAsync("/json?h=delegating");
            var got = new List<string>();
            for (var i = 0; i < 3; i++) got.Add((await Next(capture)).Uri.Query);
            return (got.Count == 3, string.Join(" ", got));
        });

        await Check("N6", "404 and connection refused", async () =>
        {
            var r404 = await defaultClient.GetAsync("/missing");
            var c404 = await Next(capture);
            try { await defaultClient.GetAsync($"http://127.0.0.1:{FreePort()}/nobody-home"); } catch (HttpRequestException) { }
            var cErr = await Next(capture);
            return (c404.Status == 404 && cErr.Error is not null,
                $"404 captured status={c404.Status}; refused → status={cErr.Status?.ToString() ?? "none"} error='{cErr.Error}'");
        });

        await Check("N7", "Large body is truncated at BodyLimit but counted fully", async () =>
        {
            capture.BodyLimit = 1024;
            var bytes = await defaultClient.GetByteArrayAsync("/big");
            var c = await Next(capture);
            capture.BodyLimit = 1024 * 1024;
            return (c.ResponseBody!.Truncated && c.ResponseBody.Bytes.Length == 1024 && c.ResponseBody.TotalBytes == bytes.Length,
                $"read {bytes.Length}B; stored {c.ResponseBody.Bytes.Length}B; counted {c.ResponseBody.TotalBytes}B; truncated={c.ResponseBody.Truncated}");
        });

        await Check("N8a", "WebSocket baseline with capture disabled", async () =>
        {
            capture.Disable();
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{spyglassPort}/chat"), default);
                return (ws.State == WebSocketState.Open, $"state={ws.State}");
            }
            finally { capture.Enable(); }
        });

        await Check("N8", "Agent's own WebSocket (/agent) is excluded; other WebSockets?", async () =>
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{spyglassPort}/agent"), default);
            }
            catch (Exception ex) { return (false, "agent ws: " + ex.ToString().Split('\n').Where(l => !l.Contains(" at ") || l.Contains("S4.")).Take(12).Aggregate((a, b) => a + "\n          " + b)); }
            using (var ws2 = new ClientWebSocket())
                await ws2.ConnectAsync(new Uri($"ws://127.0.0.1:{spyglassPort}/chat"), default);
            await Task.Delay(100);
            var seen = Drain(capture).Select(c => $"{c.Uri.AbsolutePath}→{c.Status}{(c.IsUpgrade ? " (upgrade)" : "")}").ToList();
            return (!seen.Any(x => x.StartsWith("/agent")) && seen.Any(x => x.StartsWith("/chat")), $"both sockets connected; captured: [{string.Join(", ", seen)}]");
        });

        await Check("N9", "Overhead: 300 sequential GETs disabled vs enabled", async () =>
        {
            capture.Disable();
            var off = await Time(() => defaultClient.GetStringAsync("/json"), 300);
            capture.Enable();
            var on = await Time(() => defaultClient.GetStringAsync("/json"), 300);
            Drain(capture);
            capture.CaptureInitiator = false;
            var onNoStack = await Time(() => defaultClient.GetStringAsync("/json"), 300);
            Drain(capture);
            return (true, $"disabled {off:F3} ms/req · enabled {on:F3} ms/req · enabled without initiator stack {onNoStack:F3} ms/req");
        });

        capture.Disable();
        await Check("N10", "Disable again → nothing captured", async () =>
        {
            await defaultClient.GetStringAsync("/json");
            await Task.Delay(50);
            return (capture.Captured.IsEmpty, $"captured={capture.Captured.Count}");
        });

        Console.WriteLine($".NET {Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n");
        foreach (var r in Results)
            Console.WriteLine($"[{(r.Ok ? "PASS" : "FAIL")}] {r.Id,-3} {r.Name}\n        {r.Detail}");
    }

    static async Task<CapturedRequest> Next(NetworkCapture capture)
    {
        for (var i = 0; i < 200; i++)
        {
            if (capture.Captured.TryDequeue(out var c))
            {
                // wait for the body to finish streaming into the capture
                for (var j = 0; j < 100 && c.ResponseBody is { Completed: false }; j++) await Task.Delay(10);
                return c;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("nothing captured");
    }

    static List<CapturedRequest> Drain(NetworkCapture capture)
    {
        var l = new List<CapturedRequest>();
        while (capture.Captured.TryDequeue(out var c)) l.Add(c);
        return l;
    }

    static async Task<double> Time(Func<Task> f, int n)
    {
        for (var i = 0; i < 20; i++) await f();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < n; i++) await f();
        return sw.Elapsed.TotalMilliseconds / n;
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}

sealed class LoggingHandler(HttpMessageHandler inner) : DelegatingHandler(inner);

/// <summary>Tiny HttpListener server: /json, /echo, /stream, /big, /missing, and WebSocket endpoints.</summary>
sealed class TestServer : IDisposable
{
    readonly HttpListener _listener = new();
    public int Port { get; }
    public Uri BaseUri => new($"http://127.0.0.1:{Port}/");

    public TestServer()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _listener.Prefixes.Add(BaseUri.ToString());
        _listener.Start();
        _ = Loop();
    }

    async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            _ = Handle(ctx);
        }
    }

    static async Task Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        var res = ctx.Response;
        try
        {
            if (ctx.Request.IsWebSocketRequest)
            {
                var ws = await ctx.AcceptWebSocketAsync(null);
                await ws.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
                return;
            }
            switch (path)
            {
                case "/json":
                    await Write(res, "application/json", """{"user":{"id":1,"fullName":"Ada Lovelace"},"items":[1,2,3]}""");
                    break;
                case "/echo":
                    using (var r = new StreamReader(ctx.Request.InputStream))
                        await Write(res, "application/json", await r.ReadToEndAsync());
                    break;
                case "/stream":
                    res.ContentType = "text/plain";
                    res.SendChunked = true;
                    for (var i = 0; i < 5; i++)
                    {
                        var b = Encoding.UTF8.GetBytes($"chunk {i}\n");
                        await res.OutputStream.WriteAsync(b);
                        await res.OutputStream.FlushAsync();
                        await Task.Delay(200);
                    }
                    res.Close();
                    break;
                case "/big":
                    var big = new byte[200_000];
                    Random.Shared.NextBytes(big);
                    res.ContentType = "application/octet-stream";
                    res.ContentLength64 = big.Length;
                    await res.OutputStream.WriteAsync(big);
                    res.Close();
                    break;
                default:
                    res.StatusCode = 404;
                    await Write(res, "text/plain", "not found");
                    break;
            }
        }
        catch { /* client went away */ }
    }

    static async Task Write(HttpListenerResponse res, string type, string body)
    {
        var b = Encoding.UTF8.GetBytes(body);
        res.ContentType = type;
        res.ContentLength64 = b.Length;
        await res.OutputStream.WriteAsync(b);
        res.Close();
    }

    public void Dispose() => _listener.Close();
}
