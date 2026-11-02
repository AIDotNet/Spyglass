using System.Net;
using System.Net.Sockets;
using System.Text;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Spyglass.App.Core.Network;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>Network capture end to end (design/network/, spikes S4/S5). Capture is only on inside these tests.</summary>
[Collection("app")]
public class NetworkTests(AppFixture app) : IAsyncLifetime
{
    static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = true });
    readonly TestHttpServer _server = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await app.Session.DisableNetworkAsync();
        _server.Dispose();
    }

    async Task<NetworkRequestDto> WaitFor(Func<NetworkRequestDto, bool> match, bool finished = true)
    {
        for (var i = 0; i < 100; i++)
        {
            var r = app.Session.Network.Requests.LastOrDefault(x => match(x) && (!finished || x.State != NetworkState.Pending));
            if (r is not null) return r;
            await Task.Delay(50);
        }
        throw new TimeoutException("request not captured: " + string.Join(", ", app.Session.Network.Requests.Select(r => r.Url)));
    }

    [Fact]
    public async Task Nothing_is_captured_until_enabled_and_after_disabled()
    {
        await app.Session.DisableNetworkAsync();
        await Http.GetStringAsync(new Uri(_server.BaseUri, "api/user?before"));
        await app.Session.EnableNetworkAsync();
        await Http.GetStringAsync(new Uri(_server.BaseUri, "api/user?during"));
        await WaitFor(r => r.Url.EndsWith("?during"));
        await app.Session.DisableNetworkAsync();
        await Http.GetStringAsync(new Uri(_server.BaseUri, "api/user?after"));
        await Task.Delay(300);
        Assert.DoesNotContain(app.Session.Network.Requests, r => r.Url.EndsWith("?before") || r.Url.EndsWith("?after"));
    }

    [Fact]
    public async Task Get_json_with_headers_timing_initiator_and_body()
    {
        await app.Session.EnableNetworkAsync();
        var text = await Http.GetStringAsync(new Uri(_server.BaseUri, "api/user?t=json"));
        var r = await WaitFor(x => x.Url.EndsWith("?t=json"));

        Assert.Equal(200, r.Status);
        Assert.Equal("application/json", r.MimeType);
        Assert.Equal("http/1.1", r.HttpVersion);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), r.ResponseSize);
        Assert.Contains(r.ResponseHeaders, h => h.Name == "Set-Cookie" && h.IsSensitive);
        Assert.NotNull(r.Timing);
        Assert.True(r.Timing!.Total > 0 && r.Timing.Wait >= 0, $"timing {r.Timing}");
        Assert.Contains("127.0.0.1", r.RemoteAddress);                  // from System.Net telemetry (spike S5)
        Assert.Contains("NetworkTests.Get_json_with_headers_timing_initiator_and_body", r.Initiator!.Frame);
        Assert.Equal(text, (await app.Session.GetNetworkBodyAsync(r.Id)).Content);
        Assert.Equal("fetch", NetworkClassifier.TypeLabel(r));
    }

    [Fact]
    public async Task Post_401_keeps_request_body_and_flags_credentials()
    {
        await app.Session.EnableNetworkAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_server.BaseUri, "api/login"))
        {
            Content = new StringContent("""{"user":"ada","password":"x"}""", Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret-token");
        using var resp = await Http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("invalid_credentials", await resp.Content.ReadAsStringAsync());   // app still gets the body

        var r = await WaitFor(x => x.Url.EndsWith("/api/login"));
        Assert.Equal(401, r.Status);
        Assert.True(Assert.Single(r.RequestHeaders, h => h.Name == "Authorization").IsSensitive);
        Assert.Equal("""{"user":"ada","password":"x"}""", (await app.Session.GetNetworkBodyAsync(r.Id, "request")).Content);
        Assert.Contains("invalid_credentials", (await app.Session.GetNetworkBodyAsync(r.Id)).Content);
        var curl = NetworkCopy.Curl(r, """{"user":"ada","password":"x"}""");
        Assert.Contains("--data-raw", curl);
        Assert.Contains("-H 'Authorization: Bearer secret-token'", curl);
    }

    [Fact]
    public async Task Redirect_hops_are_separate_rows_like_chrome()
    {
        await app.Session.EnableNetworkAsync();
        await Http.GetStringAsync(new Uri(_server.BaseUri, "api/old"));
        var hop2 = await WaitFor(x => x.RedirectedFromId is not null && x.Url.EndsWith("/api/user"));
        var hop1 = app.Session.Network.Get(hop2.RedirectedFromId!.Value)!;
        Assert.Equal(302, hop1.Status);
        Assert.EndsWith("/api/old", hop1.Url);       // URL snapshot taken before the redirect mutated the request
        Assert.Equal(200, hop2.Status);
    }

    [Fact]
    public async Task Connection_refused_is_reported_with_chrome_error_name()
    {
        await app.Session.EnableNetworkAsync();
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        await Assert.ThrowsAsync<HttpRequestException>(() => Http.GetAsync($"http://127.0.0.1:{port}/nobody"));
        var r = await WaitFor(x => x.Url.Contains($":{port}/nobody"));
        Assert.Equal(NetworkState.Failed, r.State);
        Assert.Equal("net::ERR_CONNECTION_REFUSED", r.Error);
        Assert.Equal("(failed) net::ERR_CONNECTION_REFUSED", NetworkCopy.Status(r));
    }

    [Fact]
    public async Task Spyglass_traffic_is_never_captured()
    {
        await app.Session.EnableNetworkAsync();
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(app.Hub.BaseUri, "mcp") }));
        await mcp.ListToolsAsync();
        await Http.GetStringAsync(new Uri(_server.BaseUri, "api/user?marker"));
        await WaitFor(x => x.Url.EndsWith("?marker"));
        Assert.DoesNotContain(app.Session.Network.Requests, r => r.Url.Contains($":{app.Hub.Port}/"));
    }

    [Fact]
    public async Task Search_bodies_and_headers()
    {
        await app.Session.EnableNetworkAsync();
        await Http.GetStringAsync(new Uri(_server.BaseUri, "api/user?search"));
        var r = await WaitFor(x => x.Url.EndsWith("?search"));
        var hits = await app.Session.SearchNetworkAsync(new NetworkSearchParams("ada lovelace"));
        Assert.Contains(hits.Matches, m => m.Id == r.Id && m.Part == "response");
    }

    [Fact]
    public async Task Mcp_network_tools()
    {
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(app.Hub.BaseUri, "mcp") }));
        async Task<string> Call(string tool, Dictionary<string, object?> args) =>
            string.Join("\n", (await mcp.CallToolAsync(tool, args)).Content.OfType<TextContentBlock>().Select(t => t.Text));

        Assert.Contains("Recording network activity", await Call("network_capture", new() { ["action"] = "start" }));
        var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_server.BaseUri, "api/login?mcp"))
        { Content = new StringContent("{\"user\":\"ada\"}", Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret-token");
        (await Http.SendAsync(req)).Dispose();
        var captured = await WaitFor(x => x.Url.EndsWith("?mcp"));

        var list = await Call("network_requests", new() { ["status"] = "4xx" });
        Assert.Matches($@"#{captured.Id}\s+POST\s+401\s+/api/login\?mcp", list);
        Assert.Contains("⚠", list);

        var detail = await Call("network_request", new() { ["id"] = $"#{captured.Id}" });
        Assert.Contains("Authorization: •••••• (length", detail);
        Assert.DoesNotContain("secret-token", detail);                  // credentials never reach the model
        Assert.Contains("\"error\": \"invalid_credentials\"", detail);   // pretty-printed JSON
        Assert.Contains("timing      total", detail);
        Assert.Contains("Not recording", await Call("network_capture", new() { ["action"] = "stop" }));
    }
}

