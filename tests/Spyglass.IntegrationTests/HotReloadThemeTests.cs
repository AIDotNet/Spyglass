using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Spyglass.Protocol.Messages;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>
/// HR5 (design/hot-reload/README.md §4.2, spike S8): classless Styles/ResourceDictionary documents (StyleInclude,
/// ResourceInclude, MergeResourceInclude), StaticResource consumers, App.axaml.
/// </summary>
[Collection("app")]
public class HotReloadThemeTests(AppFixture app) : IAsyncLifetime
{
    const string Asm = "avares://Spyglass.IntegrationTests/";
    static string Source(string file) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../", file));
    static readonly string[] Files = ["HotReloadView.axaml", "HotReloadStyles.axaml", "HotReloadColors.axaml", "HotReloadMerged.axaml", "TestApp.axaml"];
    static readonly Dictionary<string, string> Original = Files.ToDictionary(f => f, f => File.ReadAllText(Source(f)));

    Window _window = null!;
    HotReloadView _view = null!;

    async Task Reset()
    {
        foreach (var f in Files) await app.Session.ApplyXamlAsync(Asm + f, Original[f]);
    }

    public async ValueTask InitializeAsync()
    {
        await Reset();
        (_window, _view) = await app.OnUi(() =>
        {
            var view = new HotReloadView { DataContext = new HotReloadViewModel() };
            var w = new Window { Content = view };
            w.Show();
            return (w, view);
        });
    }

    public async ValueTask DisposeAsync()
    {
        await Reset();
        await app.OnUi(() => { _window.Close(); return 0; });
    }

    static string Edit(string file, string from, string to)
    {
        Assert.Contains(from, Original[file]);
        return Original[file].Replace(from, to);
    }

    Task<Color?> BrushOf(string name) => app.OnUi(() => (_view.FindControl<Border>(name)!.Background as ISolidColorBrush)?.Color);

    [Fact]
    public async Task Manifest_has_classless_documents()
    {
        var m = await app.Session.GetHotReloadManifestAsync();
        Assert.Null(Assert.Single(m.Documents, d => d.Uri == Asm + "HotReloadStyles.axaml").Class);
        Assert.Contains(m.Documents, d => d.Uri == Asm + "HotReloadMerged.axaml");
    }

