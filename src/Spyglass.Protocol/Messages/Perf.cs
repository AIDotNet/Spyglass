using System.Text.Json.Serialization;

namespace Spyglass.Protocol.Messages;

// ===== metrics / overlays (v0.3, design/protocol/01-methods §12) =====

/// <param name="Enabled">Start (true) or stop (false) collecting. Everything is off until the client enables it.</param>
/// <param name="Reset">Clear windows and hot spots (also implied when collection starts from off).</param>
/// <param name="Activities">Also listen to Avalonia activities (style / resource / layout hot spots). Costs more; default true.</param>
public sealed record MetricsConfigureParams(bool Enabled)
{
    public bool Reset { get; init; }
    public bool Activities { get; init; } = true;
}

public sealed record MetricsStatusResult(bool Enabled, bool DiagnosticsSwitch, bool Activities);

/// <summary>Percentiles over the rolling window (about 10 s) of one histogram instrument; times in ms.</summary>
public sealed record HistogramSummary(double P50, double P90, double P95, long Count)
{
    public double Avg { get; init; }
    public double Max { get; init; }
}

/// <summary>Kind: style (selector), resource (key), layout (control type, self time), event (routed event), hitTest.</summary>
public sealed record HotSpotDto(string Kind, string Name, long Count, double TotalMs, double MaxMs);

/// <summary>Output of a diagnostic rule. Severity: info | warning.</summary>
public sealed record FindingDto(string Rule, string Severity, string Message)
{
    public int? ElementId { get; init; }
    public int? Count { get; init; }
}

/// <summary>
/// One-second sample. <see cref="Fps"/> = compositor frames rendered in the interval / elapsed time.
/// <see cref="Histograms"/> are keyed by Avalonia instrument name (avalonia.ui.measure.time, …).
/// <see cref="HotSpots"/> are cumulative since collection started (top entries per kind).
/// </summary>
public sealed record MetricsSampleParams(double? Fps, int? VisualCount, IReadOnlyDictionary<string, HistogramSummary> Histograms)
{
    public long Seq { get; init; }
    public double IntervalMs { get; init; }
    public int Frames { get; init; }
    public int? EventHandlerCount { get; init; }
    /// <summary>Mean of each histogram's measurements recorded during the last interval (ms).</summary>
    public IReadOnlyDictionary<string, double>? Averages { get; init; }
    public IReadOnlyList<HotSpotDto>? HotSpots { get; init; }
    public IReadOnlyList<FindingDto>? Findings { get; init; }
}

/// <summary>Current overlays of one TopLevel (result of overlays/set and overlays/get).</summary>
public sealed record OverlaysResult(int TopLevelId, IReadOnlyList<DebugOverlay> Overlays);
