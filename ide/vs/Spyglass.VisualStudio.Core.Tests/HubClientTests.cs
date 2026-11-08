using Spyglass.VisualStudio.Core;
using Xunit;

namespace Spyglass.VisualStudio.Core.Tests;

/// <summary>HubClient against <see cref="FakeHub"/>: token, hello, status, diagnostics, reveal, reconnect.</summary>
[Collection("fakehub")]
public sealed class HubClientTests : IAsyncLifetime
{
    internal const int Port = 29545;
    readonly string _home = Wait.TempHome();
    readonly List<string> _log = [];
    readonly List<IAsyncDisposable> _cleanup = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--) await _cleanup[i].DisposeAsync();
        try { Directory.Delete(_home, true); } catch { }
    }

    async Task<FakeHub> Hub(string token = "secret-token", bool writeTokenFile = true)
    {
        var hub = await FakeHub.StartAsync(Port, _home, token, writeTokenFile);
        _cleanup.Add(hub);
        return hub;
    }

    HubClient Client(string[]? folders = null, bool autoStart = false)
    {
        var c = new HubClient(new HubClientOptions
        {
            Port = Port,
            Home = _home,
            Ide = "vs",
            Version = "17.14",
            WorkspaceFolders = () => folders ?? ["/work/App"],
            AutoStart = autoStart,
            Log = l => { lock (_log) _log.Add(l); },
            RetryDelay = TimeSpan.FromMilliseconds(200),
            MaxRetryDelay = TimeSpan.FromMilliseconds(800),
        });
        _cleanup.Insert(0, c);   // clients go before the hub
        return c;
    }

    [Fact]
    public async Task Connects_with_the_token_from_the_hub_file_and_says_hello()
    {
        var hub = await Hub();
        var client = Client(["/work/App", "/work/Lib"]);
        var connected = 0;
        client.Connected += () => Interlocked.Increment(ref connected);
        client.Start();
        await Wait.UntilAsync(() => client.IsConnected, "connected");

        Assert.True(hub.Hellos.Reader.TryRead(out var hello));
        Assert.Equal(("vs", "17.14", "secret-token"), (hello.Ide, hello.Version, hello.Token));
        Assert.Equal(["/work/App", "/work/Lib"], hello.WorkspaceFolders);
        Assert.Equal(1, connected);
        Assert.Equal(HubConnectionState.Connected, client.State);
        Assert.EndsWith($"hub-{Port}.json", client.TokenFile);
    }

    [Fact]
    public async Task Status_decides_which_files_are_sent_case_insensitively_except_on_linux()
    {
        var file = Path.Combine(_home, "Views", "MainView.axaml");
        var hub = await Hub();
        hub.InitialStatus = new HotReloadStatusParams([new("a1", "Sample", 42, true, true, null, 3)], [file], "⚡ MainView.axaml → Sample", true);
        var client = Client();
        HotReloadStatusParams? seen = null;
        client.StatusChanged += s => seen = s;
        client.Start();
        await Wait.UntilAsync(() => client.CanReload(file), "status");

        Assert.Equal("Sample", Assert.Single(seen!.Apps).Name);
        Assert.Equal(!OperatingSystem.IsLinux(), client.CanReload(file.ToUpperInvariant()));
        Assert.False(client.CanReload(Path.Combine(_home, "Views", "Other.axaml")));

        hub.SendStatus(new HotReloadStatusParams([], []));
        await Wait.UntilAsync(() => !client.CanReload(file), "files cleared by a newer status");
    }

    [Fact]
    public async Task Wrong_token_is_reported_and_retried_until_the_file_is_right()
    {
        var hub = await Hub(token: "right");
        File.WriteAllText(hub.TokenFile, """{"port":29545,"token":"wrong"}""");
        var client = Client();
        client.Start();
        await Wait.UntilAsync(() => { lock (_log) return _log.Any(l => l.Contains("refused", StringComparison.Ordinal)); }, "refusal logged");
        Assert.False(client.IsConnected);

        hub.WriteTokenFile();
        await Wait.UntilAsync(() => client.IsConnected, "connected after the token was fixed");
    }

    [Fact]
    public async Task Without_a_hub_and_without_autostart_it_keeps_retrying_with_backoff()
    {
        var client = Client();
        var states = new List<HubConnectionState>();
        client.StateChanged += s => { lock (states) states.Add(s); };
        client.Start();
        await Task.Delay(700);
        Assert.False(client.IsConnected);
        lock (states) Assert.DoesNotContain(HubConnectionState.Starting, states);   // autoStart off: never spawns serve

        var hub = await Hub();
        await Wait.UntilAsync(() => client.IsConnected, "connected once the hub appeared");
        Assert.Equal(1, hub.Accepted);
    }

    [Fact]
    public async Task Stale_token_file_without_a_listener_counts_as_no_hub()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, $"hub-{Port}.json"), """{"token":"from-a-dead-hub"}""");
        var client = Client();
        var failed = false;   // a refused connection takes about 2 s to report on Windows, so wait for it instead of a fixed time
        client.StateChanged += s => { if (s == HubConnectionState.Disconnected) Volatile.Write(ref failed, true); };
        client.Start();
        await Wait.UntilAsync(() => Volatile.Read(ref failed), "the connection to fail");
        var hub = await Hub();   // overwrites the token file
        await Wait.UntilAsync(() => client.IsConnected, "connected");
        Assert.Equal(1, hub.Accepted);
    }

    [Fact]
    public async Task Reconnects_after_the_hub_drops_the_connection_and_clears_status_meanwhile()
    {
        var file = Path.Combine(_home, "A.axaml");
        var hub = await Hub();
        hub.InitialStatus = new HotReloadStatusParams([], [file]);
        var client = Client();
        var statuses = new List<HotReloadStatusParams?>();
        client.StatusChanged += s => { lock (statuses) statuses.Add(s); };
        var connected = 0;
        client.Connected += () => Interlocked.Increment(ref connected);
        client.Start();
        await Wait.UntilAsync(() => client.CanReload(file), "status");

        await hub.DropAllAsync();
        await Wait.UntilAsync(() => { lock (statuses) return statuses.Contains(null); }, "status cleared on disconnect");
        await Wait.UntilAsync(() => connected == 2 && client.CanReload(file), "reconnected");
        Assert.Equal(2, hub.Accepted);
    }

    [Fact]
    public async Task Reconnect_command_drops_and_connects_again()
    {
        var hub = await Hub();
        var client = Client();
        client.Start();
        await Wait.UntilAsync(() => client.IsConnected, "connected");
        client.Reconnect();
        await Wait.UntilAsync(() => hub.Accepted == 2 && client.IsConnected, "second connection");
    }

    [Fact]
    public async Task Stop_disconnects_and_does_not_reconnect()
    {
        var hub = await Hub();
        var client = Client();
        client.Start();
        await Wait.UntilAsync(() => client.IsConnected, "connected");
        await client.StopAsync();
        await Wait.UntilAsync(() => hub.Connections == 0, "hub saw the close");
        await Task.Delay(600);
        Assert.Equal(1, hub.Accepted);
        Assert.Equal(HubConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task Diagnostics_arrive_with_severity_line_and_column_whatever_the_enum_casing()
    {
        var hub = await Hub();
        var client = Client();
        var got = new TaskCompletionSource<XamlDiagnosticsParams>();
        client.DiagnosticsReceived += d => got.TrySetResult(d);
        client.Start();
        await Wait.UntilAsync(() => client.IsConnected, "connected");

        // The hub writes "error" (JsonStringEnumMemberName); reading is case-insensitive either way.
        hub.SendRaw(IdeMethods.Diagnostics, new
        {
            path = "/work/App/MainView.axaml",
            version = 7,
            items = new object[]
            {
                new { severity = "Error", message = "Unable to resolve property Titel", line = 8, column = 33, code = "AVLN2000" },
                new { severity = "warning", message = "w" },
            },
            summary = "✗ MainView.axaml:8:33 → Sample: …",
        });
        var d = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(7, d.Version);
        Assert.Equal((XamlDiagnosticSeverity.Error, 8, 33, "AVLN2000"), (d.Items[0].Severity, d.Items[0].Line, d.Items[0].Column, d.Items[0].Code));
        Assert.Equal(XamlDiagnosticSeverity.Warning, d.Items[1].Severity);
        Assert.Null(d.Items[1].Line);
    }

    [Fact]
    public async Task Reveal_is_answered_by_the_handler_and_false_without_one()
    {
        var hub = await Hub();
        var client = Client();
        client.Start();
        await Wait.UntilAsync(() => client.IsConnected, "connected");

        Assert.False((await hub.RevealAsync(new("/work/App/MainView.axaml", 3, 5))).Ok);

        EditorRevealParams? asked = null;
        client.RevealHandler = (r, _) => { asked = r; return Task.FromResult(true); };
        Assert.True((await hub.RevealAsync(new("/work/App/MainView.axaml", 3, 5))).Ok);
        Assert.Equal(new EditorRevealParams("/work/App/MainView.axaml", 3, 5), asked);

        client.RevealHandler = (_, _) => throw new IOException("gone");
        Assert.False((await hub.RevealAsync(new("/work/App/Missing.axaml", 1, 1))).Ok);
    }

    [Fact]
    public async Task Unknown_requests_get_method_not_found()
    {
        var hub = await Hub();
        var client = Client();
        client.Start();
        await Wait.UntilAsync(() => client.IsConnected, "connected");
        var ex = await Assert.ThrowsAsync<JsonRpcException>(() => hub.RequestAsync<object>("hotReload/restartApp", new { }));
        Assert.Equal(RpcErrorCodes.MethodNotFound, ex.Code);
    }

    [Fact]
    public void Serve_command_runs_a_dll_with_dotnet()
    {
        var (file, args) = HubClient.ServeCommand("/x/Spyglass.dll", 29541);
        Assert.Equal("dotnet", file);
        Assert.Equal(["/x/Spyglass.dll", "serve", "--port", "29541", "--quiet"], args);
        (file, args) = HubClient.ServeCommand("", 29500);
        Assert.Equal("spyglass", file);
        Assert.Equal(["serve", "--port", "29500", "--quiet"], args);
        Assert.Equal(@"C:\tools\spyglass.exe", HubClient.ServeCommand(@" C:\tools\spyglass.exe ", 1).FileName);
    }

    [Fact]
    public void Port_and_home_come_from_the_environment_by_default()
    {
        var client = new HubClient(new HubClientOptions { Home = "/h" });
        Assert.Equal(HubClient.DefaultPort(), client.Port);
        Assert.Equal(Path.Combine("/h", $"hub-{client.Port}.json"), client.TokenFile);
    }
}
