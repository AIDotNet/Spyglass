using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Spyglass.App.Core;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>
/// Performance feature: metrics/configure, metrics/sample, overlays, rule engine and MCP performance_sample.
/// Headless limits: the compositor does render frames in the test app, but FPS is only asserted to be reported, not to
/// have a particular value; the overlays are state only (nothing is drawn headless that we could read back).
/// </summary>
[Collection("app")]
public class PerfTests(AppFixture app)
{
    AgentSession S => app.Session;

    /// <summary>Forces measure/arrange/render passes for <paramref name="ms"/> milliseconds.</summary>
    async Task Churn(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        var i = 0;
        var scratch = await app.OnUi(() =>
        {
            var p = new StackPanel { Name = "PerfScratch" };
            app.Window.Root.Children.Add(p);
            return p;
        });
        try {
        while (DateTime.UtcNow < until)
        {
            await app.OnUi(() =>
            {
                // Layout passes (margin) and style attachment (a fresh button every round) in a scratch panel; leaves no trace.
                scratch.Margin = new Thickness(i++ % 4);
                scratch.Children.Clear();
                scratch.Children.Add(new Button { Content = "scratch" + i, Classes = { "scratch" } });
                return 0;
            });
            await Task.Delay(15);
        }
        }
        finally { await app.OnUi(() => { app.Window.Root.Children.Remove(scratch); return 0; }); }
    }

    async Task<List<MetricsSampleParams>> CollectAsync(int churnMs, bool activities = true)
    {
        var samples = new List<MetricsSampleParams>();
        void On(MetricsSampleParams p) { lock (samples) samples.Add(p); }
        S.MetricsSample += On;
        try
        {
            await using var lease = await S.AcquireMetricsAsync(activities);
            Assert.True(lease.Status.Enabled);
            await Churn(churnMs);
        }
        finally { S.MetricsSample -= On; }
        lock (samples) return samples.ToList();
    }

    [Fact]
    public void Agent_declares_perf_capabilities()
    {
        Assert.True(S.Has(Capabilities.Metrics));
        Assert.True(S.Has(Capabilities.Activities));
        Assert.True(S.Has(Capabilities.Overlays));
    }

    [Fact]
    public async Task Enabling_metrics_delivers_samples_with_histograms_and_hot_spots()
    {
        var samples = await CollectAsync(2600);
        Assert.NotEmpty(samples);
        var last = samples[^1];
        Assert.True(last.Seq >= 2);
        Assert.NotNull(last.Fps);
        Assert.True(last.VisualCount > 10);
        Assert.True(last.IntervalMs is > 500 and < 3000);

        var measure = last.Histograms["avalonia.ui.measure.time"];
        Assert.True(measure.Count > 0);
        Assert.True(measure.P50 <= measure.P90 && measure.P90 <= measure.P95 && measure.P95 <= measure.Max);
        Assert.Contains("avalonia.ui.arrange.time", last.Histograms.Keys);

        // Layout churn ran activities too: the style / layout hot spots are not empty.
        Assert.Contains(last.HotSpots!, h => h.Kind == "layout" && h.Count > 0);
        Assert.Contains(last.HotSpots!, h => h.Kind == "style" && h.Count > 0);
        Assert.All(last.HotSpots!, h => Assert.True(h.TotalMs >= 0 && h.MaxMs <= h.TotalMs + 1e-6 || h.Count > 1));
    }

    [Fact]
    public async Task Disabling_metrics_stops_samples()
    {
        var samples = await CollectAsync(1500);
        Assert.NotEmpty(samples);
        Assert.False(S.MetricsActive);
        Assert.Null(S.LastMetrics);

        var after = 0;
        void On(MetricsSampleParams _) => Interlocked.Increment(ref after);
        S.MetricsSample += On;
        await Churn(2500);   // plenty of work, but nobody is listening
        S.MetricsSample -= On;
        Assert.Equal(0, after);

        var st = await S.ConfigureMetricsAsync(false);
        Assert.False(st.Enabled);
    }

    [Fact]
    public async Task Leases_are_reference_counted()
    {
        await using var a = await S.AcquireMetricsAsync();
        var b = await S.AcquireMetricsAsync();
        await b.DisposeAsync();
        Assert.True(S.MetricsActive);
        var st = await S.ConfigureMetricsAsync(true);   // still on after one lease was released
        Assert.True(st.Enabled);
        await Churn(1400);
        Assert.NotNull(S.LastMetrics);
    }

    [Fact]
    public async Task Without_activities_there_are_no_hot_spots()
    {
        var samples = await CollectAsync(1500, activities: false);
        Assert.NotEmpty(samples);
        Assert.Empty(samples[^1].HotSpots!);
        Assert.NotEmpty(samples[^1].Histograms);
    }