    [Fact]
    public async Task Styles_document_reloads_in_place()
    {
        Assert.Equal(20, await app.OnUi(() => _view.FindControl<TextBlock>("Styled")!.FontSize));
        var r = await app.Session.ApplyXamlAsync(Asm + "HotReloadStyles.axaml", Edit("HotReloadStyles.axaml", "Value=\"20\"", "Value=\"33\""));
        Assert.True(r.Ok, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        Assert.True(r.Instances >= 1);
        Assert.Equal(33, await app.OnUi(() => _view.FindControl<TextBlock>("Styled")!.FontSize));

        // Again: the replacement is found the second time too (it carries the same source info as compiled XAML).
        Assert.True((await app.Session.ApplyXamlAsync(Asm + "HotReloadStyles.axaml", Edit("HotReloadStyles.axaml", "Value=\"20\"", "Value=\"41\""))).Ok);
        Assert.Equal(41, await app.OnUi(() => _view.FindControl<TextBlock>("Styled")!.FontSize));
    }

    [Fact]
    public async Task Resource_dictionary_updates_dynamic_and_static_consumers()
    {
        Assert.Equal(Colors.Red, await BrushOf("DynamicAccent"));
        var r = await app.Session.ApplyXamlAsync(Asm + "HotReloadColors.axaml", Edit("HotReloadColors.axaml", "Color=\"Red\"", "Color=\"Lime\""));
        Assert.True(r.Ok, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        Assert.Equal(Colors.Lime, await BrushOf("DynamicAccent"));
        Assert.Equal(Colors.Lime, await BrushOf("StaticAccent"));   // HotReloadView re-applied: it reads Hr.Accent as StaticResource
    }

    [Fact]
    public async Task Merged_dictionary_change_reapplies_the_document_that_merges_it()
    {
        Assert.Equal(Colors.Green, await BrushOf("MergedAccent"));
        var r = await app.Session.ApplyXamlAsync(Asm + "HotReloadMerged.axaml", Edit("HotReloadMerged.axaml", "Color=\"Green\"", "Color=\"Gold\""));
        Assert.True(r.Ok, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        Assert.Equal(Colors.Gold, await BrushOf("MergedAccent"));
    }

    [Fact]
    public async Task Reloading_a_view_keeps_the_newest_included_documents()
    {
        Assert.True((await app.Session.ApplyXamlAsync(Asm + "HotReloadColors.axaml", Edit("HotReloadColors.axaml", "Color=\"Red\"", "Color=\"Lime\""))).Ok);
        var view = Original["HotReloadView.axaml"].Replace("""<TextBox x:Name="Input" />""", """<TextBox x:Name="Input" /><TextBlock x:Name="Extra" Text="x" />""");
        Assert.True((await app.Session.ApplyXamlAsync(Asm + "HotReloadView.axaml", view)).Ok);
        Assert.NotNull(await app.OnUi(() => _view.FindControl<TextBlock>("Extra")));
        Assert.Equal(Colors.Lime, await BrushOf("StaticAccent"));   // not the compiled Red
    }

    [Fact]
    public async Task Classless_compile_error_changes_nothing()
    {
        var r = await app.Session.ApplyXamlAsync(Asm + "HotReloadStyles.axaml", Edit("HotReloadStyles.axaml", "Property=\"FontSize\"", "Property=\"FontSiz\""));
        Assert.False(r.Ok);
        Assert.Contains(r.Diagnostics, d => d.Severity == XamlDiagnosticSeverity.Error && d.Line == 3 && d.Message.Contains("FontSiz"));
        Assert.Equal(20, await app.OnUi(() => _view.FindControl<TextBlock>("Styled")!.FontSize));
    }

    [Fact]
    public async Task Reloaded_elements_keep_their_xaml_source_location()
    {
        Assert.True((await app.Session.ApplyXamlAsync(Asm + "HotReloadView.axaml",
            Original["HotReloadView.axaml"].Replace("""Text="styled" """, """Text="styled again" """))).Ok);
        var src = await app.OnUi(() => Avalonia.Markup.Xaml.Diagnostics.XamlSourceInfo.GetXamlSourceInfo(_view.FindControl<TextBlock>("Styled")!));
        Assert.NotNull(src);
        Assert.Equal(Source("HotReloadView.axaml"), src!.SourceUri!.LocalPath);
    }

    [Fact]
    public async Task App_axaml_reloads_the_application_and_keeps_the_theme()
    {
        var r = await app.Session.ApplyXamlAsync(Asm + "TestApp.axaml", Edit("TestApp.axaml",
            """<x:Double x:Key="Test.Gap">12</x:Double>""", """<x:Double x:Key="Test.Gap">12</x:Double><SolidColorBrush x:Key="Hr.AppBrush" Color="Teal" />"""));
        Assert.True(r.Ok, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        Assert.Equal(1, r.Instances);
        var (brush, fluent, styles) = await app.OnUi(() => (
            (Application.Current!.FindResource("Hr.AppBrush") as ISolidColorBrush)?.Color,
            Application.Current!.Styles.OfType<FluentTheme>().Count(),
            Application.Current!.Styles.Count));
        Assert.Equal(Colors.Teal, brush);
        Assert.Equal(1, fluent);                                       // theme re-created, not duplicated
        Assert.Equal(Original["TestApp.axaml"].Split("<Style ").Length, styles);   // FluentTheme + the app's own styles, once
        Assert.True(await app.OnUi(() => { var b = new Button(); _view.FindControl<StackPanel>("Root")!.Children.Add(b); b.ApplyTemplate(); return b.Template is not null; }));
    }
}
