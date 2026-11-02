using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>Drives the MCP endpoint with the official MCP client, like a coding agent would.</summary>
[Collection("app")]
public class McpTests(AppFixture app) : IAsyncLifetime
{
    McpClient _client = null!;

    public async ValueTask InitializeAsync() =>
        _client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(app.Hub.BaseUri, "mcp"),
        }));

    public async ValueTask DisposeAsync() => await _client.DisposeAsync();

    async Task<(string Text, CallToolResult Result)> Call(string tool, Dictionary<string, object?>? args = null)
    {
        var r = await _client.CallToolAsync(tool, args ?? new());
        return (string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text)), r);
    }

    [Fact]
    public async Task Exposes_the_mvp_tools_as_read_only()
    {
        var tools = await _client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet();
        foreach (var expected in new[] { "apps_list", "binding_errors", "element_inspect", "element_properties", "property_explain",
                                         "screenshot", "ui_find", "ui_snapshot", "network_capture", "network_requests", "network_request", "logs_read", "events_trace" })
            Assert.Contains(expected, names);
        // Everything is read-only except tools that change state (start/stop capture).
        Assert.All(tools.Where(t => t.Name is not ("network_capture" or "property_set" or "state_force" or "ui_interact" or "changes_reset")), t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint, t.Name));
        Assert.Contains("ui_snapshot", _client.ServerInstructions);
    }

    [Fact]
    public async Task Apps_list_names_the_app_and_its_windows()
    {
        var (text, _) = await Call("apps_list");
        Assert.Contains("Spyglass Integration Tests", text);
        Assert.Contains("Avalonia 12.1.4", text);
        Assert.Contains("\"Main\"", text);
        Assert.Matches(@"binding errors: [1-9]", text);
    }

    [Fact]
    public async Task Snapshot_is_a_readable_outline_with_refs_and_problems()
    {
        var (text, _) = await Call("ui_snapshot");
        Assert.Contains("logical tree", text);
        Assert.Matches(@"Button#SaveButton\.primary ""Save"" \[e\d+\] 120×32", text);
        Assert.Contains("placeholder=\"Your name\"", text);
        Assert.Matches(@"Button#CancelButton ""Cancel"" \[e\d+\].* disabled", text);
        Assert.Matches(@"TextBlock#Broken \[e\d+\].*⚠ binding error \(Text\)", text);
        Assert.DoesNotContain("(template)", text);
        Assert.True(text.Split('\n').Length < 40, text);   // compact

        var (visual, _) = await Call("ui_snapshot", new() { ["tree"] = "visual", ["includeTemplate"] = true });
        Assert.Contains("(template)", visual);
    }

    [Fact]
    public async Task Find_by_text_selector_and_xaml()
    {
        Assert.Matches(@"^1 match for ""text=Save"":\nButton#SaveButton", (await Call("ui_find", new() { ["query"] = "text=Save" })).Text);
        Assert.Contains("TextBox#NameBox", (await Call("ui_find", new() { ["query"] = "StackPanel > TextBox" })).Text);
        Assert.Contains("#SaveButton", (await Call("ui_find", new() { ["query"] = "xaml=TestWindow.axaml:13" })).Text);
        Assert.Contains("No elements match", (await Call("ui_find", new() { ["query"] = "#Nope" })).Text);
    }

    [Fact]
    public async Task Inspect_explains_the_broken_binding_with_its_xaml_line()
    {
        var (text, r) = await Call("element_inspect", new() { ["element"] = "#Broken" });
        Assert.NotEqual(true, r.IsError);
        Assert.Contains("TestWindow.axaml:10", text);
        Assert.Contains("path \"User.FullNmae\"", text);
        Assert.Contains("Could not find a matching property accessor for 'FullNmae'", text);
        Assert.Contains("datacontext Spyglass.IntegrationTests.TestViewModel", text);
    }

    [Fact]
    public async Task Inspect_lists_non_default_properties_with_their_source()
    {
        var (text, _) = await Call("element_inspect", new() { ["element"] = "text=Save" });
        Assert.Contains("TestWindow.axaml:13", text);
        Assert.Matches(@"Width\s+120\s+LocalValue", text);
        // Colour depends on whether the pointer is over the button (another test moves it there): check the source only.
        Assert.Matches(@"Background\s+#[0-9a-fA-F]{8}\s+StyleTrigger", text);
        Assert.Contains("margin 4,2,4,2", text);
        Assert.Contains("binding errors: none", text);
    }

    [Fact]
    public async Task Mistyped_element_suggests_the_right_one()
    {
        var (text, r) = await Call("element_inspect", new() { ["element"] = "#SaveButon" });
        Assert.True(r.IsError);
        Assert.Contains("No element matches '#SaveButon'", text);
        Assert.Matches(@"Similar: .*Button#SaveButton\.primary \[e\d+\]", text);
    }

    [Fact]
    public async Task Ambiguous_element_lists_candidates()
    {
        var (text, r) = await Call("element_inspect", new() { ["element"] = "Button" });
        Assert.True(r.IsError);
        Assert.Matches(@"matches \d+ elements", text);
        Assert.Contains("#SaveButton", text);
        Assert.Contains("#CancelButton", text);
    }

    [Fact]
    public async Task Stale_reference_tells_the_model_what_to_do()
    {
        var (text, r) = await Call("element_inspect", new() { ["element"] = "e999999" });
        Assert.True(r.IsError);
        Assert.Contains("no longer exists", text);
        Assert.Contains("ui_snapshot", text);
    }

    [Fact]
    public async Task Properties_filter_by_name()
    {
        var (text, _) = await Call("element_properties", new() { ["element"] = "#SaveButton", ["names"] = new[] { "Width", "*Height" } });
        Assert.Contains("Layoutable.Width", text);
        Assert.Contains("Layoutable.Height", text);
        Assert.Contains("Layoutable.MinHeight", text);
        Assert.DoesNotContain("Background", text);
    }

    [Fact]
    public async Task Screenshot_returns_a_png_image()
    {
        var (text, r) = await Call("screenshot", new() { ["highlight"] = "#SaveButton", ["maxSize"] = 300 });
        var img = Assert.Single(r.Content.OfType<ImageContentBlock>());
        Assert.Equal("image/png", img.MimeType);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], img.DecodedData.ToArray()[..4]);
        Assert.Contains("outlined", text);
        Assert.Contains("300×200 px", text);   // 600×400 window scaled to maxSize
    }
}

