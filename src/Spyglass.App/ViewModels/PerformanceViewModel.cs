using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using Spyglass.App.Core;
using Spyglass.App.Localization;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.App.ViewModels;

/// <summary>
/// Performance page (wireframe W5). Metrics collection in the agent runs only while this page is shown
/// (lease on <see cref="AgentSession"/>), like Network recording; leaving the page or disconnecting stops it.
/// </summary>
public sealed class PerformanceViewModel : ObservableObject
{
    const int HistoryLength = 60;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly AgentSession _session;
    readonly Action<int> _showElement;
    readonly SemaphoreSlim _gate = new(1, 1);
    AgentSession.MetricsLease? _lease;
    bool _active, _hasSample, _syncing;
    int? _topLevelId;
    double _fps, _layoutMs, _renderMs;
    int _visuals;
    string _fpsContext = "", _layoutContext = "", _renderContext = "";
    string _fpsText = "–", _layoutText = "–", _renderText = "–";
    bool _idle;
    double _peakFps;
    (DateTime At, double Fps)? _lastActive;
    (DateTime At, double Ms)? _lastLayout, _lastRender;
    string _status = "";

    public PerformanceViewModel(AgentSession session, Action<int> showElement)
    {
        _session = session;
        _showElement = showElement;
        session.MetricsSample += p => Dispatcher.UIThread.Post(() => Apply(p));
        WatchLanguage();
        Overlays = new ObservableCollection<OverlayToggle>(
        [
            new(DebugOverlay.Fps, "Perf.OverlayFps", this),
            new(DebugOverlay.DirtyRects, "Perf.OverlayDirtyRects", this),
            new(DebugOverlay.LayoutTimeGraph, "Perf.OverlayLayoutGraph", this),
            new(DebugOverlay.RenderTimeGraph, "Perf.OverlayRenderGraph", this),
        ]);
        ResetCommand = new RelayCommand(async () =>
        {
            Reset();
            if (_lease is not null) await session.ConfigureMetricsAsync(true, reset: true);
        });
    }

    public ObservableCollection<PerfPoint> History { get; } = new();
    public ObservableCollection<PercentileRow> Percentiles { get; } = new();
    public ObservableCollection<HotSpotGroup> HotSpots { get; } = new();
    public ObservableCollection<FindingRow> Findings { get; } = new();
    public ObservableCollection<OverlayToggle> Overlays { get; }
    public RelayCommand ResetCommand { get; }

    public bool Supported => _session.Has(Capabilities.Metrics);
    public bool Unsupported => !Supported;
    public bool OverlaysSupported => _session.Has(Capabilities.Overlays);
    public bool Waiting => Supported && !_hasSample;
    public bool HasData => Supported && _hasSample;
    public bool HasFindings => Findings.Count > 0;
    public bool NoFindings => Findings.Count == 0;
    public bool HasHotSpots => HotSpots.Count > 0;
    public bool NoHotSpots => HotSpots.Count == 0;

    /// <summary>
    /// What the cards show. A quiet app renders nothing (Avalonia draws only when something changes), which is
    /// not "no data": say so, and remember when it was last active instead of showing a row of zeros.
    /// </summary>
    public string FpsText { get => _fpsText; private set => Set(ref _fpsText, value); }
    public string LayoutText { get => _layoutText; private set => Set(ref _layoutText, value); }
    public string RenderText { get => _renderText; private set => Set(ref _renderText, value); }
    public bool IsIdle { get => _idle; private set => Set(ref _idle, value); }
    public bool NoPercentiles => Percentiles.Count == 0;

