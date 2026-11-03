using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Spyglass.App.Core;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>
/// XAML hot reload end to end (design/hot-reload/README.md §4, §6, §7): generated manifest, in-place re-populate with state
/// kept, compile errors, root attribute/handler cleanup, new instances, and the hub's file watcher.
/// </summary>
[Collection("app")]
public class HotReloadTests(AppFixture app) : IAsyncLifetime
{
    const string Uri = "avares://Spyglass.IntegrationTests/HotReloadView.axaml";
    static readonly string SourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../HotReloadView.axaml"));
    static readonly string Original = File.ReadAllText(SourcePath);

    Window _window = null!;
    HotReloadView _view = null!;

    public async ValueTask InitializeAsync()
    {
        // Every test starts from the compiled XAML (skipped by the agent when it already is).
        await app.Session.ApplyXamlAsync(Uri, Original);
        (_window, _view) = await app.OnUi(() =>
        {
            var view = new HotReloadView { DataContext = new HotReloadViewModel() };
            var w = new Window { Title = "Hot reload test", Width = 300, Height = 200, Content = view };
            w.Show();
            return (w, view);
        });
    }

    public async ValueTask DisposeAsync()
    {
        await app.Session.ApplyXamlAsync(Uri, Original);
        await app.OnUi(() => { _window.Close(); return 0; });
    }

    static string Edit(string from, string to)
    {
        Assert.Contains(from, Original);
        return Original.Replace(from, to);
    }

    const string AddSubtitle = """<TextBlock x:Name="Subtitle" Text="{Binding Subtitle}" />""";
    static string WithSubtitle => Edit("""<TextBox x:Name="Input" />""", AddSubtitle + "\n    <TextBox x:Name=\"Input\" />");

    [Fact]
    public async Task Manifest_lists_generated_documents_with_source_paths()
    {
        Assert.True(app.Session.Has(Capabilities.HotReload));
        var m = await app.Session.GetHotReloadManifestAsync();
        Assert.True(m.Available, m.Reason);
        var doc = Assert.Single(m.Documents, d => d.Uri == Uri);
        Assert.Equal("Spyglass.IntegrationTests.HotReloadView", doc.Class);
        Assert.Equal(SourcePath, doc.Path);
        Assert.Contains(m.Documents, d => d.Class == "Spyglass.IntegrationTests.TestWindow");
    }

