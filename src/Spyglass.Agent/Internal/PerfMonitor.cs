using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Rendering;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Agent.Internal;

/// <summary>
/// Performance monitoring (design/protocol/01-methods §12, wireframe W5). Everything is off until the client sends
/// metrics/configure {enabled:true}; disabling disposes the <see cref="MeterListener"/>, the <see cref="ActivityListener"/>
/// and the timer, so a disabled monitor costs nothing (the Avalonia instruments then see no listener).
/// <list type="bullet">
/// <item>Histograms (measure/arrange/render/input/compositor time) come from <c>Avalonia.Diagnostic.Meter</c>; each second's values
/// go into a bucket, the last <see cref="WindowSeconds"/> buckets give P50/P90/P95. Buckets are capped (reservoir sampling).</item>
/// <item>FPS = number of <c>avalonia.comp.render.time</c> measurements (one per frame the compositor actually rendered) in the
/// interval / elapsed seconds; falls back to <c>avalonia.ui.render.time</c> when the compositor reports nothing.</item>
/// <item>Visual / handler counts are observable instruments, polled once a second.</item>
/// <item>Hot spots come from <c>Avalonia.Diagnostic.Source</c> activities, aggregated by (kind, name) with self time
/// (duration minus nested activities), cumulative since collection started.</item>
/// <item>Rule engine v0: non-virtualized ItemsControls, huge visual trees, slow layout passes.</item>
/// </list>
/// Timer, rules and overlays run on the UI thread; listener callbacks may run on any thread and only take a short lock.
/// </summary>
internal sealed class PerfMonitor : IDisposable
{
    public const string MeterName = "Avalonia.Diagnostic.Meter";
    public const string SourceName = "Avalonia.Diagnostic.Source";
    public const string FrameInstrument = "avalonia.comp.render.time";
    public const string UiRenderInstrument = "avalonia.ui.render.time";
    public const string MeasureInstrument = "avalonia.ui.measure.time";
    public const string ArrangeInstrument = "avalonia.ui.arrange.time";
    const string VisualCount = "avalonia.ui.visual.count";
    const string HandlerCount = "avalonia.ui.event.handler.count";

    public const int WindowSeconds = 10;
    const int BucketCap = 2048;
    const int MaxHotSpotKeys = 4000;
    const int TopPerKind = 8;
    const int RuleEveryTicks = 5;
    /// <summary>Realized containers above which a non-virtualizing panel is reported.</summary>
    public const int NonVirtualizedThreshold = 100;
    const int LargeVisualTree = 10_000;

    readonly NodeRegistry _registry;
    readonly TreeWalker _walker;
    readonly Action<MetricsSampleParams> _push;
    readonly object _lock = new();

    MeterListener? _meters;
    ActivityListener? _activities;
    DispatcherTimer? _timer;
    readonly Stopwatch _clock = new();
    TimeSpan _lastTick;
    long _seq;
    int _ticks;

    readonly Dictionary<string, Series> _series = new();
    readonly Dictionary<string, double> _gauges = new();
    readonly Dictionary<(string Kind, string Name), Hot> _hot = new();
    List<FindingDto> _findings = [];

    public PerfMonitor(NodeRegistry registry, TreeWalker walker, Action<MetricsSampleParams> push)
    {
        _registry = registry;
        _walker = walker;
        _push = push;
    }

    public bool IsEnabled => _meters is not null;
    public bool ActivitiesEnabled => _activities is not null;
    public MetricsSampleParams? Latest { get; private set; }

    public static bool DiagnosticsSwitch =>
        AppContext.TryGetSwitch("Avalonia.Diagnostics.Diagnostic.IsEnabled", out var on) && on;

    // ---------- configure (UI thread) ----------

    public MetricsStatusResult Configure(MetricsConfigureParams p)
    {
        if (!p.Enabled) { Stop(); return Status(); }
        if (_meters is null)
        {
            if (DiagnosticsSwitch) Start(p.Activities);
        }
        else
        {
            if (p.Reset) Reset();
            SetActivities(p.Activities);
        }
        return Status();
    }