    [Fact]
    public async Task Overlays_round_trip_and_are_restored()
    {
        var id = (await S.GetRootsAsync()).Roots.Single(r => r.Title == "Main").Node.Id;
        Assert.Empty((await S.GetOverlaysAsync(id)).Overlays);

        var set = await S.SetOverlaysAsync(id, [DebugOverlay.Fps, DebugOverlay.RenderTimeGraph]);
        Assert.Equal([DebugOverlay.Fps, DebugOverlay.RenderTimeGraph], set.Overlays);
        Assert.Equal(id, set.TopLevelId);
        Assert.Equal(set.Overlays, (await S.GetOverlaysAsync(id)).Overlays);
        var flags = await app.OnUi(() => app.Window.RendererDiagnostics.DebugOverlays);
        Assert.Equal(Avalonia.Rendering.RendererDebugOverlays.Fps | Avalonia.Rendering.RendererDebugOverlays.RenderTimeGraph, flags);

        // An element inside the window resolves to its TopLevel too.
        var inner = (await S.SearchAsync(new SearchParams("#SaveButton"))).Matches.Single().Id;
        Assert.Equal(id, (await S.SetOverlaysAsync(inner, [])).TopLevelId);
        Assert.Empty((await S.GetOverlaysAsync(id)).Overlays);
    }

    [Fact]
    public async Task Non_virtualized_ItemsControl_is_reported()
    {
        ScrollViewer scroller = null!;
        await app.OnUi(() =>
        {
            var list = new ItemsControl { Name = "PerfList", ItemsSource = Enumerable.Range(0, 250).Select(i => "row " + i).ToList() };
            scroller = new ScrollViewer { Name = "PerfScroller", Height = 100, Content = list };
            app.Window.Root.Children.Add(scroller);
            return 0;
        });
        try
        {
            var samples = await CollectAsync(1300);   // the rules run on the first tick
            var f = Assert.Single(samples[0].Findings!, x => x.Rule == "NonVirtualizedItems");
            Assert.Equal("warning", f.Severity);
            Assert.Equal(250, f.Count);
            Assert.Contains("ItemsControl#PerfList is not virtualized: 250 ContentPresenter realized", f.Message);
            Assert.NotNull(f.ElementId);
            Assert.Equal("PerfList", (await S.DescribeAsync(f.ElementId!.Value)).Node.Name);
        }
        finally
        {
            await app.OnUi(() => { app.Window.Root.Children.Remove(scroller); return 0; });
        }
        // Without it the finding is gone, and the stock window has no findings at all.
        var clean = await CollectAsync(1300);
        Assert.DoesNotContain(clean[0].Findings!, x => x.Rule == "NonVirtualizedItems");
    }

    // ---- MCP ----

    [Fact]
    public async Task Mcp_performance_sample_end_to_end()
    {
        await app.OnUi(() =>
        {
            var list = new ItemsControl { Name = "McpList", ItemsSource = Enumerable.Range(0, 300).Select(i => "row " + i).ToList() };
            app.Window.Root.Children.Add(new ScrollViewer { Name = "McpScroller", Height = 80, Content = list });
            return 0;
        });
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(app.Hub.BaseUri, "mcp") }));
        try
        {
            var churn = Churn(3500);
            var r = await client.CallToolAsync("performance_sample", new Dictionary<string, object?> { ["durationMs"] = 2000 });
            await churn;
            var text = string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));
            Assert.NotEqual(true, r.IsError);
            Assert.Matches(@"FPS: avg [\d.]+, min [\d.]+", text);
            Assert.Matches(@"measure\s+P50 [\d.]+ ms\s+P95", text);
            Assert.Contains("visuals:", text);
            Assert.Contains("Hot style selectors:", text);
            Assert.Contains("[warning] ItemsControl#McpList is not virtualized: 300 ContentPresenter realized", text);
            Assert.Contains("Conclusion: ItemsControl#McpList is not virtualized", text);

            // Restores the previous state: nothing is collecting afterwards.
            Assert.False(S.MetricsActive);
            // Focus on an element elsewhere: the finding is filtered out.
            var save = (await S.SearchAsync(new SearchParams("#SaveButton"))).Matches.Single().Id;
            var r2 = await client.CallToolAsync("performance_sample", new Dictionary<string, object?> { ["durationMs"] = 1000, ["element"] = $"e{save}" });
            var t2 = string.Join("\n", r2.Content.OfType<TextContentBlock>().Select(t => t.Text));
            Assert.DoesNotContain("McpList", t2);
        }
        finally
        {
            await app.OnUi(() => { app.Window.Root.Children.Remove(app.Window.Root.Children.OfType<ScrollViewer>().First(s => s.Name == "McpScroller")); return 0; });
        }
    }
}