    [Fact]
    public async Task Apply_repopulates_live_instance_in_place_and_keeps_state()
    {
        var before = await app.OnUi(() =>
        {
            _view.FindControl<TextBox>("Input")!.Text = "typed by the user";
            return _view.DataContext;
        });

        var r = await app.Session.ApplyXamlAsync(Uri, WithSubtitle);

        Assert.True(r.Ok, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        Assert.True(r.Instances >= 1);
        var (sameView, dc, subtitle, input) = await app.OnUi(() => (
            ReferenceEquals(_window.Content, _view),
            _view.DataContext,
            _view.FindControl<TextBlock>("Subtitle")?.Text,
            _view.FindControl<TextBox>("Input")!.Text));
        Assert.True(sameView);
        Assert.Same(before, dc);
        Assert.Equal("World", subtitle);                 // compiled binding works under the runtime loader
        Assert.Equal("typed by the user", input);        // StateKeeper restored the unbound text
    }

    [Fact]
    public async Task Same_content_is_skipped()
    {
        var r = await app.Session.ApplyXamlAsync(Uri, Original);
        Assert.True(r.Ok);
        Assert.Equal("same content", r.Skipped);
    }

    [Fact]
    public async Task Compile_error_reports_position_and_leaves_view_unchanged()
    {
        var bad = Edit("""Text="{Binding Title}" """, """Text="{Binding Titel}" """);
        var r = await app.Session.ApplyXamlAsync(Uri, bad);

        Assert.False(r.Ok);
        Assert.False(r.RolledBack);
        var d = Assert.Single(r.Diagnostics, d => d.Severity == XamlDiagnosticSeverity.Error);
        Assert.Contains("Titel", d.Message);
        Assert.Equal(LineOf(bad, "Titel"), d.Line);
        Assert.Equal("Hello", await app.OnUi(() => _view.FindControl<TextBlock>("Title")!.Text));
    }

    [Fact]
    public async Task Malformed_xml_reports_position()
    {
        var bad = Edit("<TextBox x:Name=\"Input\" />", "<TextBox x:Name=\"Input\" >");
        var r = await app.Session.ApplyXamlAsync(Uri, bad);
        Assert.False(r.Ok);
        Assert.NotNull(Assert.Single(r.Diagnostics).Line);
    }

    [Fact]
    public async Task Removed_root_attribute_is_cleared_and_root_handlers_are_not_duplicated()
    {
        var noPadding = Edit("""Padding="4" """, "");
        Assert.True((await app.Session.ApplyXamlAsync(Uri, noPadding)).Ok);
        Assert.Equal(new Thickness(0), await app.OnUi(() => _view.Padding));

        Assert.True((await app.Session.ApplyXamlAsync(Uri, WithSubtitle)).Ok);
        Assert.True((await app.Session.ApplyXamlAsync(Uri, Original)).Ok);
        var clicks = await app.OnUi(() =>
        {
            _view.Clicks = 0;
            _view.FindControl<Button>("Go")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return _view.Clicks;
        });
        Assert.Equal(1, clicks);
        Assert.Equal(new Thickness(4), await app.OnUi(() => _view.Padding));
    }

    [Fact]
    public async Task Removing_a_name_the_code_behind_uses_is_rejected_and_changes_nothing()
    {
        var r = await app.Session.ApplyXamlAsync(Uri, Edit("""<Button x:Name="Go" Content="Go" />""", ""));

        Assert.False(r.Ok);
        Assert.False(r.RestartRequired);
        var d = Assert.Single(r.Diagnostics, d => d.Severity == XamlDiagnosticSeverity.Error);
        Assert.Contains("\"Go\"", d.Message);
        Assert.Contains("HotReloadView.ctor", d.Message);
        Assert.NotNull(await app.OnUi(() => _view.FindControl<Button>("Go")));

        // Names the code does not use can still go.
        Assert.True((await app.Session.ApplyXamlAsync(Uri, Edit("""<TextBox x:Name="Input" />""", ""))).Ok);
    }

    [Fact]
    public async Task Values_and_handlers_set_by_code_behind_survive_a_reload()
    {
        var oldGo = await app.OnUi(() =>
        {
            _view.FindControl<TextBlock>("Styled")!.IsVisible = false;          // not in the XAML
            _view.FindControl<Button>("Go")!.Content = "Changed";               // overrides the XAML literal
            ToolTip.SetTip(_view.FindControl<Button>("Go")!, "tip");            // attached
            return _view.FindControl<Button>("Go")!;
        });

        Assert.True((await app.Session.ApplyXamlAsync(Uri, WithSubtitle)).Ok);

        var (newGo, visible, content, tip, goClicks, clicks) = await app.OnUi(() =>
        {
            var go = _view.FindControl<Button>("Go")!;
            _view.GoClicks = 0;
            _view.Clicks = 0;
            go.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return (go, _view.FindControl<TextBlock>("Styled")!.IsVisible, go.Content, ToolTip.GetTip(go), _view.GoClicks, _view.Clicks);
        });
        Assert.NotSame(oldGo, newGo);
        Assert.False(visible);
        Assert.Equal("Changed", content);
        Assert.Equal("tip", tip);
        Assert.Equal(1, goClicks);   // the constructor's Click subscription, once
        Assert.Equal(1, clicks);     // the root's XAML handler, not duplicated

        // An edit of that attribute wins over the code-set value.
        Assert.True((await app.Session.ApplyXamlAsync(Uri, Edit("""Content="Go" """, """Content="Run" """))).Ok);
        Assert.Equal("Run", await app.OnUi(() => _view.FindControl<Button>("Go")!.Content));
    }

    [Fact]
    public async Task New_instances_get_the_new_xaml_and_reverting_restores_compiled()
    {
        Assert.True((await app.Session.ApplyXamlAsync(Uri, WithSubtitle)).Ok);
        Assert.True(await app.OnUi(() => new HotReloadView().FindControl<TextBlock>("Subtitle") is not null));

        Assert.True((await app.Session.ApplyXamlAsync(Uri, Original)).Ok);
        Assert.True(await app.OnUi(() => new HotReloadView().FindControl<TextBlock>("Subtitle") is null));
    }

    [Fact]
    public async Task One_compile_per_edit_whatever_the_number_of_instances()
    {
        // Types the runtime XAML compiler emitted: a compile per instance would grow this with the instance count.
        async Task<int> EmittedBy(Func<Task> apply)
        {
            var before = await app.OnUi(Spyglass.Agent.HotReload.EmittedXaml.Count);
            await apply();
            return await app.OnUi(Spyglass.Agent.HotReload.EmittedXaml.Count) - before;
        }
        // A failed compile first: it leaves types behind that can never load (CI caught reuse silently stopping after one).
        Assert.False((await app.Session.ApplyXamlAsync(Uri, Edit("{Binding Title}", "{Binding Titel}"))).Ok);
        var extra = await app.OnUi(() =>
        {
            var panel = new StackPanel();
            for (var i = 0; i < 4; i++) panel.Children.Add(new HotReloadView { DataContext = new HotReloadViewModel() });
            var w = new Window { Content = panel };
            w.Show();
            return w;
        });
        try
        {
            var five = await EmittedBy(async () => Assert.Equal(5, (await app.Session.ApplyXamlAsync(Uri, Edit("""Content="Go" """, """Content="Five" """))).Instances));
            await app.OnUi(() => { extra.Close(); return 0; });
            var one = await EmittedBy(async () => Assert.Equal(1, (await app.Session.ApplyXamlAsync(Uri, Edit("""Content="Go" """, """Content="One" """))).Instances));
            Assert.True(one > 0);
            Assert.Equal(one, five);

            // New instances after the reload reuse the compiled XAML too.
            var created = await EmittedBy(() => app.OnUi(() =>
            {
                for (var i = 0; i < 10; i++) Assert.Equal("One", new HotReloadView().FindControl<Button>("Go")!.Content);
                return 0;
            }));
            Assert.Equal(0, created);
        }
        finally { await app.OnUi(() => { extra.Close(); return 0; }); }
    }

    [Fact]
    public async Task Changing_xclass_requires_restart()
    {
        var r = await app.Session.ApplyXamlAsync(Uri, Edit("x:Class=\"Spyglass.IntegrationTests.HotReloadView\"", "x:Class=\"Spyglass.IntegrationTests.Other\""));
        Assert.False(r.Ok);
        Assert.True(r.RestartRequired);
    }

    [Fact]
    public async Task Unknown_document_is_an_error()
    {
        var ex = await Assert.ThrowsAsync<SpyglassProtocolException>(() => app.Session.ApplyXamlAsync("avares://Nope/Nope.axaml", "<x/>"));
        Assert.Equal(ErrorCodes.XamlDocumentUnknown, ex.Code);
    }

    [Fact]
    public async Task Applied_notification_reaches_the_hub()
    {
        var got = new TaskCompletionSource<HotReloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(HotReloadResult r) { if (r.Uri == Uri && r.Ok) got.TrySetResult(r); }
        app.Session.HotReloadApplied += On;
        try
        {
            await app.Session.ApplyXamlAsync(Uri, WithSubtitle);
            var r = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HotReloadSource.Direct, r.Source);
        }
        finally { app.Session.HotReloadApplied -= On; }
    }