    MetricsStatusResult Status() => new(IsEnabled, DiagnosticsSwitch, ActivitiesEnabled);

    void Start(bool activities)
    {
        Reset();
        var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) => { if (inst.Meter.Name == MeterName) l.EnableMeasurementEvents(inst); },
        };
        listener.SetMeasurementEventCallback<double>((i, v, _, _) => Measure(i, v));
        listener.SetMeasurementEventCallback<long>((i, v, _, _) => Measure(i, v));
        listener.SetMeasurementEventCallback<int>((i, v, _, _) => Measure(i, v));
        listener.Start();
        _meters = listener;

        SetActivities(activities);
        _clock.Restart();
        _lastTick = TimeSpan.Zero;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
    }

    void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _meters?.Dispose();
        _meters = null;
        SetActivities(false);
        _clock.Reset();
        Latest = null;
        lock (_lock) { _series.Clear(); _hot.Clear(); _gauges.Clear(); }
        _findings = [];
    }

    void Reset()
    {
        lock (_lock) { _series.Clear(); _hot.Clear(); _gauges.Clear(); }
        _findings = [];
        _ticks = 0;
    }

    void SetActivities(bool on)
    {
        if (on == (_activities is not null)) return;
        if (!on)
        {
            _activities!.Dispose();
            _activities = null;
            return;
        }
        var l = new ActivityListener
        {
            ShouldListenTo = s => s.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = OnActivity,
        };
        ActivitySource.AddActivityListener(l);
        _activities = l;
    }

    // ---------- listeners (any thread) ----------

    void Measure(Instrument inst, double value)
    {
        lock (_lock)
        {
            if (inst.IsObservable)
            {
                _gauges[inst.Name] = _gauges.GetValueOrDefault(inst.Name) + value;
                return;
            }
            if (!_series.TryGetValue(inst.Name, out var s)) _series[inst.Name] = s = new Series();
            s.Add(value);
        }
    }

    const string SelfKey = "sg.child";

    void OnActivity(Activity a)
    {
        var ms = a.Duration.TotalMilliseconds;
        var child = a.GetCustomProperty(SelfKey) is double c ? c : 0;
        if (a.Parent is { } parent)
            parent.SetCustomProperty(SelfKey, (parent.GetCustomProperty(SelfKey) is double pc ? pc : 0) + ms);
        var self = Math.Max(0, ms - child);

        var (kind, name) = a.OperationName switch
        {
            "Avalonia.AttachingStyle" or "Avalonia.EvaluatingStyle" => ("style", Tag(a, "Style") ?? Tag(a, "Selector")),
            "Avalonia.FindingResource" => ("resource", Tag(a, "Key")),
            "Avalonia.MeasuringLayoutable" or "Avalonia.ArrangingLayoutable" => ("layout", ShortType(Tag(a, "Control"))),
            "Avalonia.RaisingRoutedEvent" => ("event", Tag(a, "RoutedEvent")),
            "Avalonia.PerformingHitTest" => ("hitTest", ShortType(Tag(a, "Control"))),
            _ => (null, null),
        };
        if (kind is null) return;
        name = name is null ? "(unknown)" : name.Length > 160 ? name[..160] + "…" : name;
        lock (_lock)
        {
            if (!_hot.TryGetValue((kind, name), out var h))
            {
                if (_hot.Count >= MaxHotSpotKeys) return;
                _hot[(kind, name)] = h = new Hot();
            }
            h.Count++;
            h.TotalMs += self;
            if (self > h.MaxMs) h.MaxMs = self;
        }
    }

    static string? Tag(Activity a, string key) => a.GetTagItem(key)?.ToString() is { Length: > 0 } s ? s : null;
    static string? ShortType(string? t) => t is null ? null : t[(t.LastIndexOf('.') + 1)..];

    // ---------- the 1 s tick (UI thread) ----------

    void Tick()
    {
        if (_meters is null) return;
        var now = _clock.Elapsed;
        var interval = (now - _lastTick).TotalSeconds;
        _lastTick = now;
        if (interval <= 0) return;

        lock (_lock) _gauges.Clear();
        _meters.RecordObservableInstruments();

        var histograms = new Dictionary<string, HistogramSummary>();
        var averages = new Dictionary<string, double>();
        int frames, uiFrames;
        int? visuals = null, handlers = null;
        List<HotSpotDto> hot;
        lock (_lock)
        {
            frames = uiFrames = 0;
            foreach (var (name, s) in _series)
            {
                var (count, sum) = s.Roll(WindowSeconds);
                if (name == FrameInstrument) frames = (int)count;
                else if (name == UiRenderInstrument) uiFrames = (int)count;
                if (count > 0) averages[name] = sum / count;
                if (s.Summarize() is { } h) histograms[name] = h;
            }
            if (_gauges.TryGetValue(VisualCount, out var v)) visuals = (int)v;
            if (_gauges.TryGetValue(HandlerCount, out var eh)) handlers = (int)eh;
            hot = _hot
                .Select(kv => new HotSpotDto(kv.Key.Kind, kv.Key.Name, kv.Value.Count, Math.Round(kv.Value.TotalMs, 3), Math.Round(kv.Value.MaxMs, 3)))
                .GroupBy(h => h.Kind)
                .SelectMany(g => g.OrderByDescending(h => h.TotalMs).Take(TopPerKind))
                .ToList();
        }
        var framesUsed = frames > 0 ? frames : uiFrames;
        if (_ticks++ % RuleEveryTicks == 0) _findings = RunRules(visuals, histograms);

        var sample = new MetricsSampleParams(Math.Round(framesUsed / interval, 1), visuals, histograms)
        {
            Seq = ++_seq,
            IntervalMs = Math.Round(interval * 1000),
            Frames = framesUsed,
            EventHandlerCount = handlers,
            Averages = averages,
            HotSpots = hot,
            Findings = _findings,
        };
        Latest = sample;
        _push(sample);
    }

    // ---------- rules v0 (UI thread) ----------

    List<FindingDto> RunRules(int? visuals, Dictionary<string, HistogramSummary> histograms)
    {
        var list = new List<FindingDto>();
        foreach (var f in FindNonVirtualized(_walker.Roots().Select(r => r.TopLevel), NonVirtualizedThreshold, _registry))
        {
            list.Add(f);
            if (list.Count >= 20) break;
        }
        if (visuals is > LargeVisualTree)
            list.Add(new FindingDto("LargeVisualTree", "warning",
                $"Large visual tree: {visuals.Value.ToString("N0", CultureInfo.InvariantCulture)} visuals. Virtualize lists and collapse unused subtrees.") { Count = visuals });
        if (histograms.TryGetValue(MeasureInstrument, out var m) && m.P95 > 10 && m.Count >= 20)
            list.Add(new FindingDto("SlowMeasure", "warning",
                $"Slow measure passes: P95 {m.P95:F1} ms over {m.Count} controls. See the layout hot spots."));
        return list;
    }

    internal static IEnumerable<FindingDto> FindNonVirtualized(IEnumerable<TopLevel> roots, int threshold, NodeRegistry registry)
    {
        foreach (var tl in roots)
            foreach (var ic in tl.GetVisualDescendants().OfType<ItemsControl>())
            {
                if (ic.ItemsPanelRoot is not Panel panel || panel is VirtualizingPanel) continue;
                var n = panel.Children.Count;
                if (n <= threshold) continue;
                var label = ic.GetType().Name + (string.IsNullOrEmpty(ic.Name) ? "" : "#" + ic.Name);
                var container = panel.Children[0].GetType().Name;
                var scrolled = ic.FindAncestorOfType<ScrollViewer>() is not null || ic.GetVisualDescendants().Any(v => v is ScrollViewer);
                yield return new FindingDto("NonVirtualizedItems", scrolled ? "warning" : "info",
                    $"{label} is not virtualized: {n.ToString("N0", CultureInfo.InvariantCulture)} {container} realized (panel {panel.GetType().Name}). Use a VirtualizingStackPanel.")
                { ElementId = registry.IdOf(ic), Count = n };
            }
    }

    // ---------- overlays (UI thread) ----------

    readonly Dictionary<TopLevel, RendererDebugOverlays> _savedOverlays = new();

    public OverlaysResult SetOverlays(OverlaysSetParams p)
    {
        var tl = ResolveTopLevel(p.TopLevelId);
        _savedOverlays.TryAdd(tl, tl.RendererDiagnostics.DebugOverlays);
        var flags = RendererDebugOverlays.None;
        foreach (var o in p.Overlays) flags |= ToFlag(o);
        tl.RendererDiagnostics.DebugOverlays = flags;
        return GetOverlays(p.TopLevelId);
    }

    public OverlaysResult GetOverlays(int id)
    {
        var tl = ResolveTopLevel(id);
        var f = tl.RendererDiagnostics.DebugOverlays;
        return new OverlaysResult(_registry.IdOf(tl), Enum.GetValues<DebugOverlay>().Where(o => f.HasFlag(ToFlag(o))).ToList());
    }

    TopLevel ResolveTopLevel(int id) =>
        _registry.Get<object>(id) switch
        {
            TopLevel t => t,
            Visual v when TopLevel.GetTopLevel(v) is { } t => t,
            _ => throw new SpyglassProtocolException(ErrorCodes.InvalidParams, $"Node {id} is not inside a window."),
        };

    static RendererDebugOverlays ToFlag(DebugOverlay o) => o switch
    {
        DebugOverlay.Fps => RendererDebugOverlays.Fps,
        DebugOverlay.DirtyRects => RendererDebugOverlays.DirtyRects,
        DebugOverlay.LayoutTimeGraph => RendererDebugOverlays.LayoutTimeGraph,
        DebugOverlay.RenderTimeGraph => RendererDebugOverlays.RenderTimeGraph,
        _ => RendererDebugOverlays.None,
    };

    /// <summary>Put back what the app had before the client changed it.</summary>
    void RestoreOverlays()
    {
        foreach (var (tl, flags) in _savedOverlays)
            try { tl.RendererDiagnostics.DebugOverlays = flags; } catch { /* window already closed */ }
        _savedOverlays.Clear();
    }

    /// <summary>UI thread.</summary>
    public void Dispose()
    {
        Stop();
        RestoreOverlays();
    }

    // ---------- aggregation ----------

    sealed class Hot { public long Count; public double TotalMs, MaxMs; }

    sealed class Series
    {
        record Bucket(double[] Values, long Count, double Sum, double Max);

        readonly Queue<Bucket> _window = new();
        List<double> _cur = new();
        long _curCount;
        double _curSum, _curMax;

        public void Add(double v)
        {
            _curCount++;
            _curSum += v;
            if (v > _curMax) _curMax = v;
            if (_cur.Count < BucketCap) _cur.Add(v);
            else
            {
                // Reservoir sampling keeps the percentiles unbiased when a second has more than BucketCap values.
                var j = Random.Shared.NextInt64(_curCount);
                if (j < BucketCap) _cur[(int)j] = v;
            }
        }

        /// <summary>Closes the current one-second bucket; returns its count and sum.</summary>
        public (long Count, double Sum) Roll(int keep)
        {
            var r = (_curCount, _curSum);
            _window.Enqueue(new Bucket(_cur.ToArray(), _curCount, _curSum, _curMax));
            while (_window.Count > keep) _window.Dequeue();
            _cur.Clear();
            _curCount = 0; _curSum = 0; _curMax = 0;
            return r;
        }

        public HistogramSummary? Summarize()
        {
            long count = _window.Sum(b => b.Count);
            if (count == 0) return null;
            var all = _window.SelectMany(b => b.Values).ToArray();
            Array.Sort(all);
            return new HistogramSummary(Pct(all, .5), Pct(all, .9), Pct(all, .95), count)
            {
                Avg = Math.Round(_window.Sum(b => b.Sum) / count, 4),
                Max = Math.Round(_window.Max(b => b.Max), 4),
            };
        }

        static double Pct(double[] sorted, double p) =>
            sorted.Length == 0 ? 0 : Math.Round(sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)], 4);
    }
}
