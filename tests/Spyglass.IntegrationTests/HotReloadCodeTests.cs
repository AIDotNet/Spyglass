using System.CodeDom.Compiler;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Spyglass.Agent.HotReload;
using Spyglass.App.Core;
using Spyglass.Protocol.Messages;
using Xunit;

namespace Spyglass.Generated
{
    /// <summary>Stands in for a stamp recreated by a .NET Hot Reload delta (same shape as the generator's).</summary>
    internal static class SpyglassXamlStamp_testdelta
    {
        public const string Uri = "avares://Spyglass.IntegrationTests/HotReloadView.axaml";
        public const string? Class = "Spyglass.IntegrationTests.HotReloadView";
        public static string Current = "";
        public static string Text() => Current;
    }
}

namespace Spyglass.IntegrationTests
{
    /// <summary>
    /// HR3 (design/hot-reload/README.md §5, §5.1, §6): the MetadataUpdateHandler applies XAML stamps and refreshes bindings
    /// after C# deltas; Spyglass's own InitializeComponent and property-style x:Name; an editor's buffer beats a stamp.
    /// The handler is invoked directly (no dotnet watch in the test host); the real delta path is covered by the sample
    /// e2e in design/hot-reload §16.
    /// </summary>
    [Collection("app")]
    public class HotReloadCodeTests(AppFixture app) : IAsyncLifetime
    {
        const string Uri = "avares://Spyglass.IntegrationTests/HotReloadView.axaml";
        static readonly string SourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../HotReloadView.axaml"));
        static readonly string Original = File.ReadAllText(SourcePath);
        static readonly string WithSubtitle = Original.Replace("""<TextBox x:Name="Input" />""",
            """<TextBlock x:Name="Subtitle" Text="{Binding Subtitle}" />""" + "\n    <TextBox x:Name=\"Input\" />");

        Window _window = null!;
        HotReloadView _view = null!;
        HotReloadViewModel _vm = null!;

        public async ValueTask InitializeAsync()
        {
            await app.Session.ApplyXamlAsync(Uri, Original);
            (_window, _view, _vm) = await app.OnUi(() =>
            {
                var vm = new HotReloadViewModel();
                var view = new HotReloadView { DataContext = vm };
                var w = new Window { Content = view };
                w.Show();
                return (w, view, vm);
            });
        }

        public async ValueTask DisposeAsync()
        {
            await app.Session.ApplyXamlAsync(Uri, Original);
            await app.OnUi(() => { _window.Close(); return 0; });
        }

        static Task Delta(params Type[] types) =>
            Dispatcher.UIThread.InvokeAsync(() => SpyglassMetadataUpdateHandler.HandleAsync(types));

        [Fact]
        public void Views_use_spyglass_initialize_component_and_property_names()
        {
            var init = typeof(HotReloadView).GetMethod("InitializeComponent", [typeof(bool)])!;
            Assert.Equal("Spyglass.HotReload.Generators", init.GetCustomAttribute<GeneratedCodeAttribute>()!.Tool);
            Assert.NotNull(typeof(HotReloadView).GetProperty("Title", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
            Assert.Null(typeof(HotReloadView).GetField("Title", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
        }

        [Fact]
        public async Task Property_names_follow_a_reload()
        {
            Assert.True((await app.Session.ApplyXamlAsync(Uri, WithSubtitle)).Ok);
            var (viaProperty, viaScope) = await app.OnUi(() =>
            {
                var p = typeof(HotReloadView).GetProperty("Input", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
                return (p.GetValue(_view), _view.FindControl<TextBox>("Input"));
            });
            Assert.Same(viaScope, viaProperty);
        }

        [Fact]
        public async Task Stamp_in_a_delta_reloads_the_view_and_is_reported_as_stamp()
        {
            var got = new TaskCompletionSource<HotReloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            void On(HotReloadResult r) { if (r.Source == HotReloadSource.Stamp && r.Ok) got.TrySetResult(r); }
            app.Session.HotReloadApplied += On;
            try
            {
                Spyglass.Generated.SpyglassXamlStamp_testdelta.Current = WithSubtitle;
                await Delta(typeof(Spyglass.Generated.SpyglassXamlStamp_testdelta));
                Assert.Equal("World", await app.OnUi(() => _view.FindControl<TextBlock>("Subtitle")?.Text));
                Assert.True((await got.Task.WaitAsync(TimeSpan.FromSeconds(5))).Instances >= 1);
            }
            finally { app.Session.HotReloadApplied -= On; }
        }

        [Fact]
        public async Task Code_delta_refreshes_bindings_of_changed_view_models()
        {
            var got = new TaskCompletionSource<HotReloadCodeUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            void On(HotReloadCodeUpdate u) => got.TrySetResult(u);
            app.Session.HotReloadCodeUpdated += On;
            try
            {
                // A changed getter body looks like this to the UI: the value differs, nobody raised PropertyChanged.
                await app.OnUi(() => { _vm.Title = "Changed by C#"; return 0; });
                Assert.Equal("Hello", await app.OnUi(() => _view.FindControl<TextBlock>("Title")!.Text));

                await Delta(typeof(HotReloadViewModel));

                Assert.Equal("Changed by C#", await app.OnUi(() => _view.FindControl<TextBlock>("Title")!.Text));
                var u = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Contains(typeof(HotReloadViewModel).FullName, u.Types);
                Assert.True(u.Refreshed >= 1);
                Assert.Empty(u.Warnings);
                await WaitUntil(() => app.Hub.HotReload.History.Any(e => e.Path == HotReloadCoordinator.CodePath), "C# delta recorded by the hub");
            }
            finally { app.Session.HotReloadCodeUpdated -= On; }
        }

        [Fact]
        public async Task Unchanged_view_models_are_left_alone()
        {
            var got = new TaskCompletionSource<HotReloadCodeUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            void On(HotReloadCodeUpdate u) => got.TrySetResult(u);
            app.Session.HotReloadCodeUpdated += On;
            try
            {
                await app.OnUi(() => { _vm.Title = "Not refreshed"; return 0; });
                await Delta(typeof(AppFixture));   // some other type changed
                Assert.Equal("Hello", await app.OnUi(() => _view.FindControl<TextBlock>("Title")!.Text));
                Assert.Equal(0, (await got.Task.WaitAsync(TimeSpan.FromSeconds(5))).Refreshed);
            }
            finally { app.Session.HotReloadCodeUpdated -= On; }
        }

        [Fact]
        public async Task Editor_buffer_wins_over_a_stamp_with_the_saved_file()
        {
            await WaitUntil(() => app.Hub.HotReload.Targets(SourcePath).Count > 0, "manifest");
            app.Hub.HotReload.ClaimForEditor(SourcePath);
            try
            {
                Assert.True(Assert.Single(await app.Hub.HotReload.ApplyTextAsync(SourcePath, WithSubtitle)).Result.Ok);
                // dotnet watch delivers the file as saved on disk (no Subtitle) after the buffer was applied.
                Spyglass.Generated.SpyglassXamlStamp_testdelta.Current = Original;
                await Delta(typeof(Spyglass.Generated.SpyglassXamlStamp_testdelta));
                await WaitUntilAsync(async () => await app.OnUi(() => _view.FindControl<TextBlock>("Subtitle")) is not null, "buffer re-applied");
            }
            finally
            {
                // Releasing re-applies the file on disk ~100 ms later (the editor closed: disk wins); let it land here,
                // not in the next test (it reverted the stamp test's view on Windows CI).
                app.Hub.HotReload.ReleaseFromEditor(SourcePath);
                await app.SettleHotReloadAsync();
            }
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
}
