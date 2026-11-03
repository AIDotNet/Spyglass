using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia.Controls;
using Spyglass.App.Core;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>
/// The hub's /ide endpoint as an IDE extension uses it (design/hot-reload/README.md §7.1, §10): token, origin and loopback
/// checks, unsaved buffers reloading the app, diagnostics and status pushes, editor ownership vs the file watcher, reveal.
/// </summary>
[Collection("app")]
public class IdeEndpointTests(AppFixture app) : IAsyncLifetime
{
    const string Uri = "avares://Spyglass.IntegrationTests/HotReloadView.axaml";
    static readonly string SourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../HotReloadView.axaml"));
    static readonly string Original = File.ReadAllText(SourcePath);
    static readonly string WithSubtitle = Original.Replace("""<TextBox x:Name="Input" />""",
        """<TextBlock x:Name="Subtitle" Text="{Binding Subtitle}" />""" + "\n    <TextBox x:Name=\"Input\" />");

    Window _window = null!;
    HotReloadView _view = null!;
    readonly List<IAsyncDisposable> _cleanup = [];

    public async ValueTask InitializeAsync()
    {
        await app.Session.ApplyXamlAsync(Uri, Original);
        (_window, _view) = await app.OnUi(() =>
        {
            var view = new HotReloadView { DataContext = new HotReloadViewModel() };
            var w = new Window { Content = view };
            w.Show();
            return (w, view);
        });
        await WaitUntil(() => app.Hub.HotReload.Targets(SourcePath).Count > 0, "manifest");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _cleanup) await c.DisposeAsync();
        await WaitUntil(() => app.Hub.Ide.Connections.Count == 0, "IDE disconnected");
        if (await File.ReadAllTextAsync(SourcePath) != Original) await File.WriteAllTextAsync(SourcePath, Original);
        await app.SettleHotReloadAsync();   // closing the IDE re-applies the disk text, the restored file triggers the watcher
        await app.Session.ApplyXamlAsync(Uri, Original);
        await app.OnUi(() => { _window.Close(); return 0; });
    }

    sealed class FakeIde : IAsyncDisposable
    {
        public required JsonRpcPeer Peer;
        public required ClientWebSocket Socket;
        public required Task Loop;
        public readonly Channel<XamlDiagnosticsParams> Diagnostics = Channel.CreateUnbounded<XamlDiagnosticsParams>();
        public readonly Channel<HotReloadStatusParams> Status = Channel.CreateUnbounded<HotReloadStatusParams>();
        public readonly Channel<EditorRevealParams> Reveals = Channel.CreateUnbounded<EditorRevealParams>();

        public async Task<XamlDiagnosticsParams> NextDiagnostics(Func<XamlDiagnosticsParams, bool> match)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var d = await Diagnostics.Reader.ReadAsync(cts.Token);
                if (match(d)) return d;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            try { await Loop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            await Peer.DisposeAsync();
            Socket.Dispose();
        }
    }

    async Task<(FakeIde Ide, ClientWebSocket Ws)> Open(string? origin = null)
    {
        var ws = new ClientWebSocket();
        if (origin is not null) ws.Options.SetRequestHeader("Origin", origin);
        await ws.ConnectAsync(new System.Uri($"ws://127.0.0.1:{app.Hub.Port}{SpyglassProtocol.IdePath}"), CancellationToken.None);
        var peer = new JsonRpcPeer(ws);
        var ide = new FakeIde { Peer = peer, Socket = ws, Loop = Task.CompletedTask };
        peer.OnNotification<XamlDiagnosticsParams>(Methods.XamlDiagnostics, p => ide.Diagnostics.Writer.TryWrite(p));
        peer.OnNotification<HotReloadStatusParams>(Methods.HotReloadStatus, p => ide.Status.Writer.TryWrite(p));
        peer.Handle<EditorRevealParams, EditorRevealResult>(Methods.EditorReveal, (p, _) =>
        {
            ide.Reveals.Writer.TryWrite(p);
            return Task.FromResult(new EditorRevealResult(true));
        });
        ide.Loop = peer.RunAsync(CancellationToken.None);
        _cleanup.Add(ide);
        return (ide, ws);
    }

    async Task<FakeIde> Connect()
    {
        var (ide, _) = await Open();
        var folder = Path.GetDirectoryName(SourcePath)!;
        var hello = await ide.Peer.RequestAsync<IdeHelloParams, IdeHelloResult>(Methods.IdeHello,
            new IdeHelloParams("test", "1.0", [folder], app.Hub.Ide.Token));
        Assert.Equal(app.Hub.Port, hello.Port);
        return ide;
    }

    [Fact]
    public void Token_file_is_private_and_matches()
    {
        var path = IdeEndpoint.TokenFilePath(app.Hub.Port);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(app.Hub.Ide.Token, doc.RootElement.GetProperty("token").GetString());
        Assert.Equal(app.Hub.Port, doc.RootElement.GetProperty("port").GetInt32());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task Xaml_completion_needs_hello_then_answers_over_the_wire()
    {
        // The test project's own HotReloadView.axaml: Avalonia types from the test project's references.
        var text = Original.Replace("<TextBox x:Name=\"Input\" />", "<TextBox x:Name=\"Input\" />\n    <Bu");
        var offset = text.IndexOf("<Bu", StringComparison.Ordinal) + 3;
        var p = new XamlCompletionParams(SourcePath, text, offset);

        var (anonymous, _) = await Open();
        var ex = await Assert.ThrowsAsync<SpyglassProtocolException>(() =>
            anonymous.Peer.RequestAsync<XamlCompletionParams, XamlCompletionResult>(Methods.XamlCompletion, p));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);

        var (ide, _) = await Open();
        var hello = await ide.Peer.RequestAsync<IdeHelloParams, IdeHelloResult>(Methods.IdeHello,
            new IdeHelloParams("test", "1.0", [], app.Hub.Ide.Token));
        Assert.Contains(Capabilities.XamlLanguage, hello.Capabilities);
        await ide.Peer.NotifyAsync(Methods.XamlPrepare, new XamlPrepareParams(SourcePath));

        XamlCompletionResult r;
        var until = DateTime.UtcNow.AddMinutes(2);
        do r = await ide.Peer.RequestAsync<XamlCompletionParams, XamlCompletionResult>(Methods.XamlCompletion, p);
        while (r.Incomplete && DateTime.UtcNow < until);   // the first request may find the project still loading
        Assert.Null(r.Message);
        Assert.Equal(offset - 2, r.From);
        var button = Assert.Single(r.Items, i => i.Label == "Button");
        Assert.Equal(("element", "T:Avalonia.Controls.Button"), (button.Kind, button.DocId));

        var doc = await ide.Peer.RequestAsync<XamlDocumentationParams, XamlDocumentationResult>(Methods.XamlDocumentation,
            new XamlDocumentationParams(SourcePath, button.DocId!));
        Assert.False(string.IsNullOrWhiteSpace(doc.Markdown));
    }

    [Fact]
    public async Task Wrong_token_is_rejected()
    {
        var (ide, _) = await Open();
        var ex = await Assert.ThrowsAsync<SpyglassProtocolException>(() =>
            ide.Peer.RequestAsync<IdeHelloParams, IdeHelloResult>(Methods.IdeHello, new IdeHelloParams("test", "1.0", [], "nope")));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.Empty(app.Hub.Ide.Connections);
    }

    [Fact]
    public async Task Browser_origin_is_refused()
    {
        await Assert.ThrowsAsync<WebSocketException>(() => Open(origin: "http://evil.example"));
    }

    [Fact]
    public async Task Status_after_hello_lists_reloadable_files_and_apps()
    {
        var ide = await Connect();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var status = await ide.Status.Reader.ReadAsync(cts.Token);
        Assert.Contains(SourcePath, status.Files);
        Assert.Contains(status.Apps, a => a.HotReload && a.Available && a.Documents > 0);
    }

    [Fact]
    public async Task Unsaved_buffer_reloads_the_app_and_errors_come_back_as_diagnostics()
    {
        var ide = await Connect();
        await ide.Peer.NotifyAsync(Methods.XamlDidOpen, new XamlDocumentParams(SourcePath, 1, Original));

        await ide.Peer.NotifyAsync(Methods.XamlDidChange, new XamlDocumentParams(SourcePath, 2, WithSubtitle));
        var ok = await ide.NextDiagnostics(d => d.Version == 2);
        Assert.Empty(ok.Items);
        Assert.StartsWith("⚡", ok.Summary);
        Assert.Equal("World", await app.OnUi(() => _view.FindControl<TextBlock>("Subtitle")?.Text));

        var bad = WithSubtitle.Replace("{Binding Subtitle}", "{Binding Subtitel}");
        await ide.Peer.NotifyAsync(Methods.XamlDidChange, new XamlDocumentParams(SourcePath, 3, bad));
        var err = await ide.NextDiagnostics(d => d.Version == 3);
        var item = Assert.Single(err.Items);
        Assert.Equal(XamlDiagnosticSeverity.Error, item.Severity);
        Assert.Equal(bad[..bad.IndexOf("Subtitel", StringComparison.Ordinal)].Count(c => c == '\n') + 1, item.Line);
        Assert.Equal("World", await app.OnUi(() => _view.FindControl<TextBlock>("Subtitle")?.Text));   // app untouched
    }

    [Fact]
    public async Task Open_editor_owns_the_file_until_closed_then_disk_wins()
    {
        var ide = await Connect();
        await ide.Peer.NotifyAsync(Methods.XamlDidOpen, new XamlDocumentParams(SourcePath, 1, Original));
        await WaitUntil(() => app.Hub.Ide.Connections.Any(c => c.Hello.Ide == "test"), "connected");
        await Task.Delay(100);

        // Disk changes while the editor has the file: the buffer is the truth, the watcher stays quiet.
        await File.WriteAllTextAsync(SourcePath, WithSubtitle);
        await Task.Delay(600);
        Assert.Null(await app.OnUi(() => _view.FindControl<TextBlock>("Subtitle")));

        // Closing the editor hands the file back: the different disk content is applied.
        await ide.Peer.NotifyAsync(Methods.XamlDidClose, new XamlDocumentParams(SourcePath, 1));
        await WaitUntilAsync(async () => await app.OnUi(() => _view.FindControl<TextBlock>("Subtitle")) is not null, "disk applied after close");
    }

    [Fact]
    public async Task Reveal_goes_to_the_ide_whose_workspace_has_the_file()
    {
        var ide = await Connect();
        Assert.True(app.Hub.Ide.HasEditorFor(SourcePath));
        Assert.False(app.Hub.Ide.HasEditorFor("/somewhere/else/View.axaml"));
        Assert.True(await app.Hub.Ide.RevealAsync(SourcePath, 7, 5));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var r = await ide.Reveals.Reader.ReadAsync(cts.Token);
        Assert.Equal((SourcePath, 7, 5), (r.Path, r.Line, r.Column));
    }

    static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), $"timed out waiting for: {what}");
    }

    static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail($"timed out waiting for: {what}");
    }
}
