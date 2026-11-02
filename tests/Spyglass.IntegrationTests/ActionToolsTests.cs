using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>The act tools (property_set, state_force, ui_interact, wait_for, changes_reset) through the real MCP client.</summary>
[Collection("app")]
public class ActionToolsTests(AppFixture app) : IAsyncLifetime
{
    McpClient _client = null!;
    ActWindow _w = null!;

    public async ValueTask InitializeAsync()
    {
        _client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(app.Hub.BaseUri, "mcp") }));
        _w = await app.OnUi(() => { var w = new ActWindow(); w.Show(); return w; });
        // The headless render timer is manual: tick it so the compositor knows the window (hit testing needs a frame).
        await app.OnUi(() => { Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); return 0; });
        await Task.Delay(100);
    }

    public async ValueTask DisposeAsync()
    {
        await app.OnUi(() => { _w.Close(); return 0; });
        await _client.DisposeAsync();
    }

    async Task<(string Text, bool IsError)> Call(string tool, params (string, object?)[] args)
    {
        var r = await _client.CallToolAsync(tool, args.ToDictionary(a => a.Item1, a => a.Item2));
        return (string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text)), r.IsError == true);
    }

    async Task<string> Ok(string tool, params (string, object?)[] args)
    {
        var (text, err) = await Call(tool, args);
        Assert.False(err, text);
        return text;
    }

    Task<string> Act(string element, string action, params (string, object?)[] more) =>
        Ok("ui_interact", [("element", element), ("action", action), .. more]);

    [Fact]
    public async Task Tools_are_listed_with_honest_annotations()
    {
        var tools = (await _client.ListToolsAsync()).ToDictionary(t => t.Name);
        foreach (var n in new[] { "property_set", "state_force", "ui_interact", "wait_for", "changes_reset" }) Assert.Contains(n, tools.Keys);
        Assert.True(tools["wait_for"].ProtocolTool.Annotations?.ReadOnlyHint);
        foreach (var n in new[] { "property_set", "state_force", "ui_interact", "changes_reset" })
        {
            Assert.NotEqual(true, tools[n].ProtocolTool.Annotations?.ReadOnlyHint);
            Assert.False(tools[n].ProtocolTool.Annotations?.DestructiveHint);
        }
        Assert.Contains("input", app.Session.Capabilities);
    }

    [Fact]
    public async Task Invoke_clicks_a_button_through_its_automation_peer()
    {
        var before = app.Window.Clicks;
        var text = await Act("#SaveButton", "invoke");
        Assert.Equal(before + 1, app.Window.Clicks);
        Assert.Contains("peer:IInvokeProvider", text);
        Assert.Contains("0 binding errors, 0 log warnings", text);
        Assert.Matches(@"invoked Button#SaveButton\.primary \[e\d+\]", text);
    }

    [Fact]
    public async Task Invoke_on_a_disabled_button_is_refused_with_the_reason()
    {
        var (text, err) = await Call("ui_interact", ("element", "#CancelButton"), ("action", "invoke"));
        Assert.True(err);
        Assert.Contains("is disabled", text);
        Assert.Contains("IsEnabled=False", text);
        var (hidden, herr) = await Call("ui_interact", ("element", "#InHidden"), ("action", "invoke"));
        Assert.True(herr);
        Assert.Contains("not visible", hidden);
        Assert.Contains("HiddenPanel", hidden);
    }

    [Fact]
    public async Task SetText_and_typeText_edit_a_textbox()
    {
        var text = await Act("#ActText", "setText", ("text", "hello"));
        Assert.Equal("hello", await app.OnUi(() => _w.Input.Text));
        Assert.Contains("peer:IValueProvider", text);
        Assert.Contains("text \"hello\"", text);

        await Act("#ActText", "typeText", ("text", " world"));
        Assert.Equal("hello world", await app.OnUi(() => _w.Input.Text));
        await Act("#ActText", "typeText", ("text", "fresh"), ("replace", true));
        Assert.Equal("fresh", await app.OnUi(() => _w.Input.Text));

        var (err, isErr) = await Call("ui_interact", ("element", "#ActButton"), ("action", "setText"), ("text", "x"));
        Assert.True(isErr);
        Assert.Contains("does not accept text", err);
    }

    [Fact]
    public async Task Toggle_flips_or_sets_an_explicit_state()
    {
        var t1 = await Act("#ActCheck", "toggle");
        Assert.True(await app.OnUi(() => _w.Check.IsChecked == true));
        Assert.Contains("checked", t1);
        await Act("#ActCheck", "toggle", ("state", true));   // already on: stays on
        Assert.True(await app.OnUi(() => _w.Check.IsChecked == true));
        await Act("#ActCheck", "toggle", ("state", false));
        Assert.True(await app.OnUi(() => _w.Check.IsChecked == false));
        await Act("#ActToggle", "invoke");   // invoke on a toggle flips it
        Assert.True(await app.OnUi(() => _w.Toggle.IsChecked == true));
    }

    [Fact]
    public async Task Select_picks_combo_and_list_items_by_text_index_or_item()
    {
        var t = await Act("#ActCombo", "select", ("text", "gamma"));
        Assert.Equal(2, await app.OnUi(() => _w.Combo.SelectedIndex));
        Assert.Contains("selected \"Gamma\"", t);
        await Act("#ActList", "select", ("index", 1));
        Assert.Equal(1, await app.OnUi(() => _w.List.SelectedIndex));
        await Act("#ActList", "select", ("text", "Three"));
        Assert.Equal(2, await app.OnUi(() => _w.List.SelectedIndex));
        // an item itself
        await Act("text=Two", "select");
        Assert.Equal(1, await app.OnUi(() => _w.List.SelectedIndex));

        var (err, isErr) = await Call("ui_interact", ("element", "#ActList"), ("action", "select"), ("text", "Nine"));
        Assert.True(isErr);
        Assert.Contains("Items: [0] \"One\"", err);
    }

    [Fact]
    public async Task Expand_and_collapse()
    {
        await Act("#ActExpander", "expand");
        Assert.True(await app.OnUi(() => _w.Expand.IsExpanded));
        await Act("#ActExpander", "collapse");
        Assert.False(await app.OnUi(() => _w.Expand.IsExpanded));
        var t = await Act("#ActCombo", "expand");
        Assert.True(await app.OnUi(() => _w.Combo.IsDropDownOpen));
        Assert.Contains("dropdown open", t);
        await Act("#ActCombo", "collapse");
        Assert.False(await app.OnUi(() => _w.Combo.IsDropDownOpen));
    }

    [Fact]
    public async Task Keys_reach_buttons_and_hot_keys_resolve()
    {
        var before = await app.OnUi(() => _w.ActClicks);
        var focus = await Act("#ActButton", "focus");
        Assert.True(await app.OnUi(() => _w.Click.IsFocused));
        Assert.Contains("focused", focus);
        await Act("#ActButton", "key", ("key", "Enter"));
        Assert.Equal(before + 1, await app.OnUi(() => _w.ActClicks));

        var keysBefore = await app.OnUi(() => _w.KeysSeen);
        var t = await Act("#ActText", "key", ("key", "Ctrl+S"));
        Assert.Equal(1, await app.OnUi(() => _w.SaveShortcutCount));
        Assert.True(await app.OnUi(() => _w.KeysSeen) > keysBefore);
        Assert.Contains("Ctrl+S", t);

        var (err, isErr) = await Call("ui_interact", ("element", "#ActText"), ("action", "key"), ("key", "Banana"));
        Assert.True(isErr);
        Assert.Contains("Examples: Enter", err);
    }

    [Fact]
    public async Task Pointer_click_and_scroll_into_view_are_the_fallbacks()
    {
        var before = await app.OnUi(() => _w.ActClicks);
        var t = await Act("#ActButton", "pointer");
        Assert.True(before + 1 == await app.OnUi(() => _w.ActClicks), t);
        Assert.Contains("synth:pointer", t);

        var s = await Act("#BelowFold", "scrollIntoView");
        Assert.Contains("BringIntoView", s);
        var offset = await app.OnUi(() => app.Window.FindControl<ScrollViewer>("Scroller")!.Offset.Y);
        Assert.True(offset > 0, $"offset {offset}");
        await app.OnUi(() => { app.Window.FindControl<ScrollViewer>("Scroller")!.Offset = default; return 0; });
    }

    [Fact]
    public async Task Binding_errors_raised_by_an_action_are_counted_and_listed()
    {
        var t = await Act("#ActBreak", "invoke");
        Assert.Matches(@"during the action: [1-9]\d* binding errors?", t);
        Assert.Contains("⚠ binding error on TextBlock.Text", t);
        Assert.Contains("Missing", t);
    }

    [Fact]
    public async Task Property_set_and_state_force_round_trip_through_changes_reset()
    {
        var set = await Ok("property_set", ("element", "#ActButton"), ("property", "Width"), ("value", "222"));
        Assert.Contains("Width: NaN → 222", set.Replace("Auto", "NaN"));
        Assert.Contains("changes_reset", set);
        Assert.Equal(222, await app.OnUi(() => _w.Click.Width));

        var set2 = await Ok("property_set", ("element", "#SaveButton"), ("property", "Width"), ("value", "150"));
        Assert.Contains("120 → 150", set2);
        Assert.Contains("TestWindow.axaml:13", set2);   // permanent fix location

        var force = await Ok("state_force", ("element", "#ActButton"), ("pseudoClass", ":pointerover"));
        Assert.Contains(":pointerover", force);
        var cls = await Ok("state_force", ("element", "#ActButton"), ("class", "danger"));
        Assert.Contains(".danger", cls);
        Assert.Equal([":pointerover", "danger"], (await app.OnUi(() => _w.Click.Classes.ToList())).OrderBy(x => x));

        var reset = await Ok("changes_reset", ("element", "#ActButton"));
        Assert.Contains("Width", reset);
        Assert.Contains(":pointerover", reset);
        Assert.Contains("danger", reset);
        Assert.True(await app.OnUi(() => double.IsNaN(_w.Click.Width) && _w.Click.Classes.Count == 0));
        Assert.Equal(150, await app.OnUi(() => app.Window.SaveButton.Width));   // other elements untouched

        var all = await Ok("changes_reset");
        Assert.Contains("Width", all);
        Assert.Equal(120, await app.OnUi(() => app.Window.SaveButton.Width));
        Assert.Contains("Nothing to reset", await Ok("changes_reset"));
    }

    [Fact]
    public async Task Property_set_explains_bad_input()
    {
        var (bad, e1) = await Call("property_set", ("element", "#ActButton"), ("property", "Width"), ("value", "wide"));
        Assert.True(e1);
        Assert.Contains("currently", bad);
        var (nope, e2) = await Call("property_set", ("element", "#ActButton"), ("property", "Widht"), ("value", "1"));
        Assert.True(e2);
        Assert.Contains("no property 'Widht'", nope);
        var (ro, e3) = await Call("property_set", ("element", "#ActButton"), ("property", "Bounds"), ("value", "0,0,1,1"));
        Assert.True(e3);
        Assert.Contains("read-only", ro);
    }

    [Fact]
    public async Task Wait_for_succeeds_when_the_condition_becomes_true()
    {
        await Act("#ActDelay", "invoke");
        var t = await Ok("wait_for", ("condition", "visible"), ("element", "#ActLater"), ("timeoutMs", 3000));
        Assert.Matches(@"✓ #ActLater visible after \d+ ms", t);
        Assert.Contains("TextBlock#ActLater", t);
        var p = await Ok("wait_for", ("condition", "propertyEquals"), ("element", "#ActStatus"), ("property", "Text"), ("value", "idle"));
        Assert.Contains("✓", p);
        Assert.Contains("✓ UI idle", await Ok("wait_for", ("condition", "idle")));
        Assert.Contains("✓", await Ok("wait_for", ("condition", "enabled"), ("element", "#ActButton")));
    }

    [Fact]
    public async Task Wait_for_timeout_reports_the_current_state()
    {
        var t = await Ok("wait_for", ("condition", "propertyEquals"), ("element", "#ActStatus"), ("property", "Text"), ("value", "Saved"), ("timeoutMs", 300));
        Assert.Contains("✗ Timed out", t);
        Assert.Contains("TextBlock#ActStatus", t);
        Assert.Contains("Text is currently \"idle\"", t);

        var missing = await Ok("wait_for", ("condition", "exists"), ("element", "#Z"), ("timeoutMs", 200));   // 1-char token: no similar-name search, which would fetch whole trees and disturb TreeSyncTests
        Assert.Contains("✗ Timed out", missing);
        Assert.Contains("no element matches", missing);

        var disabled = await Ok("wait_for", ("condition", "enabled"), ("element", "#CancelButton"), ("timeoutMs", 100));
        Assert.Contains("✗", disabled);
        Assert.Contains("disabled", disabled);
        Assert.Contains("✓", await Ok("wait_for", ("condition", "gone"), ("element", "#NoSuchThing"), ("timeoutMs", 100)));
    }

    [Fact]
    public async Task Scenario_S6_save_does_nothing_because_the_command_cannot_execute()
    {
        // 1. try to click Save: refused, and the reason names the command
        var (inv, err) = await Call("ui_interact", ("element", "#ActLocked"), ("action", "invoke"));
        Assert.True(err);
        Assert.Contains("CanExecute", inv);
        // 2. wait for the expected status text: times out and shows what is there instead
        var wait = await Ok("wait_for", ("condition", "propertyEquals"), ("element", "#ActStatus"), ("property", "Text"), ("value", "Saved"), ("timeoutMs", 200));
        Assert.Contains("✗ Timed out", wait);
        Assert.Contains("\"idle\"", wait);
        // 3. inspect the button: disabled
        var inspect = await Ok("element_inspect", ("element", "#ActLocked"));
        Assert.Contains("disabled", inspect);
    }

    [Fact]
    public async Task Read_only_and_no_input_switches_refuse_with_the_variable_name()
    {
        try
        {
            Environment.SetEnvironmentVariable("SPYGLASS_MCP_NOINPUT", "1");
            var (noInput, e1) = await Call("ui_interact", ("element", "#ActButton"), ("action", "invoke"));
            Assert.True(e1);
            Assert.Contains("SPYGLASS_MCP_NOINPUT", noInput);
            Assert.Contains("Opacity", await Ok("property_set", ("element", "#ActButton"), ("property", "Opacity"), ("value", "0.5")));
            Assert.Equal(0, await app.OnUi(() => _w.ActClicks));

            Environment.SetEnvironmentVariable("SPYGLASS_MCP_READONLY", "1");
            foreach (var (tool, args) in new (string, (string, object?)[])[]
                     {
                         ("property_set", [("element", "#ActButton"), ("property", "Opacity"), ("value", "1")]),
                         ("state_force", [("element", "#ActButton"), ("pseudoClass", ":pressed")]),
                         ("ui_interact", [("element", "#ActButton"), ("action", "invoke")]),
                         ("changes_reset", []),
                     })
            {
                var (text, err) = await Call(tool, args);
                Assert.True(err, tool);
                Assert.Contains("SPYGLASS_MCP_READONLY", text);
            }
            // read-only tools are unaffected
            Assert.Contains("✓", await Ok("wait_for", ("condition", "exists"), ("element", "#ActButton")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SPYGLASS_MCP_NOINPUT", null);
            Environment.SetEnvironmentVariable("SPYGLASS_MCP_READONLY", null);
        }
        await Ok("changes_reset");
        Assert.Equal(1.0, await app.OnUi(() => _w.Click.Opacity));
    }
}
