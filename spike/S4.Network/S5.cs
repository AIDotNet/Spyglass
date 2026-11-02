using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO.Compression;
using System.Net;
using System.Text;

namespace S4.Network;

/// <summary>Spike S5: Chrome-parity details (design/network/02-chrome-parity.md §13).</summary>
public static class S5
{
    public static async Task RunAsync()
    {
        using var server = new S5Server();
        using var telemetry = new NetTelemetry();
        var captured = new ConcurrentQueue<(HttpRequestMessage Req, HttpResponseMessage? Resp, Dictionary<string, string[]> ReqHeadersAtStart, Guid Activity, long Start, long Stop, string UriAtStart)>();
        var starts = new ConcurrentDictionary<HttpRequestMessage, (Dictionary<string, string[]>, Guid, long, string)>();

        using var sub = DiagnosticListener.AllListeners.Subscribe(new Obs<DiagnosticListener>(l =>
        {
            if (l.Name != "HttpHandlerDiagnosticListener") return;
            l.Subscribe(new Obs<KeyValuePair<string, object?>>(kv =>
            {
                var req = kv.Value?.GetType().GetProperty("Request")?.GetValue(kv.Value) as HttpRequestMessage;
                if (req is null) return;
                if (kv.Key.EndsWith(".Start"))
                    starts[req] = (req.Headers.Concat(req.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                        .ToDictionary(h => h.Key, h => h.Value.ToArray()), EventSource.CurrentThreadActivityId, Stopwatch.GetTimestamp(), req.RequestUri!.ToString());
                if (kv.Key.EndsWith(".Stop") && starts.TryRemove(req, out var s))
                    captured.Enqueue((req, kv.Value!.GetType().GetProperty("Response")?.GetValue(kv.Value) as HttpResponseMessage, s.Item1, s.Item2, s.Item3, Stopwatch.GetTimestamp(), s.Item4));
            }), (n, _, _) => n.StartsWith("System.Net.Http.HttpRequestOut"));
        }));

        var cookies = new CookieContainer();
        cookies.Add(new Uri(server.BaseUri), new Cookie("session", "abc123"));
        using var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = cookies,
            AllowAutoRedirect = true,
        }) { BaseAddress = new Uri(server.BaseUri) };

        // Fresh connection (DNS via "localhost" + TCP connect), then a reused one.
        var localhost = new Uri($"http://localhost:{server.Port}/");
        await http.GetStringAsync(new Uri(localhost, "/plain"));
        await http.GetStringAsync(new Uri(localhost, "/plain?again"));
        await http.GetStringAsync("/redirect");
        var gz = await http.GetStringAsync("/gzip");
        await Task.Delay(300);

        Console.WriteLine($".NET {Environment.Version}\n");
        foreach (var c in captured)
        {
            Console.WriteLine($"== {c.Req.Method} uri-at-start={c.UriAtStart} now={c.Req.RequestUri} → {(int?)c.Resp?.StatusCode} final={c.Resp?.RequestMessage?.RequestUri}  activity={c.Activity}");
            Console.WriteLine($"   request headers at Start : {string.Join(", ", c.ReqHeadersAtStart.Keys)}");
            Console.WriteLine($"   request headers at Stop  : {string.Join(", ", c.Req.Headers.Select(h => h.Key))}");
            Console.WriteLine($"   response headers         : {string.Join(", ", c.Resp?.Headers.Select(h => h.Key).Concat(c.Resp.Content.Headers.Select(h => h.Key)) ?? [])}");
            string cl;
            try { cl = $"content-length={c.Resp?.Content.Headers.ContentLength} content-encoding={string.Join(",", c.Resp?.Content.Headers.ContentEncoding ?? [])}"; }
            catch (ObjectDisposedException) { cl = "content disposed by the redirect handler (intermediate hop)"; }
            Console.WriteLine("   " + cl);
            var mine = telemetry.For(c.Activity);
            Console.WriteLine($"   telemetry events in this request's activity tree ({mine.Count}):");
            foreach (var e in mine) Console.WriteLine($"     +{Stopwatch.GetElapsedTime(c.Start, e.Ts).TotalMilliseconds,7:F2} ms  {e.Source}/{e.Name} {e.Payload}");
        }
        Console.WriteLine($"\ngzip body decoded by HttpClient: {gz.Length} chars, starts with {gz[..20]}");
        Console.WriteLine("\nAll telemetry events (activity / related):");
        foreach (var e in telemetry.All.Take(0)) Console.WriteLine($"  {e.Source}/{e.Name} act={Short(e.Activity)} rel={Short(e.Related)} {e.Payload}");
    }

    static string Short(Guid g) => g == Guid.Empty ? "-" : g.ToString()[..8];

    sealed class Obs<T>(Action<T> next) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception e) { }
        public void OnNext(T v) => next(v);
    }
}

