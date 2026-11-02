using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Spyglass.App.Core.Network;
using Spyglass.Protocol.Messages;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>Network panel v0.3 pure logic: sorting, overview range filter, HAR, column persistence.</summary>
public class NetworkV03LogicTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    static NetworkRequestDto R(long id, double startMs, double total, double wait, double download, string url = "https://example.com/a", string method = "GET",
        int? status = 200, string? mime = "application/json", long? size = 100, NetworkState state = NetworkState.Finished) => new()
    {
        Id = id, Method = method, Url = url, StartTime = T0.AddMilliseconds(startMs), State = state, Status = status, MimeType = mime, ResponseSize = size,
        EndTime = state == NetworkState.Pending ? null : T0.AddMilliseconds(startMs + total),
        Timing = state == NetworkState.Pending ? null : new NetworkTimingDto(Queueing: 1, Send: 1, Wait: wait, Download: download, Total: total),
    };

    static string Order(IEnumerable<NetworkRequestDto> rs, NetworkSortSpec s) => string.Join(",", NetworkSort.Sort(rs, s).Select(r => r.Id));

    // id: start, total, wait, download  → response time = start + total - download; end = start + total
    static readonly NetworkRequestDto[] Samples =
    [
        R(1, 0, 300, 100, 50),    // resp 250  end 300  dur 300 lat 100
        R(2, 10, 100, 20, 10),    // resp 100  end 110  dur 100 lat 20
        R(3, 20, 500, 400, 30),   // resp 490  end 520  dur 500 lat 400
        R(4, 30, 60, 5, 5),       // resp 85   end 90   dur 60  lat 5
    ];

    [Theory]
    [InlineData(WaterfallSort.StartTime, false, "1,2,3,4")]
    [InlineData(WaterfallSort.StartTime, true, "4,3,2,1")]
    [InlineData(WaterfallSort.ResponseTime, false, "4,2,1,3")]
    [InlineData(WaterfallSort.EndTime, false, "4,2,1,3")]
    [InlineData(WaterfallSort.TotalDuration, false, "4,2,1,3")]
    [InlineData(WaterfallSort.TotalDuration, true, "3,1,2,4")]
    [InlineData(WaterfallSort.Latency, false, "4,2,1,3")]
    [InlineData(WaterfallSort.Latency, true, "3,1,2,4")]
    public void Waterfall_orders(WaterfallSort w, bool desc, string expected) =>
        Assert.Equal(expected, Order(Samples.Reverse(), new NetworkSortSpec(NetworkColumn.Waterfall, desc, w)));

    [Fact]
    public void Waterfall_orders_differ_when_the_metrics_disagree()
    {
        // A started first but answered last; B is the slowest overall; C has the lowest latency.
        var a = R(1, 0, 300, 290, 10);   // response at 290, end 300, latency 290
        var b = R(2, 5, 1000, 100, 900); // response at 105, end 1005, latency 100
        var c = R(3, 10, 400, 5, 395);   // response at 15,  end 410,  latency 5
        var all = new[] { a, b, c };
        Assert.Equal("1,2,3", Order(all, new(NetworkColumn.Waterfall, false, WaterfallSort.StartTime)));
        Assert.Equal("3,2,1", Order(all, new(NetworkColumn.Waterfall, false, WaterfallSort.ResponseTime)));
        Assert.Equal("1,3,2", Order(all, new(NetworkColumn.Waterfall, false, WaterfallSort.EndTime)));
        Assert.Equal("1,3,2", Order(all, new(NetworkColumn.Waterfall, false, WaterfallSort.TotalDuration)));
        Assert.Equal("3,2,1", Order(all, new(NetworkColumn.Waterfall, false, WaterfallSort.Latency)));
    }

    [Fact]
    public void Column_sorts_and_stable_ties()
    {
        var rs = new[]
        {
            R(1, 0, 10, 1, 1, "https://h/zeta", "POST", 500, "text/plain", 5),
            R(2, 1, 10, 1, 1, "https://h/Alpha", "GET", 200, "image/png", 900),
            R(3, 2, 10, 1, 1, "https://h/mid", "GET", 404, "application/json", null),
            R(4, 3, 10, 1, 1, "https://h/pending", "GET", null, null, null, NetworkState.Pending),
        };
        Assert.Equal("2,3,4,1", Order(rs, new(NetworkColumn.Name)));                // case-insensitive
        Assert.Equal("2,3,4,1", Order(rs, new(NetworkColumn.Method)));              // GET (ties by time) before POST
        Assert.Equal("2,3,1,4", Order(rs, new(NetworkColumn.Status)));              // pending last
        Assert.Equal("4,1,3,2", Order(rs, new(NetworkColumn.Status, true)));        // descending is the exact reverse of "known first"
        Assert.Equal("1,3,4,2", Order(rs, new(NetworkColumn.Type)));                // fetch ×3 (ties by time), then png
        Assert.Equal("1,2,3,4", Order(rs, new(NetworkColumn.Size)));                // 5, 900, unknown(3,4: ties by time)
        Assert.Equal("1,2,3,4", Order(rs, NetworkSortSpec.Chronological));
    }

    [Fact]
    public void Custom_header_column_sorts_by_value()
    {
        static NetworkRequestDto H(long id, string v) => R(id, id, 1, 1, 1) with { ResponseHeaders = [new HeaderDto("Server", v)] };
        Assert.Equal("2,1,3", Order([H(1, "nginx"), H(2, "Kestrel"), H(3, "nginx2")], new NetworkSortSpec(Custom: "server")));
    }

    [Fact]
    public void Overview_range_is_inclusive_and_uses_activity()
    {
        var now = T0.AddSeconds(10);
        var r = R(1, 100, 50, 10, 10);            // active 100..150
        DateTimeOffset At(double ms) => T0.AddMilliseconds(ms);
        Assert.True(NetworkOverview.InRange(r, At(0), At(100), now));    // touches the start: inclusive
        Assert.True(NetworkOverview.InRange(r, At(150), At(200), now));  // touches the end: inclusive
        Assert.True(NetworkOverview.InRange(r, At(110), At(120), now));  // inside
        Assert.True(NetworkOverview.InRange(r, At(0), At(1000), now));   // contains
        Assert.False(NetworkOverview.InRange(r, At(0), At(99), now));
        Assert.False(NetworkOverview.InRange(r, At(151), At(300), now));
        Assert.True(NetworkOverview.InRange(r, At(120), At(110), now));  // reversed drag
        Assert.True(NetworkOverview.InRange(r, null, null, now));        // no range
        var pending = R(2, 100, 0, 0, 0, state: NetworkState.Pending);
        Assert.True(NetworkOverview.InRange(pending, At(5000), At(6000), now));   // still running: active until "now"
        Assert.False(NetworkOverview.InRange(pending, At(11_000), At(12_000), now));
    }

    [Fact]
    public void Overview_model_and_lanes()
    {
        var m = NetworkOverview.Build([R(1, 0, 100, 1, 1), R(2, 50, 100, 1, 1), R(3, 200, 10, 1, 1)], T0.AddSeconds(1));
        Assert.Equal(T0, m.Origin);
        Assert.Equal(210, m.TotalMs);
        var lanes = NetworkOverview.Lanes(m.Bars, 4);
        Assert.NotEqual(lanes[0], lanes[1]);       // overlapping requests get different lanes
        Assert.Equal(lanes[0], lanes[2]);          // a later one reuses a free lane
        Assert.Equal(m.Origin.AddMilliseconds(50), m.At(50));
    }

    // ---- HAR ----

    static NetworkRequestDto Full(long id) => new()
    {
        Id = id, Method = "POST", Url = "https://api.example.com/login?x=1&y=a%20b", StartTime = T0.AddMilliseconds(id * 10), State = NetworkState.Finished,
        RequestHeaders = [new("Content-Type", "application/json"), new("Authorization", "Bearer secret-token", true), new("Cookie", "sid=abc123", true), new("X-Api-Key", "k-999", true), new("X-Trace", "t1")],
        RequestBodySize = 14, Status = 401, StatusText = "Unauthorized", HttpVersion = "http/1.1",
        ResponseHeaders = [new("Content-Type", "application/json"), new("Set-Cookie", "theme=dark; Path=/; HttpOnly", true), new("Server", "Kestrel")],
        MimeType = "application/json", ResponseSize = 27, RemoteAddress = "127.0.0.1:5001", ConnectionId = 7,
        Initiator = new InitiatorDto("UserService.SignInAsync", ["UserService.SignInAsync (UserService.cs:42)", "LoginViewModel.Execute (LoginViewModel.cs:10)"]),
        Timing = new NetworkTimingDto(1.5, 2, 3, -1, 0.5, 20.25, 4, 31.25), EndTime = T0.AddMilliseconds(id * 10 + 31.25),
    };

    static IReadOnlyDictionary<(long, string), NetworkBodyResult> Bodies(long id) => new Dictionary<(long, string), NetworkBodyResult>
    {
        [(id, "request")] = new("application/json", "text", """{"user":"ada"}""", 14, false),
        [(id, "response")] = new("application/json", "text", """{"error":"invalid_credentials"}""", 27, false),
    };

    static void AssertSameRow(NetworkRequestDto a, NetworkRequestDto b, bool sensitive)
    {
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(a.Method, b.Method);
        Assert.Equal(a.Url, b.Url);
        Assert.Equal(a.StartTime, b.StartTime);
        Assert.Equal(a.State, b.State);
        Assert.Equal(a.Status, b.Status);
        Assert.Equal(a.StatusText, b.StatusText);
        Assert.Equal(a.HttpVersion, b.HttpVersion);
        Assert.Equal(a.MimeType, b.MimeType);
        Assert.Equal(a.ResponseSize, b.ResponseSize);
        Assert.Equal(a.RequestBodySize, b.RequestBodySize);
        Assert.Equal(a.RemoteAddress, b.RemoteAddress);
        Assert.Equal(a.ConnectionId, b.ConnectionId);
        Assert.Equal(a.Timing, b.Timing);
        Assert.Equal(a.EndTime, b.EndTime);
        Assert.Equal(a.Error, b.Error);
        Assert.Equal(a.Initiator!.Frame, b.Initiator!.Frame);
        Assert.Equal(a.Initiator.Stack, b.Initiator.Stack);
        Assert.Equal(a.RequestHeaders.Where(h => sensitive || !h.IsSensitive).Select(h => (h.Name, h.Value)), b.RequestHeaders.Select(h => (h.Name, h.Value)));
    }

    [Fact]
    public void Har_with_sensitive_data_round_trips_rows_and_bodies()
    {
        var rows = new[] { Full(1), Full(2) with { Error = null, Status = 200, StatusText = "OK" } };
        var json = HarConverter.Build(rows, Bodies(1), new HarOptions(IncludeSensitive: true));
        var import = HarConverter.Parse(json);
        Assert.Equal(2, import.Requests.Count);
        for (var i = 0; i < 2; i++) AssertSameRow(rows[i], import.Requests[i], sensitive: true);
        Assert.Contains("Bearer secret-token", json);
        Assert.Contains("sid=abc123", json);
        Assert.Equal("""{"error":"invalid_credentials"}""", import.Bodies[(1, "response")].Content);
        Assert.Equal("""{"user":"ada"}""", import.Bodies[(1, "request")].Content);
        Assert.Contains(import.Requests[0].RequestHeaders, h => h.Name == "Authorization" && h.IsSensitive);   // re-flagged on import
    }

    [Fact]
    public void Sanitized_har_has_no_credentials_or_configured_sensitive_headers()
    {
        var json = HarConverter.Build([Full(1)], Bodies(1), new HarOptions(ExtraSensitiveHeaders: ["X-Trace"]));
        foreach (var secret in new[] { "secret-token", "sid=abc123", "theme=dark", "k-999", "Authorization", "Set-Cookie", "\"Cookie\"", "X-Trace" })
            Assert.DoesNotContain(secret, json);
        Assert.Contains("Kestrel", json);                      // ordinary headers stay
        Assert.Contains("invalid_credentials", json);   // bodies stay (Chrome only strips headers)
        var import = HarConverter.Parse(json);
        AssertSameRow(Full(1) with { RequestHeaders = [new("Content-Type", "application/json")] }, import.Requests[0], sensitive: false);
    }

    [Fact]
    public void Har_structure_follows_the_spec()
    {
        var doc = JsonDocument.Parse(HarConverter.Build([Full(1)], Bodies(1)));
        var log = doc.RootElement.GetProperty("log");
        Assert.Equal("1.2", log.GetProperty("version").GetString());
        var e = log.GetProperty("entries")[0];
        Assert.Equal(31.25, e.GetProperty("time").GetDouble());
        var t = e.GetProperty("timings");
        Assert.Equal(-1, t.GetProperty("ssl").GetDouble());           // unknown = -1
        Assert.Equal(20.25, t.GetProperty("wait").GetDouble());
        Assert.Equal(4, t.GetProperty("receive").GetDouble());
        Assert.Equal("127.0.0.1", e.GetProperty("serverIPAddress").GetString());
        Assert.Equal("UserService.SignInAsync", e.GetProperty("_initiator").GetProperty("frame").GetString());
        Assert.Equal("a b", e.GetProperty("request").GetProperty("queryString")[1].GetProperty("value").GetString());
        Assert.Equal("""{"user":"ada"}""", e.GetProperty("request").GetProperty("postData").GetProperty("text").GetString());
    }

    [Fact]
    public void Binary_bodies_use_base64_and_failed_requests_survive()
    {
        var png = new NetworkRequestDto { Id = 1, Method = "GET", Url = "https://x/a.png", StartTime = T0, State = NetworkState.Finished, Status = 200, MimeType = "image/png", ResponseSize = 3 };
        var failed = new NetworkRequestDto { Id = 2, Method = "GET", Url = "https://nope.invalid/", StartTime = T0.AddSeconds(1), State = NetworkState.Failed, Error = "net::ERR_NAME_NOT_RESOLVED" };
        var bodies = new Dictionary<(long, string), NetworkBodyResult> { [(1, "response")] = new("image/png", "base64", Convert.ToBase64String([1, 2, 3]), 3, false) };
        var import = HarConverter.Parse(HarConverter.Build([png, failed], bodies));
        Assert.Equal("base64", import.Bodies[(1, "response")].Encoding);
        Assert.Equal(NetworkState.Failed, import.Requests[1].State);
        Assert.Equal("net::ERR_NAME_NOT_RESOLVED", import.Requests[1].Error);
        Assert.Null(import.Requests[1].Status);
        Assert.Null(import.Requests[1].Timing);
    }

    [Fact]
    public void Parses_a_chrome_style_har_and_rejects_garbage()
    {
        const string chrome = """
        {"log":{"version":"1.2","creator":{"name":"WebInspector","version":"537.36"},"entries":[
          {"_initiator":{"type":"script","stack":{"callFrames":[{"functionName":"load","url":"https://a/app.js","lineNumber":12,"columnNumber":3}]}},
           "_resourceType":"fetch","startedDateTime":"2026-11-02T10:00:00.000Z","time":120.5,
           "request":{"method":"GET","url":"https://a/api","httpVersion":"h2","headers":[{"name":"accept","value":"*/*"}],"queryString":[],"cookies":[],"headersSize":-1,"bodySize":0},
           "response":{"status":200,"statusText":"","httpVersion":"h2","headers":[{"name":"content-type","value":"application/json; charset=utf-8"}],"cookies":[],
             "content":{"size":10,"mimeType":"application/json; charset=utf-8","text":"{\"a\":1}"},"redirectURL":"","headersSize":-1,"bodySize":-1},
           "cache":{},"timings":{"blocked":2,"dns":-1,"connect":-1,"ssl":-1,"send":0.1,"wait":100,"receive":18.4},"serverIPAddress":"1.2.3.4","connection":"55"}]}}
        """;
        var r = HarConverter.Parse(chrome).Requests.Single();
        Assert.Equal("h2", r.HttpVersion);
        Assert.Equal("application/json", r.MimeType);
        Assert.Equal(120.5, r.Timing!.Total);
        Assert.Equal(-1, r.Timing.Dns);
        Assert.Equal("load (https://a/app.js:12)", r.Initiator!.Frame);
        Assert.Equal("1.2.3.4", r.RemoteAddress);
        Assert.Equal(55, r.ConnectionId);
        Assert.Equal(NetworkState.Finished, r.State);
        Assert.Throws<FormatException>(() => HarConverter.Parse("not json"));
        Assert.Throws<FormatException>(() => HarConverter.Parse("""{"foo":1}"""));
    }

    [Fact]
    public void Copy_all_joins_formats()
    {
        var rs = new[] { R(1, 0, 1, 1, 1, "https://h/a"), R(2, 1, 1, 1, 1, "https://h/b") };
        Assert.Equal("https://h/a\nhttps://h/b", NetworkCopyAll.Join("urls", rs, _ => null));
        var curl = NetworkCopyAll.Join("curl", rs, r => r.Id == 2 ? "{}" : null);
        Assert.Contains("curl 'https://h/a'", curl);
        Assert.Contains("--data-raw '{}'", curl);
        Assert.Contains(" ;\ncurl ", curl);
    }

    // ---- column persistence ----

    [Fact]
    public void Column_settings_persist_to_the_injected_path()
    {
        var path = Path.Combine(Path.GetTempPath(), "spyglass-test-" + Guid.NewGuid().ToString("N"), "net.json");
        try
        {
            var store = new NetworkSettingsStore(path);
            var s = store.Load();                                          // missing file → defaults
            Assert.Equal(NetworkSettings.DefaultColumns.ToHashSet().Append(NetworkColumn.Name).ToHashSet(), s.VisibleColumns);
            s.SetVisible(NetworkColumn.Method, false);
            s.SetVisible(NetworkColumn.Domain, true);
            s.SetVisible(NetworkColumn.Connection, true);
            s.CustomHeaders.Add("Cache-Control");
            s.ShowOverview = true;
            s.AllowSensitiveHar = true;
            store.Save(s);

            var back = new NetworkSettingsStore(path).Load();
            Assert.DoesNotContain(NetworkColumn.Method, back.VisibleColumns);
            Assert.Contains(NetworkColumn.Domain, back.VisibleColumns);
            Assert.Contains(NetworkColumn.Connection, back.VisibleColumns);
            Assert.Contains(NetworkColumn.Name, back.VisibleColumns);
            Assert.Equal(["Cache-Control"], back.CustomHeaders);
            Assert.True(back.ShowOverview && back.AllowSensitiveHar);

            File.WriteAllText(path, "{ corrupt");
            Assert.Equal(NetworkSettings.DefaultColumns.Length + 1, new NetworkSettingsStore(path).Load().VisibleColumns.Count);   // corrupt → defaults
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }
}

