using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Spyglass.App.Core;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Mcp;

/// <summary>Performance tools (design/mcp/01-tools.md #17).</summary>
[McpServerToolType]
public sealed class SpyglassPerfTools(SpyglassHub hub)
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [McpServerTool(Name = "performance_sample", Title = "Sample app performance", ReadOnly = true, Idempotent = false, OpenWorld = false)]
    [Description("Samples the app's performance for a few seconds: FPS, measure/arrange/render times (P50/P95), visual count, the hottest style selectors, resource keys and controls (from Avalonia's own profiling), and rule findings such as non-virtualized lists. Turns profiling on for the duration and restores the previous state. Avalonia only renders when something changes, so interact with or animate the app during the sample for meaningful FPS.")]
    public async Task<string> PerformanceSample(
        [Description("Sampling time in milliseconds. Default 3000, max 30000.")] int durationMs = 3000,
        [Description("Only report findings inside this window/element (an id like 'e12'). Timings and FPS are process-wide.")] string? element = null,
        [Description("Connected app (id like 'a1', name, or pid). Optional when only one app is connected.")] string? app = null,
        CancellationToken ct = default)
    {
        var s = ElementResolver.App(hub, app);
        if (!s.Has(Capabilities.Metrics))
            throw new McpException($"{s.App.Name}'s Spyglass agent has no performance metrics: Avalonia diagnostics are off (SpyglassAgentOptions.EnableAvaloniaDiagnostics) or the agent is outdated.");
        durationMs = Math.Clamp(durationMs, 500, 30_000);
        int? focus = element is null ? null : await ElementResolver.ElementAsync(s, element);

        var samples = new List<MetricsSampleParams>();
        void OnSample(MetricsSampleParams p) { lock (samples) samples.Add(p); }
        var baseline = s.LastMetrics;   // another consumer (the Performance page) may already be collecting
        s.MetricsSample += OnSample;
        try
        {
            await using var lease = await s.AcquireMetricsAsync(activities: true, ct);
            if (!lease.Status.Enabled)
                throw new McpException("The agent could not start metrics (Avalonia diagnostics switch is off).");
            await Task.Delay(durationMs + 1100, ct);
        }
        finally { s.MetricsSample -= OnSample; }

        MetricsSampleParams[] got;
        lock (samples) got = samples.Where(x => baseline is null || x.Seq > baseline.Seq).ToArray();
        if (got.Length == 0) return "No samples arrived (the app's UI thread may be blocked).";
        var last = got[^1];

        var findings = last.Findings ?? [];
        if (focus is { } f)
        {
            var kept = new List<FindingDto>();
            foreach (var fd in findings)
            {
                if (fd.ElementId is not { } id) { kept.Add(fd); continue; }
                try { if ((await s.GetAncestorsAsync(id, ct: ct)).Any(n => n.Id == f)) kept.Add(fd); } catch (SpyglassProtocolException) { }
            }
            findings = kept;
        }
        return Render(got, baseline, last, findings, durationMs);
    }

    static string Render(MetricsSampleParams[] got, MetricsSampleParams? baseline, MetricsSampleParams last, IReadOnlyList<FindingDto> findings, int durationMs)
    {
        var fps = got.Select(x => x.Fps ?? 0).ToArray();
        var sb = new StringBuilder();
        sb.AppendLine($"Performance over {got.Length} s (requested {durationMs} ms):");
        sb.AppendLine($"  FPS: avg {Num(fps.Average())}, min {Num(fps.Min())}, max {Num(fps.Max())}  ({got.Sum(x => x.Frames)} frames)");
        if (fps.Max() == 0) sb.AppendLine("  note: no frames were rendered; Avalonia only renders when something changes. Interact with the app during the sample.");
        foreach (var (label, key) in new[] { ("measure", "avalonia.ui.measure.time"), ("arrange", "avalonia.ui.arrange.time"), ("render", "avalonia.ui.render.time"), ("compositor", "avalonia.comp.render.time") })
            if (last.Histograms.TryGetValue(key, out var h))
                sb.AppendLine($"  {label,-10} P50 {Ms(h.P50)}  P95 {Ms(h.P95)}  max {Ms(h.Max)}  ({h.Count} in the last {PerfWindow(got.Length)} s)");
        sb.AppendLine($"  visuals: {(last.VisualCount is { } v ? v.ToString("N0", Inv) : "n/a")}   event handlers: {(last.EventHandlerCount is { } eh ? eh.ToString("N0", Inv) : "n/a")}");

        foreach (var (kind, title) in new[] { ("style", "style selectors"), ("resource", "resource keys"), ("layout", "layout (control self time)") })
        {
            var top = Delta(last, baseline, kind).OrderByDescending(h => h.TotalMs).Take(5).ToList();
            if (top.Count == 0) continue;
            sb.AppendLine($"Hot {title}:");
            foreach (var h in top) sb.AppendLine($"  {Ms(h.TotalMs),10}  ×{h.Count,-6} {h.Name}");
        }

        sb.AppendLine(findings.Count == 0 ? "Findings: none." : "Findings:");
        foreach (var f in findings)
            sb.AppendLine($"  [{f.Severity}] {f.Message}{(f.ElementId is { } id ? $" [e{id}]" : "")}");
        sb.Append("Conclusion: ").Append(Conclusion(fps, last, findings));
        return sb.ToString();
    }

    static int PerfWindow(int sampled) => Math.Min(sampled, 10);

    /// <summary>Hot spots are cumulative since collection started; subtract what was there before this sample began.</summary>
    static IEnumerable<HotSpotDto> Delta(MetricsSampleParams last, MetricsSampleParams? baseline, string kind)
    {
        var before = baseline?.HotSpots?.Where(h => h.Kind == kind).ToDictionary(h => h.Name);
        foreach (var h in last.HotSpots?.Where(h => h.Kind == kind) ?? [])
        {
            if (before is not null && before.TryGetValue(h.Name, out var b))
            {
                var d = h with { Count = h.Count - b.Count, TotalMs = h.TotalMs - b.TotalMs };
                if (d.Count > 0) yield return d;
            }
            else yield return h;
        }
    }

    static string Conclusion(double[] fps, MetricsSampleParams last, IReadOnlyList<FindingDto> findings)
    {
        if (findings.FirstOrDefault(f => f.Rule == "NonVirtualizedItems") is { } nv) return nv.Message;
        if (findings.FirstOrDefault() is { } f) return f.Message;
        if (fps.Max() > 0 && fps.Average() < 30) return $"Low frame rate ({Num(fps.Average())} FPS on average). Check the hot layout and style entries above.";
        if (fps.Max() == 0) return "The app was idle, so there is nothing to judge. Repeat while interacting with it.";
        var render = last.Histograms.GetValueOrDefault("avalonia.ui.render.time");
        return $"No problems found{(render is null ? "" : $"; render P95 {Ms(render.P95)}")}.";
    }

    static string Num(double v) => Math.Round(v, 1).ToString("0.#", Inv);
    static string Ms(double v) => (v < 10 ? Math.Round(v, 2).ToString("0.##", Inv) : Math.Round(v, 1).ToString("0.#", Inv)) + " ms";
}
