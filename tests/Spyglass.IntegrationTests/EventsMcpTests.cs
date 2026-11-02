using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>MCP `events_trace` (design/mcp/01-tools.md #11).</summary>
[Collection("app")]
public class EventsMcpTests(AppFixture app) : IAsyncLifetime
{
    McpClient _client = null!;

    public async ValueTask InitializeAsync() =>
        _client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(app.Hub.BaseUri, "mcp"),
        }));

    public async ValueTask DisposeAsync()
    {
        await app.Session.StopEventsAsync();
        await _client.DisposeAsync();
    }

    async Task<(string Text, bool IsError)> Call(Dictionary<string, object?> args)
    {
        var r = await _client.CallToolAsync("events_trace", args);
        return (string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text)), r.IsError == true);
    }

    Task ClickSave() => app.OnUi(() =>
    {
        var b = app.Window.SaveButton;
        var p = b.TranslatePoint(new Point(b.Bounds.Width / 2, b.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseMove(p);
        app.Window.MouseDown(p, MouseButton.Left);
        app.Window.MouseUp(p, MouseButton.Left);
        return 0;
    });

    [Fact]
    public async Task Start_read_stop()
    {
        var (start, err) = await Call(new() { ["action"] = "start", ["events"] = new[] { "Button.Click", "InputElement.PointerPressed", "Bogus.Event" } });
        Assert.False(err, start);
        Assert.Contains("Recording Button.Click, InputElement.PointerPressed", start);
        Assert.Contains("Unknown events ignored: Bogus.Event", start);

        await ClickSave();
        await Task.Delay(300);

        var (read, _) = await Call(new() { ["action"] = "read" });
        Assert.Matches(@"PointerPressed\s+source \[e\d+\] ContentPresenter#PART_ContentPresenter  bubble  handled by \[e\d+\] Button#SaveButton", read);
        Assert.Matches(@"Click\s+source \[e\d+\] Button#SaveButton  bubble  not handled  route: e\d+ → e\d+", read);
        Assert.Matches(@"^2 events \(cursor: \d+\):", read);

        var (again, _) = await Call(new() { ["action"] = "read" });
        Assert.StartsWith("No events since cursor", again);

        var (stop, _) = await Call(new() { ["action"] = "stop" });
        Assert.Contains("Stopped", stop);
        await ClickSave();
        await Task.Delay(300);
        var (after, _) = await Call(new() { ["action"] = "read" });
        Assert.StartsWith("No events", after);
    }

    [Fact]
    public async Task Start_with_element_filter_and_bad_action()
    {
        var (start, _) = await Call(new() { ["action"] = "start", ["events"] = new[] { "Button.Click" }, ["element"] = "#CancelButton" });
        Assert.Contains("only events whose route passes", start);
        await ClickSave();
        await Task.Delay(300);
        var (read, _) = await Call(new() { ["action"] = "read" });
        Assert.StartsWith("No events", read);

        var (bad, isError) = await Call(new() { ["action"] = "pause" });
        Assert.True(isError);
        Assert.Contains("Unknown action", bad);
    }
}