/// <summary>Pure client-side logic: Chrome filter syntax, classification, copy formats.</summary>
public class NetworkClientTests
{
    static NetworkRequestDto R(string url, string method = "GET", int? status = 200, string? mime = "application/json", long size = 100,
        params HeaderDto[] response) => new()
    {
        Id = 1, Method = method, Url = url, StartTime = DateTimeOffset.Now, Status = status, MimeType = mime, ResponseSize = size,
        ResponseHeaders = response, State = NetworkState.Finished,
    };

    [Theory]
    [InlineData("api", true)]
    [InlineData("-api", false)]
    [InlineData("domain:example.com", true)]
    [InlineData("domain:*.com", true)]
    [InlineData("domain:other.com", false)]
    [InlineData("method:post", false)]
    [InlineData("status-code:200", true)]
    [InlineData("status-code:200 method:GET", true)]
    [InlineData("status-code:200 method:POST", false)]
    [InlineData("larger-than:1k", false)]
    [InlineData("larger-than:50", true)]
    [InlineData("mime-type:application/json", true)]
    [InlineData("/users\\/\\d+/", true)]
    [InlineData("has-response-header:Set-Cookie", true)]
    [InlineData("set-cookie-name:theme", true)]
    [InlineData("scheme:https", true)]
    [InlineData("resource-type:fetch", true)]
    public void Chrome_filter_syntax(string filter, bool expected)
    {
        var r = R("https://example.com/api/users/42", response: new HeaderDto("Set-Cookie", "theme=dark; Path=/", true));
        Assert.Equal(expected, new NetworkFilter(filter).Matches(r));
    }

    [Theory]
    [InlineData("image/png", "png")]
    [InlineData("text/html", "document")]
    [InlineData("application/javascript", "script")]
    [InlineData("application/json", "fetch")]
    [InlineData("application/octet-stream", "other")]
    public void Type_labels(string mime, string label) => Assert.Equal(label, NetworkClassifier.TypeLabel(R("https://x/y", mime: mime)));

    [Fact]
    public void Copy_formats_and_column_text()
    {
        var r = R("https://example.com/api/items?q=1", "POST") with
        {
            RequestHeaders = [new HeaderDto("Content-Type", "application/json"), new HeaderDto("X-Trace", "a'b")],
        };
        Assert.Contains("curl 'https://example.com/api/items?q=1'", NetworkCopy.Curl(r, "{}"));
        Assert.Contains("'X-Trace: a'\\''b'", NetworkCopy.Curl(r, "{}"));
        Assert.Contains("-Method \"POST\"", NetworkCopy.PowerShell(r, "{}"));
        Assert.Contains("\"method\": \"POST\"", NetworkCopy.Fetch(r, "{}"));
        Assert.Contains("new StringContent(\"{}\", System.Text.Encoding.UTF8, \"application/json\")", NetworkCopy.CSharp(r, "{}"));
        Assert.Equal("items?q=1", NetworkCopy.Name(r));
        Assert.Equal("1.5 kB", NetworkCopy.Size(1500));
        Assert.Equal("(pending)", NetworkCopy.Status(r with { Status = null, Error = null }));
        Assert.Equal("(canceled)", NetworkCopy.Status(r with { Canceled = true }));
    }
}
