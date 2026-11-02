using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Spyglass.VisualStudio.Core;
using Xunit;

namespace Spyglass.VisualStudio.Core.Tests;

/// <summary>
/// The real thing: <c>spyglass serve</c> + the sample app from this repository's Debug build (build the repo first; the
/// tests are skipped otherwise). The client sends unsaved text only; the sample's files on disk are never written.
/// </summary>
public sealed class RealHubFixture : IAsyncLifetime
{
    public const int Port = 29541;
    public string Home { get; } = Wait.TempHome();
    public string? RepoRoot { get; } = FindRepoRoot();
    public string HubDll => Path.Combine(RepoRoot!, "src", "Spyglass.App", "bin", "Debug", "net10.0", "Spyglass.dll");
    public string SampleDll => Path.Combine(RepoRoot!, "samples", "Spyglass.Sample", "bin", "Debug", "net10.0", "Spyglass.Sample.dll");
    public string MainWindow => Path.Combine(RepoRoot!, "samples", "Spyglass.Sample", "MainWindow.axaml");
    public string? SkipReason { get; private set; }
    Process? _serve, _sample;

    public async ValueTask InitializeAsync()
    {
        if (RepoRoot is null || !File.Exists(HubDll) || !File.Exists(SampleDll))
        {
            SkipReason = "Build the Spyglass repository (Debug) first: src/Spyglass.App and samples/Spyglass.Sample.";
            return;
        }
        _serve = Start(HubDll, ["serve", "--port", Port.ToString(), "--quiet"]);
        await Wait.UntilAsync(() => File.Exists(Path.Combine(Home, $"hub-{Port}.json")), "spyglass serve token file", 20000);
        _sample = Start(SampleDll, []);
    }

    Process Start(string dll, string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add(dll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["SPYGLASS_HOME"] = Home;
        psi.Environment["SPYGLASS_PORT"] = Port.ToString();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sample is { HasExited: false }) { _sample.Kill(); await _sample.WaitForExitAsync(); }
        if (_serve is not null) await Terminate(_serve);
        try { Directory.Delete(Home, true); } catch { }
    }

    /// <summary>SIGTERM (the hub removes its token file on a clean stop), then kill if it lingers.</summary>
    public static async Task Terminate(Process p)
    {
        if (p.HasExited) return;
        if (OperatingSystem.IsWindows()) p.Kill();
        else Process.Start("kill", ["-TERM", p.Id.ToString()])!.WaitForExit();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { p.Kill(); }
    }

    static string? FindRepoRoot()
    {
        if (Environment.GetEnvironmentVariable("SPYGLASS_REPO") is { Length: > 0 } r) return r;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Spyglass.slnx")) && Directory.Exists(Path.Combine(d.FullName, "src"))) return d.FullName;
        return null;
    }

    /// <summary>Calls a tool on the hub's MCP endpoint (streamable HTTP; the answer comes as SSE <c>data:</c> lines).</summary>
    public async Task<string> McpToolAsync(string name, object arguments)
    {
        using var http = new HttpClient();
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments } });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{Port}/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"MCP {(int)res.StatusCode}: {text}");
        var data = text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()).ToList();
        return data.Count > 0 ? string.Join("\n", data) : text;
    }
}

[CollectionDefinition("realhub")]
public sealed class RealHubCollection : ICollectionFixture<RealHubFixture>;