/// <summary>In-process EventListener for System.Net.* telemetry, with TPL activity-id flow enabled.</summary>
sealed class NetTelemetry : EventListener
{
    public record Evt(string Source, string Name, Guid Activity, Guid Related, long Ts, string Payload);
    public readonly ConcurrentQueue<Evt> All = new();
    readonly ConcurrentDictionary<Guid, Guid> _parent = new();

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "System.Threading.Tasks.TplEventSource")
            EnableEvents(source, EventLevel.LogAlways, (EventKeywords)0x80);   // TasksFlowActivityIds
        else if (source.Name is "System.Net.Http" or "System.Net.NameResolution" or "System.Net.Sockets" or "System.Net.Security")
            EnableEvents(source, EventLevel.LogAlways, EventKeywords.All);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventSource.Name.StartsWith("System.Threading")) return;
        if (e.RelatedActivityId != Guid.Empty) _parent[e.ActivityId] = e.RelatedActivityId;
        var payload = e.PayloadNames is null ? "" : string.Join(" ", e.PayloadNames.Zip(e.Payload ?? [], (n, v) => $"{n}={v}"));
        All.Enqueue(new Evt(e.EventSource.Name.Replace("System.Net.", ""), e.EventName ?? "?", e.ActivityId, e.RelatedActivityId, Stopwatch.GetTimestamp(), payload));
    }

    /// <summary>Events whose activity is the request's activity or a descendant of it.</summary>
    public List<Evt> For(Guid root) => All.Where(e => IsUnder(e.Activity, root)).ToList();

    bool IsUnder(Guid a, Guid root)
    {
        for (var i = 0; i < 32 && a != Guid.Empty; i++)
        {
            if (a == root) return true;
            if (!_parent.TryGetValue(a, out a)) return false;
        }
        return false;
    }
}

sealed class S5Server : IDisposable
{
    readonly HttpListener _l = new();
    public int Port { get; }
    public string BaseUri => $"http://127.0.0.1:{Port}/";

    public S5Server()
    {
        var t = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        t.Start(); Port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
        _l.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _l.Prefixes.Add($"http://localhost:{Port}/");
        _l.Start();
        _ = Loop();
    }

    async Task Loop()
    {
        while (_l.IsListening)
        {
            HttpListenerContext c;
            try { c = await _l.GetContextAsync(); } catch { return; }
            var r = c.Response;
            switch (c.Request.Url!.AbsolutePath)
            {
                case "/redirect":
                    r.StatusCode = 302; r.RedirectLocation = "/plain?from=redirect"; r.Close(); break;
                case "/gzip":
                    var body = Encoding.UTF8.GetBytes("{\"data\":\"" + new string('x', 2000) + "\"}");
                    using (var ms = new MemoryStream())
                    {
                        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(body);
                        r.AddHeader("Content-Encoding", "gzip"); r.ContentType = "application/json";
                        r.ContentLength64 = ms.Length; await r.OutputStream.WriteAsync(ms.ToArray()); r.Close();
                    }
                    break;
                default:
                    var b = Encoding.UTF8.GetBytes($"cookie header seen by server: {c.Request.Headers["Cookie"]}");
                    r.AddHeader("Set-Cookie", "theme=dark; Path=/; HttpOnly");
                    r.ContentLength64 = b.Length; await r.OutputStream.WriteAsync(b); r.Close();
                    break;
            }
        }
    }

    public void Dispose() => _l.Close();
}
