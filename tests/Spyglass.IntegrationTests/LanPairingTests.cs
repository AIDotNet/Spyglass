using System.Net;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using Spyglass.App.Core;
using Spyglass.App.Core.Lan;
using Spyglass.Mcp;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>LAN connections: pairing code, throttle, and "only /agent is reachable from other machines" (design/protocol §6).</summary>
public class LanPairingTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    static readonly IPAddress Lan1 = IPAddress.Parse("192.168.1.50");

    // ---------- code format, comparison, throttle (no network) ----------

    [Fact]
    public void Code_is_six_digits_from_a_cryptographic_source()
    {
        var codes = Enumerable.Range(0, 500).Select(_ => PairingGuard.NewCode()).ToList();
        Assert.All(codes, c => Assert.Matches(@"^\d{6}$", c));
        Assert.True(codes.Distinct().Count() > 480, "codes should be (nearly) all distinct");
        // every digit shows up in every position often enough to rule out a stuck or biased generator
        for (var pos = 0; pos < PairingGuard.CodeLength; pos++)
            Assert.Equal(10, codes.Select(c => c[pos]).Distinct().Count());
    }

    [Fact]
    public void Regenerate_changes_the_code_and_raises_an_event()
    {
        var guard = new PairingGuard("123456");
        var raised = 0;
        guard.CodeChanged += () => raised++;
        var next = guard.Regenerate();
        Assert.NotEqual("123456", next);
        Assert.Equal(next, guard.Code);
        Assert.Equal(1, raised);
    }

    [Theory]
    [InlineData("123456", "123456", true)]
    [InlineData("123456", "123457", false)]
    [InlineData("123456", "023456", false)]
    [InlineData("123456", "12345", false)]
    [InlineData("123456", "1234567", false)]
    [InlineData("123456", "", false)]
    [InlineData("123456", null, false)]
    [InlineData(null, null, false)]
    public void Codes_compare_exactly(string? a, string? b, bool expected) => Assert.Equal(expected, PairingGuard.CodesEqual(a, b));

    [Fact]
    public void Throttle_blocks_after_repeated_failures_and_recovers_after_the_cool_down()
    {
        var now = DateTimeOffset.UtcNow;
        var guard = new PairingGuard("111111", () => now);
        for (var i = 0; i < PairingGuard.MaxFailures; i++)
        {
            Assert.False(guard.IsBlocked(Lan1));
            Assert.Equal(PairingResult.WrongCode, guard.Check(Lan1, "000000"));
        }
        Assert.True(guard.IsBlocked(Lan1));
        Assert.Equal(PairingResult.Blocked, guard.Check(Lan1, "111111"));        // even the right code: no guessing oracle
        Assert.Equal(PairingResult.Accepted, guard.Check(IPAddress.Parse("192.168.1.51"), "111111"));   // other addresses unaffected

        now += PairingGuard.BlockDuration + TimeSpan.FromSeconds(1);
        Assert.False(guard.IsBlocked(Lan1));
        Assert.Equal(PairingResult.Accepted, guard.Check(Lan1, "111111"));
    }

    [Fact]
    public void Throttle_counts_only_failures_inside_the_window_and_a_success_resets()
    {
        var now = DateTimeOffset.UtcNow;
        var guard = new PairingGuard("111111", () => now);
        for (var i = 0; i < PairingGuard.MaxFailures - 1; i++) guard.Check(Lan1, "x");
        now += PairingGuard.FailureWindow + TimeSpan.FromSeconds(1);
        for (var i = 0; i < PairingGuard.MaxFailures - 1; i++) guard.Check(Lan1, "x");
        Assert.False(guard.IsBlocked(Lan1));                                      // old failures expired
        Assert.Equal(PairingResult.Accepted, guard.Check(Lan1, "111111"));        // success forgets the failures
        for (var i = 0; i < PairingGuard.MaxFailures - 1; i++) guard.Check(Lan1, "x");
        Assert.False(guard.IsBlocked(Lan1));
    }

    [Fact]
    public void Throttle_treats_mapped_ipv4_as_ipv4_and_an_ipv6_64_as_one_host()
    {
        var guard = new PairingGuard("111111");
        for (var i = 0; i < PairingGuard.MaxFailures; i++) guard.Check(Lan1.MapToIPv6(), "x");
        Assert.True(guard.IsBlocked(Lan1));

        for (var i = 0; i < PairingGuard.MaxFailures; i++) guard.Check(IPAddress.Parse($"2001:db8:1:2::{i + 1}"), "x");
        Assert.True(guard.IsBlocked(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd")));
        Assert.False(guard.IsBlocked(IPAddress.Parse("2001:db8:1:3::1")));
    }

    // ---------- endpoint policy (no network) ----------

    [Theory]
    [InlineData("127.0.0.1", "/mcp", true, true)]
    [InlineData("::1", "/mcp", true, true)]
    [InlineData("::ffff:127.0.0.1", "/mcp", true, true)]           // IPv4-mapped loopback
    [InlineData("127.5.5.5", "/mcp", true, true)]
    [InlineData("192.168.1.50", "/mcp", true, false)]
    [InlineData("::ffff:192.168.1.50", "/mcp", true, false)]       // IPv4-mapped LAN address is not loopback
    [InlineData("192.168.1.50", "/", true, false)]
    [InlineData("192.168.1.50", "/anything", true, false)]
    [InlineData("192.168.1.50", "/agent", true, true)]
    [InlineData("192.168.1.50", "/AGENT", true, true)]
    [InlineData("192.168.1.50", "/agent/x", true, true)]           // same routing rule as app.Map("/agent")
    [InlineData("192.168.1.50", "/agentx", true, false)]
    [InlineData("192.168.1.50", "/agent", false, false)]           // LAN off: nothing for other machines
    [InlineData("127.0.0.1", "/agent", false, true)]
    public void Only_agent_is_reachable_from_other_machines(string remote, string path, bool allowLan, bool expected) =>
        Assert.Equal(expected, new LanGate(allowLan).IsAllowed(IPAddress.Parse(remote), path));

    [Fact]
    public void Unknown_remote_address_is_not_loopback() => Assert.False(new LanGate(true).IsAllowed(null, "/mcp"));

    // ---------- real connections ----------

    static async Task<SpyglassHub> StartHub(bool allowLan, string code = "424242")
    {
        var hub = new SpyglassHub(new SpyglassHubOptions { Port = 0, Lan = new LanGate(allowLan, new PairingGuard(code)) }.WithMcp());
        await hub.StartAsync();
        return hub;
    }

    static string? LanAddress() => LanAddresses.Get().FirstOrDefault()?.Address;

    static HelloParams Hello(string? code) => new(SpyglassProtocol.Version, new AgentInfo("test", "0", AgentFlavor.Basic),
        new AppInfo("LanApp", 1, "x|y", "12.1.4", "net10", "test", "x64", AppLifetimeKind.Other, true), [], code);

    sealed record Peer(ClientWebSocket Socket, JsonRpcPeer Rpc, TaskCompletionSource Closed) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await Rpc.DisposeAsync(); Socket.Dispose(); }
    }

    static async Task<Peer> Connect(string host, int port)
    {
        var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(Timeout);
        await ws.ConnectAsync(new Uri($"ws://{host}:{port}/agent"), cts.Token);
        var rpc = new JsonRpcPeer(ws);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rpc.Closed += _ => closed.TrySetResult();
        _ = rpc.RunAsync();
        return new Peer(ws, rpc, closed);
    }

    static Task<HelloResult> SendHello(Peer p, string? code) => p.Rpc.RequestAsync<HelloParams, HelloResult>(Methods.SessionHello, Hello(code));

    static async Task<int> HelloStatus(string host, int port, string? code)
    {
        await using var p = await Connect(host, port);
        try { await SendHello(p, code); return 0; }
        catch (SpyglassProtocolException ex) { return ex.Code; }
    }

    static string RequireLan()
    {
        var address = LanAddress();
        if (address is null) Assert.Skip("no non-loopback IPv4 address on this machine");
        return address;
    }

    [Fact]
    public async Task Loopback_agents_need_no_code()
    {
        await using var hub = await StartHub(allowLan: true);
        Assert.Equal(0, await HelloStatus("127.0.0.1", hub.Port, null));
        Assert.Equal(0, await HelloStatus("127.0.0.1", hub.Port, "wrong!"));
    }

    [Fact]
    public async Task Lan_agent_with_the_right_code_is_accepted()
    {
        var lan = RequireLan();
        await using var hub = await StartHub(allowLan: true);
        await using var p = await Connect(lan, hub.Port);
        var result = await SendHello(p, "424242");
        Assert.False(string.IsNullOrEmpty(result.SessionId));
        Assert.Single(hub.Sessions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("000000")]
    [InlineData("42424")]
    public async Task Lan_agent_with_a_missing_or_wrong_code_gets_Unauthorized_and_is_disconnected(string? code)
    {
        var lan = RequireLan();
        await using var hub = await StartHub(allowLan: true);
        await using var p = await Connect(lan, hub.Port);
        var ex = await Assert.ThrowsAsync<SpyglassProtocolException>(() => SendHello(p, code));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        await p.Closed.Task.WaitAsync(Timeout);                       // the hub closed the connection
        Assert.Empty(hub.Sessions);
    }

    [Fact]
    public async Task Lan_agent_is_blocked_after_repeated_wrong_codes_even_with_the_right_one()
    {
        var lan = RequireLan();
        await using var hub = await StartHub(allowLan: true);
        for (var i = 0; i < PairingGuard.MaxFailures; i++)
            Assert.Equal(ErrorCodes.Unauthorized, await HelloStatus(lan, hub.Port, "000000"));

        await Assert.ThrowsAnyAsync<Exception>(() => HelloStatus(lan, hub.Port, "424242"));   // upgrade refused (429)
        Assert.Empty(hub.Sessions);
        Assert.Equal(0, await HelloStatus("127.0.0.1", hub.Port, null));                      // loopback is never throttled
    }

    [Fact]
    public async Task Regenerating_the_code_drops_lan_sessions_but_not_loopback_ones()
    {
        var lan = RequireLan();
        await using var hub = await StartHub(allowLan: true);
        await using var remote = await Connect(lan, hub.Port);
        await SendHello(remote, "424242");
        await using var local = await Connect("127.0.0.1", hub.Port);
        await SendHello(local, null);
        Assert.Equal(2, hub.Sessions.Count);

        var next = hub.Lan.Guard.Regenerate();
        await remote.Closed.Task.WaitAsync(Timeout);
        Assert.False(local.Closed.Task.IsCompleted);
        Assert.Single(hub.Sessions);
        Assert.Equal(ErrorCodes.Unauthorized, await HelloStatus(lan, hub.Port, "424242"));     // old code is dead
        Assert.Equal(0, await HelloStatus(lan, hub.Port, next));
    }

    [Fact]
    public async Task Mcp_and_everything_but_agent_is_refused_for_other_machines()
    {
        var lan = RequireLan();
        await using var hub = await StartHub(allowLan: true);
        using var http = new HttpClient { Timeout = Timeout };
        foreach (var path in new[] { "mcp", "", "anything", "AGENTS", "agent/../mcp" })
        {
            var remote = await http.GetAsync($"http://{lan}:{hub.Port}/{path}");
            Assert.Equal(HttpStatusCode.Forbidden, remote.StatusCode);
        }
        var viaMcpPost = await http.PostAsync($"http://{lan}:{hub.Port}/mcp", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, viaMcpPost.StatusCode);

        // loopback still reaches MCP, and /agent answers other machines (400: it wants a WebSocket)
        var local = await http.GetAsync($"http://127.0.0.1:{hub.Port}/mcp");
        Assert.NotEqual(HttpStatusCode.Forbidden, local.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync($"http://{lan}:{hub.Port}/agent")).StatusCode);
    }

    [Fact]
    public async Task Without_allow_lan_nothing_listens_on_the_lan_address()
    {
        var lan = RequireLan();
        await using var hub = await StartHub(allowLan: false);
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(lan, hub.Port));
        Assert.Equal(0, await HelloStatus("127.0.0.1", hub.Port, null));
    }

    [Fact]
    public async Task Lan_peer_that_never_says_hello_is_not_a_session()
    {
        var lan = RequireLan();
        await using var hub = await StartHub(allowLan: true);
        await using var p = await Connect(lan, hub.Port);
        await p.Rpc.NotifyAsync(Methods.PickStarted, Empty.Instance);   // notifications before hello are ignored
        await Task.Delay(300);
        Assert.Empty(hub.Sessions);
    }
}