[Collection("app")]
public class McpV02Tests(AppFixture app) : IAsyncLifetime
{
    McpClient _client = null!;
    public async ValueTask InitializeAsync() =>
        _client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(app.Hub.BaseUri, "mcp") }));
    public async ValueTask DisposeAsync() => await _client.DisposeAsync();

    async Task<string> Call(string tool, Dictionary<string, object?> args) =>
        string.Join("\n", (await _client.CallToolAsync(tool, args)).Content.OfType<TextContentBlock>().Select(t => t.Text));

    [Fact]
    public async Task Property_explain_names_the_winning_style_and_what_it_overrides()
    {
        var text = await Call("property_explain", new() { ["element"] = "#Card", ["property"] = "Background" });
        Assert.Contains("winner: Style \"Border.card.highlight\"", text);
        Assert.Contains("TestApp.axaml:16", text);
        Assert.Contains("overridden:", text);
        Assert.Contains("Style \"Border.card\" — #ffff0000", text);
        Assert.Contains("inactive (condition not met):", text);
        Assert.Contains("Border.card:pointerover", text);
    }

    [Fact]
    public async Task Property_explain_shows_the_binding()
    {
        var text = await Call("property_explain", new() { ["element"] = "#Broken", ["property"] = "Text" });
        Assert.Contains("binding: {Binding User.FullNmae} — Error", text);
    }

    [Fact]
    public async Task Binding_errors_tool_with_cursor_and_hint()
    {
        var text = await Call("binding_errors", new());
        Assert.Matches(@"\(cursor: \d+\)", text);
        Assert.Contains("path \"User.FullNmae\"", text);
        Assert.Contains("TestWindow.axaml:10", text);
        var cursor = System.Text.RegularExpressions.Regex.Match(text, @"cursor: (\d+)").Groups[1].Value;
        Assert.Contains("No new binding errors", await Call("binding_errors", new() { ["since"] = long.Parse(cursor) }));
    }
}

[Collection("app")]
public class McpLayoutTests(AppFixture app) : IAsyncLifetime
{
    McpClient _client = null!;
    public async ValueTask InitializeAsync() =>
        _client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(app.Hub.BaseUri, "mcp") }));
    public async ValueTask DisposeAsync() => await _client.DisposeAsync();

    async Task<string> Call(string element) =>
        string.Join("\n", (await _client.CallToolAsync("layout_explain", new Dictionary<string, object?> { ["element"] = element })).Content.OfType<TextContentBlock>().Select(t => t.Text));

    [Fact]
    public async Task Layout_explain_lists_every_reason_an_element_is_invisible()
    {
        var text = await Call("#InHidden");
        Assert.Contains("NOT visible — 1 reason", text);
        Assert.Contains("✗ Ancestor StackPanel#HiddenPanel has IsVisible = false, which hides all of its descendants.", text);
    }

    [Fact]
    public async Task Layout_explain_describes_the_size_chain()
    {
        var text = await Call("#SaveButton");
        Assert.Contains("visibility  visible", text);
        Assert.Matches(@"parent\s+StackPanel \[e\d+\] offered .* → desired .* → slot .* → final 120×32", text);
        Assert.Contains("Width is fixed at 120", text);
    }
}
