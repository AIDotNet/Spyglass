using Avalonia.Media;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>resources/*: provider tree, entries, lookup chain, theme variants, live edit + reset; MCP resources_find.</summary>
[Collection("app")]
public class ResourcesTests(AppFixture app) : IAsyncLifetime
{
    McpClient _client = null!;

    public async ValueTask InitializeAsync() =>
        _client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(app.Hub.BaseUri, "mcp") }));

    public async ValueTask DisposeAsync()
    {
        await app.Session.ResetResourcesAsync(new ResourcesResetParams(All: true));
        await _client.DisposeAsync();
    }

    async Task<int> Find(string query) => Assert.Single((await app.Session.SearchAsync(new SearchParams(query))).Matches).Id;

    static IEnumerable<ResourceProviderDto> Flatten(ResourceProviderDto p) => new[] { p }.Concat((p.Children ?? []).SelectMany(Flatten));

    async Task<ResourceProviderDto> AppProvider() => (await app.Session.GetResourceProvidersAsync()).Providers[0];

    async Task<int> ThemeDictionary(string variant) =>
        Flatten(await AppProvider()).First(p => p.Kind == "themeDictionary" && p.Variant == variant && p.Source?.Uri.EndsWith("TestApp.axaml") == true).Id;

    [Fact]
    public void Capability_is_declared() => Assert.True(app.Session.Has(Capabilities.Resources));

    [Fact]
    public async Task Provider_tree_has_application_dictionaries_styles_and_element_chain()
    {
        var swatch = await Find("#Swatch");
        var r = await app.Session.GetResourceProvidersAsync(swatch);
        Assert.Contains(r.Variants, v => v == "Dark");

        var root = r.Providers[0];
        Assert.Equal("application", root.Kind);
        Assert.Equal(4, root.KeyCount);   // Test.Gap, Test.Pad, Test.Title, Test.Brush

        var all = Flatten(root).ToList();
        Assert.Equal(2, Assert.Single(all, p => p.Kind == "themeDictionary" && p.Variant == "Light" && p.Source?.Uri.EndsWith("TestApp.axaml") == true).KeyCount);
        Assert.Single(all, p => p.Kind == "themeDictionary" && p.Variant == "Dark" && p.Source?.Uri.EndsWith("TestApp.axaml") == true);
        var merged = Assert.Single(all, p => p.Kind == "dictionary" && p.Source?.Uri.EndsWith("TestApp.axaml") == true);
        Assert.Equal(2, merged.KeyCount);
        Assert.Equal(47, merged.Source!.Line);
        Assert.Contains(all, p => p.Kind == "styles" && p.Name == "FluentTheme");
        var style = Assert.Single(all, p => p.Kind == "style" && p.Name.Contains("Border.swatch"));
        Assert.Equal(1, style.KeyCount);   // style-scoped resources

        // Element providers, nearest first; elements without resources are skipped.
        var elements = r.Providers.Skip(1).ToList();
        Assert.All(elements, e => Assert.Equal("element", e.Kind));
        Assert.Equal("StackPanel#Root", elements[0].Name);
        Assert.Equal(1, elements[0].KeyCount);
        Assert.Equal("TestWindow", elements[1].Name);
        Assert.DoesNotContain(elements, e => e.Id == swatch);
    }

    [Fact]
    public async Task Entries_have_typed_values_source_lines_and_filter()
    {
        var entries = (await app.Session.GetResourceEntriesAsync((await AppProvider()).Id)).Entries;
        var gap = Assert.Single(entries, e => e.Key == "Test.Gap");
        Assert.Equal(ValueKind.Number, gap.Value.Kind);
        Assert.Equal("12", gap.Value.Text);
        Assert.True(gap.Editable);
        Assert.Equal(42, gap.Source!.Line);
        Assert.EndsWith("TestApp.axaml", gap.Source.Uri);
        Assert.Equal(ValueKind.Thickness, Assert.Single(entries, e => e.Key == "Test.Pad").Value.Kind);
        Assert.Equal(ValueKind.Brush, Assert.Single(entries, e => e.Key == "Test.Brush").Value.Kind);

        var filtered = (await app.Session.GetResourceEntriesAsync((await AppProvider()).Id, query: "title")).Entries;
        Assert.Equal("Test.Title", Assert.Single(filtered).Key);
        var byValue = (await app.Session.GetResourceEntriesAsync((await AppProvider()).Id, query: "ff8800")).Entries;
        Assert.Equal("Test.Brush", Assert.Single(byValue).Key);
    }

    [Fact]
    public async Task Dynamic_resource_values_show_resolved_and_theme_variant_entries_are_flagged()
    {
        var light = (await app.Session.GetResourceEntriesAsync(await ThemeDictionary("Light"))).Entries;
        var brush = Assert.Single(light, e => e.Key == "Test.AccentBrush");
        Assert.Equal(ValueKind.Brush, brush.Value.Kind);
        Assert.Equal("#ff0562ef", brush.Value.Text);   // Color="{DynamicResource Test.Accent}" resolved
        Assert.Equal(35, brush.Source!.Line);

        // themeVariant on the parent provider adds that variant's keys, flagged.
        var withDark = (await app.Session.GetResourceEntriesAsync((await AppProvider()).Id, "Dark", "Test.Accent")).Entries;
        var accent = Assert.Single(withDark, e => e.Key == "Test.Accent");
        Assert.Equal("Dark", accent.Variant);
        Assert.Equal("#ff4d8dff", accent.Value.Text);
        Assert.Equal(38, accent.Source!.Line);
    }

    [Fact]
    public async Task Lookup_chain_orders_providers_nearest_first_and_marks_hit_miss_and_shadowed()
    {
        var swatch = await Find("#Swatch");
        var r = await app.Session.LookupResourceAsync("Test.Gap", swatch, "Light");
        Assert.True(r.Found);
        Assert.Equal("4", r.Value!.Text);   // authoritative value = what TryFindResource returns
        Assert.Null(r.Note);

        var chain = r.Chain;
        Assert.Equal("StackPanel#Root", chain[0].Provider.Name);
        Assert.True(chain[0].Winner);
        Assert.Equal(33, chain[0].Source!.Line);
        Assert.EndsWith("TestWindow.axaml", chain[0].Source!.Uri);
        Assert.Equal("TestWindow", chain[1].Provider.Name);
        Assert.False(chain[1].Hit);
        // Application: own keys before merged dictionaries; both define it and are hidden by the winner.
        var shadowed = chain.Where(c => c.Shadowed).ToList();
        Assert.Equal(["Application", "Application › TestApp.axaml"], shadowed.Select(c => c.Path));
        Assert.Equal(["12", "99"], shadowed.Select(c => c.Value!.Text));
        Assert.Single(chain, c => c.Winner);
        Assert.True(chain.ToList().FindIndex(c => c.Winner) < chain.ToList().FindIndex(c => c.Shadowed));
    }

    [Fact]
    public async Task Lookup_from_application_scope_and_unknown_key()
    {
        var r = await app.Session.LookupResourceAsync("Test.Gap");
        Assert.Equal("12", r.Value!.Text);   // application own keys win over its merged dictionary
        Assert.Equal("Application", r.Chain.First(c => c.Winner).Path);

        var none = await app.Session.LookupResourceAsync("No.Such.Key", await Find("#Swatch"));
        Assert.False(none.Found);
        Assert.Null(none.Value);
        Assert.DoesNotContain(none.Chain, c => c.Hit);
        Assert.True(none.Chain.Count > 3);
    }

    [Fact]
    public async Task Theme_variants_resolve_to_different_dictionaries_and_fluent_keys_match_avalonia()
    {
        var swatch = await Find("#Swatch");
        var light = await app.Session.LookupResourceAsync("Test.AccentBrush", swatch, "Light");
        var dark = await app.Session.LookupResourceAsync("Test.AccentBrush", swatch, "Dark");
        Assert.Equal("#ff0562ef", light.Value!.Text);
        Assert.Equal("#ff4d8dff", dark.Value!.Text);
        Assert.Equal("Application › ThemeDictionaries[Light]", light.Chain.Single(c => c.Winner).Path);
        Assert.Equal("Application › ThemeDictionaries[Dark]", dark.Chain.Single(c => c.Winner).Path);
        Assert.DoesNotContain(light.Chain, c => c.Path.Contains("[Dark]"));

        // The emulated chain must agree with Avalonia for a Fluent key (Default fallback), otherwise Note is set.
        foreach (var v in new[] { "Light", "Dark" })
        {
            var f = await app.Session.LookupResourceAsync("ButtonBackground", swatch, v);
            Assert.True(f.Found);
            Assert.Null(f.Note);
            Assert.Contains("FluentTheme", f.Chain.Single(c => c.Winner).Path);
        }

        // SystemAccentColor is produced by FluentTheme itself (platform accent), not by a dictionary: still resolved, and
        // the result says the chain cannot show the provider.
        var accent = await app.Session.LookupResourceAsync("SystemAccentColor", swatch, "Light");
        Assert.True(accent.Found);
        Assert.Equal(ValueKind.Color, accent.Value!.Kind);
    }

    [Fact]
    public async Task Set_replaces_value_dynamic_resource_users_update_and_reset_restores()
    {
        var swatch = await Find("#Swatch");
        var rootId = await Find("#Root");
        Assert.Equal(4, await app.OnUi(() => app.Window.Swatch.Width));

        var e = await app.Session.SetResourceAsync(rootId, "Test.Gap", new ValueDto(ValueKind.Number, "20"));
        Assert.Equal("20", e.Value.Text);
        Assert.True(e.IsModifiedBySpyglass);
        Assert.Equal(20, await app.OnUi(() => app.Window.Swatch.Width));   // Width="{DynamicResource Test.Gap}" followed
        Assert.Equal("20", (await app.Session.LookupResourceAsync("Test.Gap", swatch)).Value!.Text);

        await app.Session.ResetResourcesAsync(new ResourcesResetParams(rootId, "Test.Gap"));
        Assert.Equal(4, await app.OnUi(() => app.Window.Swatch.Width));
        var again = (await app.Session.GetResourceEntriesAsync(rootId)).Entries.Single();
        Assert.False(again.IsModifiedBySpyglass);
        Assert.Equal("4", again.Value.Text);
    }

    [Fact]
    public async Task Set_colour_brush_updates_bound_element_and_reset_all_restores()
    {
        var windowProvider = (await app.Session.GetResourceProvidersAsync(await Find("#Swatch"))).Providers.Single(p => p.Name == "TestWindow").Id;
        Assert.Equal(Color.Parse("#00AA00"), await app.OnUi(() => ((ISolidColorBrush)app.Window.Swatch.Background!).Color));

        var e = await app.Session.SetResourceAsync(windowProvider, "Test.Brush", new ValueDto(ValueKind.Brush, "#ff112233"));
        Assert.Equal("#ff112233", e.Value.Text);
        Assert.Equal(Color.Parse("#112233"), await app.OnUi(() => ((ISolidColorBrush)app.Window.Swatch.Background!).Color));

        // A second edit keeps the first original.
        await app.Session.SetResourceAsync(windowProvider, "Test.Brush", new ValueDto(ValueKind.Brush, "#ff445566"));
        await app.Session.ResetResourcesAsync(new ResourcesResetParams(All: true));
        Assert.Equal(Color.Parse("#00AA00"), await app.OnUi(() => ((ISolidColorBrush)app.Window.Swatch.Background!).Color));
        Assert.False((await app.Session.GetResourceEntriesAsync(windowProvider)).Entries.Single().IsModifiedBySpyglass);
    }

    [Fact]
    public async Task Set_never_adds_keys_and_rejects_bad_values()
    {
        var appId = (await AppProvider()).Id;
        var missing = await Assert.ThrowsAsync<SpyglassProtocolException>(() => app.Session.SetResourceAsync(appId, "Test.NewKey", new ValueDto(ValueKind.String, "x")));
        Assert.Contains("never adds", missing.Message);
        Assert.DoesNotContain((await app.Session.GetResourceEntriesAsync(appId)).Entries, x => x.Key == "Test.NewKey");

        var bad = await Assert.ThrowsAsync<SpyglassProtocolException>(() => app.Session.SetResourceAsync(appId, "Test.Pad", new ValueDto(ValueKind.Thickness, "wide")));
        Assert.Equal(ErrorCodes.ConversionFailed, bad.Code);
        Assert.Equal("8,4,8,4", (await app.Session.GetResourceEntriesAsync(appId, query: "Test.Pad")).Entries.Single().Value.Text);
    }

    [Fact]
    public async Task Find_returns_definitions_per_variant_with_overrides()
    {
        var swatch = await Find("#Swatch");
        var exact = await app.Session.FindResourcesAsync("Test.Gap", swatch);
        Assert.True(exact.Exact);
        var key = Assert.Single(exact.Keys);
        Assert.Equal(["Light", "Dark"], key.Lookups.Select(l => l.Variant));
        Assert.All(key.Lookups, l => Assert.Equal("4", l.Value!.Text));

        var partial = await app.Session.FindResourcesAsync("test.acc");   // case-insensitive substring
        Assert.False(partial.Exact);
        Assert.Equal(["Test.Accent", "Test.AccentBrush"], partial.Keys.Select(k => k.Key));
        Assert.Equal("#ff0562ef", partial.Keys[0].Lookups.Single(l => l.Variant == "Light").Value!.Text);
        Assert.Equal("#ff4d8dff", partial.Keys[0].Lookups.Single(l => l.Variant == "Dark").Value!.Text);
    }

    [Fact]
    public async Task Mcp_resources_find_reports_winner_overrides_and_variants()
    {
        var swatch = await Find("#Swatch");
        var r = await _client.CallToolAsync("resources_find", new Dictionary<string, object?> { ["query"] = "Test.Gap", ["element"] = $"e{swatch}" });
        var text = string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.NotEqual(true, r.IsError);
        Assert.Contains("Resource \"Test.Gap\"", text);
        Assert.Matches(@"Light\*?, Dark\*? → 4 \(Double\)   ← StackPanel#Root \[e\d+\]  TestWindow\.axaml:33", text);
        Assert.Contains("overridden: Application  TestApp.axaml:42 = 12 (Double)", text);
        Assert.Contains("overridden: Application › TestApp.axaml  TestApp.axaml:49 = 99 (Double)", text);

        var variants = (await _client.CallToolAsync("resources_find", new Dictionary<string, object?> { ["query"] = "Test.Accent", ["themeVariant"] = "Dark" })).Content;
        var vt = string.Join("\n", variants.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.Contains("#ff4d8dff (Color)   ← Application › ThemeDictionaries[Dark]  TestApp.axaml:38", vt);
        Assert.DoesNotContain("#ff0562ef", vt);

        var none = await _client.CallToolAsync("resources_find", new Dictionary<string, object?> { ["query"] = "Zzz.Missing" });
        Assert.Contains("No resource key matches", string.Join("\n", none.Content.OfType<TextContentBlock>().Select(t => t.Text)));

        var tools = (await _client.ListToolsAsync()).Select(t => t.Name);
        Assert.Contains("resources_find", tools);
    }
}