    [Fact]
    public async Task Hub_watches_the_source_file_and_pushes_saved_changes()
    {
        await WaitUntil(() => app.Hub.HotReload.Targets(SourcePath).Count > 0, "manifest routed to the session");
        var got = new TaskCompletionSource<HotReloadEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(HotReloadEvent e) { if (e.Result.Uri == Uri && e.Result.Ok && e.Result.Instances > 0) got.TrySetResult(e); }
        app.Hub.HotReload.Reloaded += On;
        try
        {
            await File.WriteAllTextAsync(SourcePath, WithSubtitle);
            var e = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(SourcePath, e.Path);
            Assert.Equal("World", await app.OnUi(() => _view.FindControl<TextBlock>("Subtitle")?.Text));
        }
        finally
        {
            app.Hub.HotReload.Reloaded -= On;
            await File.WriteAllTextAsync(SourcePath, Original);
            await app.SettleHotReloadAsync();   // the watcher's reload of the restored file must not land in a later test
        }
    }

    [Fact]
    public async Task Fixing_an_error_back_to_the_live_text_is_recorded_as_skipped()
    {
        await WaitUntil(() => app.Hub.HotReload.Targets(SourcePath).Count > 0, "manifest routed to the session");
        var bad = await app.Hub.HotReload.ApplyTextAsync(SourcePath, Edit("Spacing=\"4\"", "Spacing=\"four\""));
        Assert.False(Assert.Single(bad).Result.Ok);
        var fixedBack = Assert.Single(await app.Hub.HotReload.ApplyTextAsync(SourcePath, Original));
        Assert.Equal("same content", fixedBack.Result.Skipped);
        await WaitUntil(() => app.Hub.HotReload.History.LastOrDefault()?.Result.Skipped is not null, "skip recorded");
    }

    static int LineOf(string text, string needle) =>
        text[..text.IndexOf(needle, StringComparison.Ordinal)].Count(c => c == '\n') + 1;

    static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), $"timed out waiting for: {what}");
    }
}
