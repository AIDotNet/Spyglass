using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>Routed-event tracing (design/protocol/01-methods §8, W3).</summary>
[Collection("app")]
public class EventsTests(AppFixture app) : IAsyncLifetime
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    readonly ConcurrentQueue<EventRecord> _pushed = new();

    void OnRaised(EventsRaisedParams p) { foreach (var r in p.Records) _pushed.Enqueue(r); }

    public ValueTask InitializeAsync()
    {
        app.Session.EventsRaised += OnRaised;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        app.Session.EventsRaised -= OnRaised;
        await app.Session.StopEventsAsync();
    }

    async Task<int> Find(string query) =>
        Assert.Single((await app.Session.SearchAsync(new SearchParams(query))).Matches).Id;

    Task ClickSave() => app.OnUi(() =>
    {
        var b = app.Window.SaveButton;
        var p = b.TranslatePoint(new Point(b.Bounds.Width / 2, b.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseMove(p);
        app.Window.MouseDown(p, MouseButton.Left);
        app.Window.MouseUp(p, MouseButton.Left);
        return 0;
    });

    async Task<List<EventRecord>> WaitFor(Func<List<EventRecord>, bool> done)
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            var list = _pushed.ToList();
            if (done(list)) return list;
            await Task.Delay(50);
        }
        return _pushed.ToList();
    }

    [Fact]
    public async Task List_routed_contains_button_click()
    {
        Assert.True(app.Session.Has(Capabilities.Events));
        var events = await app.Session.ListRoutedEventsAsync();
        var click = Assert.Single(events, e => e is { Owner: "Button", Name: "Click" });
        Assert.Equal([RouteStrategy.Bubble], click.Routes);
        Assert.Contains(events, e => e is { Owner: "InputElement", Name: "KeyDown" } && e.Routes.Contains(RouteStrategy.Tunnel));
    }

    [Fact]
    public async Task Configure_reports_defaults_and_unknown_names()
    {
        var r = await app.Session.ConfigureEventsAsync(null);
        Assert.Contains("Button.Click", r.Enabled);
        Assert.Contains("InputElement.PointerPressed", r.Enabled);
        Assert.DoesNotContain("InputElement.PointerMoved", r.Enabled);
        Assert.Empty(r.Unknown);

        r = await app.Session.ConfigureEventsAsync(["Button.Click", "Nope.Missing"]);
        Assert.Equal(["Button.Click"], r.Enabled);
        Assert.Equal(["Nope.Missing"], r.Unknown);
    }

    [Fact]
    public async Task Click_is_one_record_with_source_and_route()
    {
        var save = await Find("#SaveButton");
        var root = await Find("StackPanel#Root");
        await app.Session.ConfigureEventsAsync(["Button.Click"]);
        await ClickSave();

        var records = await WaitFor(l => l.Any(r => r.Event == "Button.Click"));
        var click = Assert.Single(records, r => r.Event == "Button.Click");
        Assert.Equal(save, click.SourceId);
        Assert.Equal("Button", click.SourceType);
        Assert.Equal("SaveButton", click.SourceName);
        Assert.Equal(RouteStrategy.Bubble, click.Strategy);
        Assert.Equal(save, click.Route[0].Id);
        Assert.Contains(click.Route, s => s.Id == root);
        Assert.Contains(click.Route, s => s.Type == "TestWindow");
        Assert.Equal(click.Route.Count, click.Route.Select(s => s.Id).Distinct().Count());   // once per node

        // Also available from the agent's buffer (MCP read path).
        var buffered = await app.Session.GetEventsAsync(click.Seq - 1, elementId: root);
        Assert.Contains(buffered, r => r.Seq == click.Seq);
    }

    [Fact]
    public async Task Pointer_pressed_is_handled_by_the_button()
    {
        var save = await Find("#SaveButton");
        await app.Session.ConfigureEventsAsync(["InputElement.PointerPressed", "InputElement.PointerReleased"]);
        await ClickSave();

        var records = await WaitFor(l => l.Any(r => r.Event == "InputElement.PointerReleased"));
        var pressed = Assert.Single(records, r => r.Event == "InputElement.PointerPressed");
        Assert.True(pressed.Handled);
        Assert.Equal(save, pressed.HandledById);
        var step = Assert.Single(pressed.Route, s => s.Handled);
        Assert.Equal(save, step.Id);
        Assert.Equal(RouteStrategy.Bubble, step.Phase);
        Assert.Equal(RouteStrategy.Bubble, pressed.Strategy);
        // Tunnel + bubble: the tunnel phase starts at the root and reaches the source before bubbling back.
        Assert.Equal(RouteStrategy.Tunnel, pressed.Route[0].Phase);
        Assert.Contains(pressed.Route, s => s.Id == pressed.SourceId && s.Phase == RouteStrategy.Bubble);
        Assert.DoesNotContain(records, r => r.Event == "InputElement.PointerMoved");
    }

    [Fact]
    public async Task Pause_stops_recording_and_resume_restarts_it()
    {
        var cursor = (await app.Session.ConfigureEventsAsync(["Button.Click"])).Cursor;
        await app.Session.PauseEventsAsync();
        await ClickSave();
        await Task.Delay(400);
        Assert.Empty(await app.Session.GetEventsAsync(cursor));
        Assert.DoesNotContain(_pushed, r => r.Seq > cursor);

        await app.Session.ResumeEventsAsync();
        await ClickSave();
        var records = await WaitFor(l => l.Any(r => r.Seq > cursor));
        Assert.Single(records, r => r.Seq > cursor && r.Event == "Button.Click");
    }

    [Fact]
    public async Task Stop_unsubscribes()
    {
        var cursor = (await app.Session.ConfigureEventsAsync(["Button.Click"])).Cursor;
        var stopped = await app.Session.ConfigureEventsAsync([]);
        Assert.Empty(stopped.Enabled);
        await ClickSave();
        await Task.Delay(300);
        Assert.Empty(await app.Session.GetEventsAsync(cursor));
    }
}
