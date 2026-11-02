using Spyglass.VisualStudio.Core;
using Xunit;

namespace Spyglass.VisualStudio.Core.Tests;

/// <summary>Editor buffers → hub: which files are sent, debounce, save-only mode, close, reconnect, toggle.</summary>
[Collection("fakehub")]
public sealed class XamlBufferSyncTests : IAsyncLifetime
{
    readonly string _home = Wait.TempHome();
    readonly string _main;
    readonly string _other;
    FakeHub _hub = null!;
    HubClient _client = null!;
    XamlBufferSync _sync = null!;

    public XamlBufferSyncTests()
    {
        _main = Path.Combine(_home, "App", "MainWindow.axaml");
        _other = Path.Combine(_home, "App", "NotCompiled.axaml");
    }

    public async ValueTask InitializeAsync()
    {
        _hub = await FakeHub.StartAsync(HubClientTests.Port, _home);
        _hub.InitialStatus = new HotReloadStatusParams([new("a1", "Sample", 1, true, true, null, 1)], [_main]);
        _client = new HubClient(new HubClientOptions
        {
            Port = HubClientTests.Port,
            Home = _home,
            AutoStart = false,
            RetryDelay = TimeSpan.FromMilliseconds(200),
            MaxRetryDelay = TimeSpan.FromMilliseconds(800),
        });
        _sync = new XamlBufferSync(_client) { DelayMs = 150 };
    }

    public async ValueTask DisposeAsync()
    {
        _sync.Dispose();
        await _client.DisposeAsync();
        await _hub.DisposeAsync();
        try { Directory.Delete(_home, true); } catch { }
    }

    async Task ConnectAsync()
    {
        _client.Start();
        await Wait.UntilAsync(() => _client.CanReload(_main), "status");
    }

    [Fact]
    public async Task Documents_open_before_connecting_are_announced_once_the_status_lists_them()
    {
        _sync.Opened(_main, () => "<Window/>", isDirty: false);
        _sync.Opened(_other, () => "<UserControl/>", isDirty: false);
        _sync.Opened(Path.Combine(_home, "App", "MainWindow.axaml.cs"), () => "class C {}", isDirty: false);
        await ConnectAsync();

        var (method, p) = await _hub.NextDocumentAsync();
        Assert.Equal((IdeMethods.DidOpen, _main, "<Window/>"), (method, p.Path, p.Text));
        Assert.True(await _hub.QuietAsync(), "only the file an app compiled is sent, and it is not dirty");
    }

    [Fact]
    public async Task Typing_is_debounced_into_one_full_text_didChange()
    {
        await ConnectAsync();
        _sync.Opened(_main, () => "v1", isDirty: false);
        Assert.Equal(IdeMethods.DidOpen, (await _hub.NextDocumentAsync()).Method);

        _sync.Changed(_main, () => "v2", isDirty: true);
        await Task.Delay(50);
        _sync.Changed(_main, () => "v3", isDirty: true);
        await Task.Delay(50);
        _sync.Changed(_main, () => "v4", isDirty: true);

        var (method, p) = await _hub.NextDocumentAsync();
        Assert.Equal((IdeMethods.DidChange, "v4", 4L), (method, p.Text, p.Version));
        Assert.True(await _hub.QuietAsync());
    }

    [Fact]
    public async Task Edits_of_files_no_app_compiled_are_never_sent()
    {
        await ConnectAsync();
        _sync.Opened(_other, () => "a", isDirty: false);
        _sync.Changed(_other, () => "b", isDirty: true);
        _sync.Saved(_other);
        _sync.Closed(_other);
        Assert.True(await _hub.QuietAsync());
    }

    [Fact]
    public async Task With_delay_zero_only_saving_sends()
    {
        _sync.DelayMs = 0;
        await ConnectAsync();
        _sync.Opened(_main, () => "v1", isDirty: false);
        await _hub.NextDocumentAsync();

        _sync.Changed(_main, () => "v2", isDirty: true);
        Assert.True(await _hub.QuietAsync(400));
        _sync.Saved(_main, () => "v2 saved");
        var (method, p) = await _hub.NextDocumentAsync();
        Assert.Equal((IdeMethods.DidChange, "v2 saved"), (method, p.Text));
    }

    [Fact]
    public async Task Closing_sends_didClose_and_cancels_a_pending_change()
    {
        await ConnectAsync();
        _sync.Opened(_main, () => "v1", isDirty: false);
        await _hub.NextDocumentAsync();
        _sync.Changed(_main, () => "v2", isDirty: true);
        _sync.Closed(_main);
        var (method, p) = await _hub.NextDocumentAsync();
        Assert.Equal((IdeMethods.DidClose, _main, null), (method, p.Path, p.Text));
        Assert.True(await _hub.QuietAsync());
    }