[Collection("realhub")]
public sealed class RealHubTests(RealHubFixture hub)
{
    [Fact]
    public async Task Unsaved_text_reloads_the_sample_app_and_a_mistake_comes_back_as_a_diagnostic()
    {
        if (hub.SkipReason is { } skip) Assert.Skip(skip);
        var original = await File.ReadAllTextAsync(hub.MainWindow);   // read only: the file on disk is never written
        var log = new List<string>();
        await using var client = new HubClient(new HubClientOptions
        {
            Port = RealHubFixture.Port,
            Home = hub.Home,
            Ide = "vs",
            Version = "test",
            WorkspaceFolders = () => [Path.GetDirectoryName(hub.MainWindow)!],
            AutoStart = false,
            Log = l => { lock (log) log.Add(l); },
        });
        using var sync = new XamlBufferSync(client) { DelayMs = 100 };
        var diagnostics = System.Threading.Channels.Channel.CreateUnbounded<XamlDiagnosticsParams>();
        client.DiagnosticsReceived += d => diagnostics.Writer.TryWrite(d);
        client.RevealHandler = (_, _) => Task.FromResult(true);

        client.Start();
        await Wait.UntilAsync(() => client.CanReload(hub.MainWindow), "hotReload/status lists the sample's MainWindow.axaml", 30000);
        Assert.Contains(client.Status!.Apps, a => a.HotReload && a.Available && a.Documents > 0);

        async Task<XamlDiagnosticsParams> Next()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                var d = await diagnostics.Reader.ReadAsync(cts.Token);
                if (HubClient.PathKey(d.Path) == HubClient.PathKey(hub.MainWindow)) return d;
            }
        }

        try
        {
            sync.Opened(hub.MainWindow, () => original, isDirty: false);

            // 1. A new TextBlock typed in the editor shows up in the running app.
            var anchor = original.IndexOf("<TextBlock x:Name=\"Heading\"", StringComparison.Ordinal);
            Assert.True(anchor > 0, "sample MainWindow.axaml changed; update the test anchor");
            var lineEnd = original.IndexOf('\n', anchor) + 1;
            var modified = original.Insert(lineEnd, "    <TextBlock Text=\"From Visual Studio\" />\n");
            sync.Changed(hub.MainWindow, () => modified, isDirty: true);
            var ok = await Next();
            Assert.Empty(ok.Items);
            Assert.StartsWith("⚡", ok.Summary);
            var found = await hub.McpToolAsync("ui_find", new { query = "text=From Visual Studio" });
            TestContext.Current.TestOutputHelper?.WriteLine($"reload: {ok.Summary}\nui_find: {found}");
            Assert.Contains("From Visual Studio", found);
            Assert.DoesNotContain("\"isError\":true", found);

            // 2. A typo comes back as an error at its line; the app keeps the last good UI.
            var bad = modified.Replace("{Binding Title}", "{Binding Titel}");
            Assert.NotEqual(modified, bad);
            sync.Changed(hub.MainWindow, () => bad, isDirty: true);
            var err = await Next();
            var item = Assert.Single(err.Items, i => i.Severity == XamlDiagnosticSeverity.Error);
            var expectedLine = bad[..bad.IndexOf("Titel", StringComparison.Ordinal)].Count(c => c == '\n') + 1;
            TestContext.Current.TestOutputHelper?.WriteLine($"diagnostic: {item}\nsummary: {err.Summary}");
            Assert.Equal(expectedLine, item.Line);
            Assert.StartsWith("✗", err.Summary);
            Assert.Contains("From Visual Studio", await hub.McpToolAsync("ui_find", new { query = "text=From Visual Studio" }));
        }
        finally
        {
            // Put the app back the way the file on disk is, then hand the file back to the hub's file watcher.
            sync.Changed(hub.MainWindow, () => original, isDirty: false);
            try { await Next(); } catch { /* best effort */ }
            sync.Closed(hub.MainWindow);
            await Task.Delay(200);
        }
    }

    [Fact]
    public async Task Auto_starts_spyglass_serve_from_a_dll_when_nothing_listens()
    {
        if (hub.SkipReason is { } skip) Assert.Skip(skip);
        const int port = 29542;
        var home = Wait.TempHome();
        var log = new List<string>();
        var client = new HubClient(new HubClientOptions
        {
            Port = port,
            Home = home,
            Command = hub.HubDll,
            AutoStart = true,
            Log = l => { lock (log) log.Add(l); },
        });
        var states = new List<HubConnectionState>();
        client.StateChanged += s => { lock (states) states.Add(s); };
        int? pid = null;
        try
        {
            client.Start();
            await Wait.UntilAsync(() => client.IsConnected, "connected to the hub it started", 30000);
            lock (states) Assert.Contains(HubConnectionState.Starting, states);
            lock (log) Assert.Contains(log, l => l.Contains($"serve --port {port} --quiet", StringComparison.Ordinal));
            await Wait.UntilAsync(() => client.Status is not null, "status after hello");
            using var doc = JsonDocument.Parse(File.ReadAllText(client.TokenFile));
            pid = doc.RootElement.GetProperty("pid").GetInt32();
        }
        finally
        {
            await client.DisposeAsync();
            pid ??= TryReadPid(client.TokenFile);
            if (pid is { } p) { try { await RealHubFixture.Terminate(Process.GetProcessById(p)); } catch (ArgumentException) { } }
            try { Directory.Delete(home, true); } catch { }
        }
    }

    static int? TryReadPid(string tokenFile)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(tokenFile));
            return doc.RootElement.GetProperty("pid").GetInt32();
        }
        catch { return null; }
    }
}