    public double Fps { get => _fps; private set => Set(ref _fps, value); }
    public double LayoutMs { get => _layoutMs; private set => Set(ref _layoutMs, value); }
    public double RenderMs { get => _renderMs; private set => Set(ref _renderMs, value); }
    public int Visuals { get => _visuals; private set => Set(ref _visuals, value); }
    public string FpsContext { get => _fpsContext; private set => Set(ref _fpsContext, value); }
    public string LayoutContext { get => _layoutContext; private set => Set(ref _layoutContext, value); }
    public string RenderContext { get => _renderContext; private set => Set(ref _renderContext, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    // ---------- lifecycle ----------

    /// <summary>Called by the main view model when this page is shown or left.</summary>
    public async void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        await _gate.WaitAsync();
        try
        {
            if (active == (_lease is not null) || active != _active) return;
            if (active)
            {
                if (!Supported) return;
                _lease = await _session.AcquireMetricsAsync();
                if (!_lease.Status.DiagnosticsSwitch)
                    Status = Loc.T("Perf.DiagnosticsOff");
                await SyncOverlaysAsync();
            }
            else
            {
                var l = _lease; _lease = null;
                if (l is not null) await l.DisposeAsync();
                _hasSample = false;
                Raise(nameof(Waiting)); Raise(nameof(HasData));
            }
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { _gate.Release(); }
    }

    void Reset()
    {
        History.Clear(); Percentiles.Clear(); HotSpots.Clear(); Findings.Clear();
        _hasSample = false; _peakFps = 0; _lastActive = null; _lastLayout = _lastRender = null; IsIdle = false;
        RaiseState();
    }

    void RaiseState()
    {
        Raise(nameof(Waiting)); Raise(nameof(HasData));
        Raise(nameof(HasFindings)); Raise(nameof(NoFindings)); Raise(nameof(NoPercentiles));
        Raise(nameof(HasHotSpots)); Raise(nameof(NoHotSpots));
    }

    // ---------- samples ----------

    static readonly (string Key, string Label)[] Instruments =
    [
        ("avalonia.ui.measure.time", "Perf.InstrMeasure"), ("avalonia.ui.arrange.time", "Perf.InstrArrange"),
        ("avalonia.ui.render.time", "Perf.InstrRender"), ("avalonia.comp.update.time", "Perf.InstrCompUpdate"),
        ("avalonia.comp.render.time", "Perf.InstrCompRender"), ("avalonia.ui.input.time", "Perf.InstrInput"),
    ];

    static readonly (string Kind, string Title)[] Kinds =
    [
        ("style", "Perf.KindStyle"), ("resource", "Perf.KindResource"), ("layout", "Perf.KindLayout"),
        ("event", "Perf.KindEvent"), ("hitTest", "Perf.KindHitTest"),
    ];

    void Apply(MetricsSampleParams p)
    {
        if (_lease is null) return;
        _hasSample = true;
        Status = "";
        var avg = p.Averages ?? new Dictionary<string, double>();
        var layout = avg.GetValueOrDefault("avalonia.ui.measure.time") + avg.GetValueOrDefault("avalonia.ui.arrange.time");
        var render = avg.GetValueOrDefault("avalonia.ui.render.time");
        Fps = p.Fps ?? 0;
        Visuals = p.VisualCount ?? 0;
        LayoutMs = Math.Round(layout, 2);
        RenderMs = Math.Round(render, 2);
        var now = DateTime.Now;
        var hasLayout = p.Histograms.ContainsKey("avalonia.ui.measure.time") || p.Histograms.ContainsKey("avalonia.ui.arrange.time");
        var hasRender = p.Histograms.ContainsKey("avalonia.ui.render.time");

        if (p.Frames > 0)
        {
            _peakFps = Math.Max(_peakFps, Fps);
            _lastActive = (now, Fps);
            FpsText = $"{Fps:0} fps";
            FpsContext = Loc.F("Perf.FramesLastSecond", p.Frames, _peakFps);
        }
        else
        {
            FpsText = Loc.T("Perf.Idle");
            FpsContext = _lastActive is { } la ? Loc.F("Perf.NoFramesLastActive", Ago(now - la.At), la.Fps) : Loc.T("Perf.NoFramesYet");
        }

        if (hasLayout) _lastLayout = (now, layout);
        LayoutText = hasLayout ? $"{LayoutMs:0.00} ms" : "–";
        LayoutContext = p.Histograms.TryGetValue("avalonia.ui.measure.time", out var m) ? Loc.F("Perf.MeasureP95", Ms(m.P95))
            : _lastLayout is { } ll ? Loc.F("Perf.NoLayoutLast", Ms(ll.Ms), Ago(now - ll.At)) : Loc.T("Perf.NoLayoutYet");

        if (hasRender) _lastRender = (now, render);
        RenderText = hasRender ? $"{RenderMs:0.00} ms" : "–";
        RenderContext = p.Histograms.TryGetValue("avalonia.ui.render.time", out var r) ? $"P95 {Ms(r.P95)}"
            : _lastRender is { } lr ? Loc.F("Perf.NoRenderLast", Ms(lr.Ms), Ago(now - lr.At)) : Loc.T("Perf.NoRenderYet");

        IsIdle = p.Frames == 0 && !hasLayout && !hasRender;

        History.Add(new PerfPoint(DateTime.Now.ToString("HH:mm:ss", Inv), Math.Round(Fps, 1), Math.Round(layout, 2), Math.Round(render, 2)));
        while (History.Count > HistoryLength) History.RemoveAt(0);

        Percentiles.Clear();
        foreach (var (key, label) in Instruments)
            if (p.Histograms.TryGetValue(key, out var h))
                Percentiles.Add(new PercentileRow(Loc.T(label), Ms(h.P50), Ms(h.P90), Ms(h.P95), Ms(h.Avg), Ms(h.Max), h.Count.ToString("N0", Inv)));

        HotSpots.Clear();
        foreach (var (kind, title) in Kinds)
        {
            var rows = (p.HotSpots ?? []).Where(h => h.Kind == kind).OrderByDescending(h => h.TotalMs).Take(6)
                .Select(h => new HotSpotRow(h.Name, "×" + h.Count.ToString("N0", Inv), Ms(h.TotalMs), Ms(h.MaxMs))).ToList();
            if (rows.Count > 0) HotSpots.Add(new HotSpotGroup(Loc.T(title), rows));
        }

        Findings.Clear();
        foreach (var f in p.Findings ?? [])
            Findings.Add(new FindingRow(f.Message, f.Severity == "warning" ? TKCode.Arc.AlertTone.Warning : TKCode.Arc.AlertTone.Info, f.ElementId, _showElement));
        RaiseState();
    }

    static string Ago(TimeSpan t) => t.TotalSeconds < 60 ? Loc.F("Perf.AgoSeconds", Math.Max(1, (int)t.TotalSeconds)) : Loc.F("Perf.AgoMinutes", (int)t.TotalMinutes);

    static string Ms(double v) => (v < 10 ? Math.Round(v, 2).ToString("0.##", Inv) : Math.Round(v, 1).ToString("0.#", Inv)) + " ms";

    // ---------- overlays ----------

    async Task<int?> TopLevelAsync()
    {
        if (_topLevelId is { } id) return id;
        var roots = (await _session.GetRootsAsync()).Roots;
        var root = roots.FirstOrDefault(r => r.Kind == RootKind.Window && r.IsActive) ?? roots.FirstOrDefault(r => r.Kind != RootKind.Popup);
        return _topLevelId = root?.Node.Id;
    }

    async Task SyncOverlaysAsync()
    {
        if (!OverlaysSupported || await TopLevelAsync() is not { } id) return;
        Show(await _session.GetOverlaysAsync(id));
    }

    void Show(OverlaysResult r)
    {
        _syncing = true;
        foreach (var o in Overlays) o.SetSilently(r.Overlays.Contains(o.Overlay));
        _syncing = false;
    }

    internal async void OverlayChanged()
    {
        if (_syncing) return;
        try
        {
            if (await TopLevelAsync() is not { } id) return;
            Show(await _session.SetOverlaysAsync(id, Overlays.Where(o => o.IsOn).Select(o => o.Overlay).ToList()));
        }
        catch (Exception ex) { Status = ex.Message; }
    }
}

public sealed class OverlayToggle(DebugOverlay overlay, string label, PerformanceViewModel owner) : ObservableObject
{
    bool _on;
    public DebugOverlay Overlay { get; } = overlay;
    public string Label => Loc.T(label);
    public bool IsOn { get => _on; set { if (Set(ref _on, value)) owner.OverlayChanged(); } }
    internal void SetSilently(bool on) { _on = on; Raise(nameof(IsOn)); }
}

public sealed record PerfPoint(string T, double Fps, double LayoutMs, double RenderMs);
public sealed record PercentileRow(string Name, string P50, string P90, string P95, string Avg, string Max, string Count);
public sealed record HotSpotRow(string Name, string Count, string Total, string Max);
public sealed record HotSpotGroup(string Title, IReadOnlyList<HotSpotRow> Rows);

public sealed class FindingRow(string message, TKCode.Arc.AlertTone tone, int? elementId, Action<int> show)
{
    public string Message { get; } = message;
    public TKCode.Arc.AlertTone Tone { get; } = tone;
    public bool CanShow { get; } = elementId is not null;
    public RelayCommand ShowCommand { get; } = new(() => { if (elementId is { } id) show(id); });
}
