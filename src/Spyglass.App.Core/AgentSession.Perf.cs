using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.App.Core;

// Performance (design/protocol/01-methods §12). Metrics collection in the agent is global to the connection, so the
// Performance page and MCP `performance_sample` share it through reference-counted leases: the first lease turns it
// on, the last one turns it off, and nobody switches off what somebody else is still using.
public sealed partial class AgentSession
{
    readonly object _metricsGate = new();
    int _metricsLeases;

    /// <summary>Raised from a background thread once a second while any lease is held.</summary>
    public event Action<MetricsSampleParams>? MetricsSample;

    /// <summary>The newest sample, or null when collection is off.</summary>
    public MetricsSampleParams? LastMetrics { get; private set; }

    internal void RaiseMetricsSample(MetricsSampleParams p)
    {
        LastMetrics = p;
        MetricsSample?.Invoke(p);
    }

    public bool MetricsActive { get { lock (_metricsGate) return _metricsLeases > 0; } }

    public Task<MetricsStatusResult> ConfigureMetricsAsync(bool enabled, bool reset = false, bool activities = true, CancellationToken ct = default) =>
        Call<MetricsConfigureParams, MetricsStatusResult>(Methods.MetricsConfigure,
            new MetricsConfigureParams(enabled) { Reset = reset, Activities = activities }, ct);

    /// <summary>Starts metrics if this is the first lease. Dispose the lease to release. <see cref="MetricsLease.Status"/> tells whether collection really runs.</summary>
    public async Task<MetricsLease> AcquireMetricsAsync(bool activities = true, CancellationToken ct = default)
    {
        bool first;
        lock (_metricsGate) first = _metricsLeases++ == 0;
        try
        {
            var status = await ConfigureMetricsAsync(true, reset: first, activities, ct);
            return new MetricsLease(this, status);
        }
        catch
        {
            lock (_metricsGate) _metricsLeases--;
            throw;
        }
    }

    async Task ReleaseMetricsAsync()
    {
        bool last;
        lock (_metricsGate) last = --_metricsLeases == 0;
        if (!last) return;
        LastMetrics = null;
        try { await ConfigureMetricsAsync(false); } catch { /* connection gone: the agent already stopped everything */ }
    }

    public sealed class MetricsLease : IAsyncDisposable
    {
        AgentSession? _session;
        internal MetricsLease(AgentSession s, MetricsStatusResult status) { _session = s; Status = status; }
        public MetricsStatusResult Status { get; }
        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _session, null) is { } s ? new ValueTask(s.ReleaseMetricsAsync()) : default;
    }

    // ---- overlays ----
    public Task<OverlaysResult> SetOverlaysAsync(int topLevelId, IReadOnlyList<DebugOverlay> overlays, CancellationToken ct = default) =>
        Call<OverlaysSetParams, OverlaysResult>(Methods.OverlaysSet, new OverlaysSetParams(topLevelId, overlays), ct);
    public Task<OverlaysResult> GetOverlaysAsync(int id, CancellationToken ct = default) =>
        Call<NodeParams, OverlaysResult>(Methods.OverlaysGet, new NodeParams(id), ct);
}