    [Fact]
    public async Task After_a_reconnect_open_documents_are_announced_again_with_unsaved_text()
    {
        await ConnectAsync();
        _sync.Opened(_main, () => "saved", isDirty: false);
        await _hub.NextDocumentAsync();
        _sync.Changed(_main, () => "unsaved", isDirty: true);
        Assert.Equal("unsaved", (await _hub.NextDocumentAsync()).Params.Text);

        await _hub.DropAllAsync();
        await Wait.UntilAsync(() => _hub.Accepted == 2, "reconnected");
        var open = await _hub.NextDocumentAsync();
        var change = await _hub.NextDocumentAsync();
        Assert.Equal((IdeMethods.DidOpen, "unsaved"), (open.Method, open.Params.Text));
        Assert.Equal((IdeMethods.DidChange, "unsaved"), (change.Method, change.Params.Text));
        Assert.True(await _hub.QuietAsync());
    }

    [Fact]
    public async Task A_file_becoming_reloadable_later_is_announced_then()
    {
        await ConnectAsync();
        _sync.Opened(_other, () => "x", isDirty: false);
        Assert.True(await _hub.QuietAsync(300));
        _hub.SendStatus(new HotReloadStatusParams([], [_main, _other.ToUpperInvariant()]));
        var (method, p) = await _hub.NextDocumentAsync();
        Assert.Equal((IdeMethods.DidOpen, _other), (method, p.Path));
    }

    [Fact]
    public async Task Turning_hot_reload_off_closes_documents_and_on_announces_them_again()
    {
        await ConnectAsync();
        _sync.Opened(_main, () => "v1", isDirty: false);
        await _hub.NextDocumentAsync();

        _sync.Enabled = false;
        Assert.Equal(IdeMethods.DidClose, (await _hub.NextDocumentAsync()).Method);
        _sync.Changed(_main, () => "v2", isDirty: true);
        Assert.True(await _hub.QuietAsync(400));

        _sync.Enabled = true;
        var open = await _hub.NextDocumentAsync();
        Assert.Equal((IdeMethods.DidOpen, "v2"), (open.Method, open.Params.Text));
        Assert.Equal(IdeMethods.DidChange, (await _hub.NextDocumentAsync()).Method);   // dirty
    }

    [Fact]
    public void Only_xaml_files_take_part()
    {
        Assert.True(XamlBufferSync.IsXaml("/a/MainWindow.axaml"));
        Assert.True(XamlBufferSync.IsXaml(@"C:\a\App.XAML"));
        Assert.False(XamlBufferSync.IsXaml("/a/MainWindow.axaml.cs"));
    }

    [Fact]
    public async Task Debouncer_runs_once_per_burst_per_key()
    {
        var runs = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var d = new Debouncer(runs.Add);
        for (var i = 0; i < 5; i++) { d.Schedule("a", TimeSpan.FromMilliseconds(100)); d.Schedule("b", TimeSpan.FromMilliseconds(100)); await Task.Delay(20); }
        d.Schedule("c", TimeSpan.FromMilliseconds(100));
        d.Cancel("c");
        await Task.Delay(400);
        Assert.Equal(["a", "b"], runs.OrderBy(x => x));
    }

    [Fact]
    public void Status_text_mirrors_the_vs_code_status_bar()
    {
        Assert.Equal(HubStatusKind.Off, HubStatusPresenter.Describe(false, HubConnectionState.Connected, 1, null, null, true).Kind);
        Assert.Equal(HubStatusKind.Disconnected, HubStatusPresenter.Describe(true, HubConnectionState.Disconnected, 1, null, null, true).Kind);
        var status = new HotReloadStatusParams([new("a", "Sample", 5, true, true, null, 2), new("b", "Tool", 6, false, false, null, 0)], [], "⚡ last", true);
        var live = HubStatusPresenter.Describe(true, HubConnectionState.Connected, 1, status, null, true);
        Assert.Equal((HubStatusKind.Live, "Spyglass: Sample"), (live.Kind, live.Text));
        Assert.Contains("Tool (pid 6): no hot reload (add Spyglass.HotReload)", live.Details);
        Assert.Equal("⚡ last", live.Details[^1]);
        Assert.Equal(HubStatusKind.Error, HubStatusPresenter.Describe(true, HubConnectionState.Connected, 1, status, "✗ bad", false).Kind);
    }
}

public sealed class DiagnosticRangeTests
{
    const string Xaml = "<Window>\r\n  <StackPanel>\n    <TextBlock Text=\"{Binding Titel}\" />\n  </StackPanel>\n</Window>";

    [Fact]
    public void Squiggle_covers_the_word_at_the_one_based_position()
    {
        var col = "    <TextBlock Text=\"{Binding Titel}\" />".IndexOf("Titel", StringComparison.Ordinal) + 1;
        Assert.Equal(new TextSpan(2, 30, 2, 36), DiagnosticRange.Compute(Xaml, 3, col));   // "Titel}"
    }

    [Fact]
    public void Without_a_word_or_column_the_line_is_marked_and_without_text_one_character()
    {
        Assert.Equal(new TextSpan(1, 2, 1, 14), DiagnosticRange.Compute(Xaml, 2, null));   // "<StackPanel>", \r stripped
        Assert.Equal(new TextSpan(7, 3, 7, 4), DiagnosticRange.Compute(Xaml, 8, 4));      // past the end
        Assert.Equal(new TextSpan(0, 0, 0, 1), DiagnosticRange.Compute(null, null, null));
    }
}