[Collection("app")]
public class NetworkV03McpTests(AppFixture app) : IAsyncLifetime
{
    static readonly HttpClient Http = new();
    readonly TestHttpServer _server = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() { await app.Session.DisableNetworkAsync(); _server.Dispose(); }

    [Fact]
    public async Task Mcp_network_export_har_writes_a_sanitized_file()
    {
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(app.Hub.BaseUri, "mcp") }));
        async Task<string> Call(string tool, Dictionary<string, object?> args) =>
            string.Join("\n", (await mcp.CallToolAsync(tool, args)).Content.OfType<TextContentBlock>().Select(t => t.Text));

        await Call("network_capture", new() { ["action"] = "start" });
        var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_server.BaseUri, "api/login?har"))
        { Content = new StringContent("{\"user\":\"ada\"}", Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "har-secret-token");
        (await Http.SendAsync(req)).Dispose();
        (await Http.GetAsync(new Uri(_server.BaseUri, "api/user?har"))).Dispose();
        for (var i = 0; i < 100 && app.Session.Network.Requests.Count(r => r.Url.EndsWith("?har") && r.State != NetworkState.Pending) < 2; i++) await Task.Delay(50);

        var text = await Call("network_export_har", new() { ["filter"] = "?har" });
        var path = text.Split('\n')[0]["HAR written: ".Length..].Trim();
        Assert.True(File.Exists(path), text);
        Assert.Contains("2 request(s)", text);
        Assert.Contains("4xx:1", text);
        Assert.Contains("Sanitized", text);

        var json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("har-secret-token", json);
        Assert.DoesNotContain("Authorization", json);
        Assert.DoesNotContain("theme=dark", json);                      // Set-Cookie of /api/user
        Assert.Contains("invalid_credentials", json);                   // response bodies included
        Assert.Contains("Ada Lovelace", json);
        var import = HarConverter.Parse(json);
        Assert.Equal(2, import.Requests.Count);
        Assert.Contains(import.Requests, r => r.Status == 401 && r.Method == "POST");
        Assert.Contains("Spyglass", json);

        Assert.Contains("No requests match", await Call("network_export_har", new() { ["filter"] = "nothing-matches-this" }));
        File.Delete(path);
    }
}
