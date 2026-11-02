using System.Net.WebSockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Agent.Internal;

/// <summary>
/// One connection to a Spyglass client: registers every request handler and runs it on the UI thread.
/// Ids are session-scoped (a new <see cref="NodeRegistry"/> per connection).
/// </summary>
internal sealed class AgentConnection : IAsyncDisposable
{
    readonly JsonRpcPeer _peer;
    readonly NodeRegistry _registry = new();
    readonly TreeWalker _walker;
    readonly ValueCodec _codec;
    readonly PropertyInspector _props;
    readonly Highlighter _highlighter = new();
    readonly PickController _pick;
    readonly EventTracer _events;

    readonly Network.NetworkMonitor _network;
    readonly PerfMonitor _perf;
    readonly TreeSync _treeSync;

    public AgentConnection(WebSocket socket, IReadOnlyList<IAgentFeature>? features = null, Func<Uri, bool>? isSpyglassTraffic = null)
    {
        _peer = new JsonRpcPeer(socket);
        _walker = new TreeWalker(_registry);
        _codec = new ValueCodec(_registry);
        _props = new PropertyInspector(_registry, _codec);
        _pick = new PickController(_walker, _highlighter);
        _events = new EventTracer(_registry, p => _ = _peer.NotifyAsync(Methods.EventsRaised, p));
        _pick.Hover += id => _ = _peer.NotifyAsync(Methods.PickHover, new PickHoverParams(id));
        _pick.Selected += (id, path) =>
        {
            _ = _peer.NotifyAsync(Methods.PickSelected, new PickSelectedParams(id, path));
            // Picking started from the app (inspect hotkey): bring Spyglass to the front with the element selected.
            if (_pickFromApp && id is not null) _ = _peer.NotifyAsync(Methods.SessionActivate, new ActivateParams("pick", id));
            _pickFromApp = false;
        };
        // Network capture (design/network): off until the client opens the Network panel; never records Spyglass's own traffic.
        _treeSync = new TreeSync(_registry, _walker, p => _ = _peer.NotifyAsync(Methods.TreeChanged, p));
        _network = new Network.NetworkMonitor(isSpyglassTraffic ?? (_ => false), (method, items) => _ = _peer.NotifyAsync(method, new NetworkRequestsParams(items)));
        RegisterNetwork();
        _perf = new PerfMonitor(_registry, _walker, p => _ = _peer.NotifyAsync(Methods.MetricsSample, p));
        RegisterPerf();
        Register();
        new ResourceInspector(_registry, _codec).Register(this);
        new ActionHandlers(_peer, _registry, _walker, _codec, _props).Register();   // input/act, wait/until, changes/reset
        TrackRoots();
        foreach (var f in features ?? []) (f as IConnectionFeature)?.Register(this);
        if (BindingErrorCollector.Instance is { } collector)
        {
            collector.Changed += OnBindingErrorsChanged;
            _subscriptions.Add(new Unsubscribe(() => collector.Changed -= OnBindingErrorsChanged));
        }
        if (LogCollector.Instance is { } logs)
        {
            _logCursor = logs.LatestSeq;
            logs.Changed += OnLogsChanged;
            _subscriptions.Add(new Unsubscribe(() => logs.Changed -= OnLogsChanged));
        }
    }

    // ---------- log push (design/protocol/01-methods §10: 250 ms batches) ----------

    const int MaxLogPush = 500;
    int _logPushQueued;
    long _logCursor;

    /// <summary>Any thread. Entries logged before the session started are not pushed; the client reads them with logs/get.</summary>
    void OnLogsChanged()
    {
        if (Interlocked.Exchange(ref _logPushQueued, 1) == 1) return;
        Dispatcher.UIThread.Post(() => DispatcherTimer.RunOnce(PushLogs, TimeSpan.FromMilliseconds(250)));
    }

    void PushLogs()
    {
        Volatile.Write(ref _logPushQueued, 0);
        var (entries, omitted) = LogCollector.Instance!.Snapshot(_registry, _logCursor, limit: MaxLogPush);
        if (entries.Count == 0) return;
        _logCursor = entries[^1].Seq;
        _ = _peer.NotifyAsync(Methods.LogsEntries, new LogEntriesParams(entries) { Dropped = omitted });
    }

    // ---------- binding error push (design/protocol/01-methods §9: aggregated, 250 ms) ----------

    bool _bindingPushQueued;
    long _bindingCursor;

    void OnBindingErrorsChanged()
    {
        if (_bindingPushQueued) return;
        _bindingPushQueued = true;
        DispatcherTimer.RunOnce(() =>
        {
            _bindingPushQueued = false;
            var changed = BindingErrorCollector.Instance!.Snapshot(_registry, null, _bindingCursor);
            if (changed.Count == 0) return;
            _bindingCursor = changed.Max(e => e.Seq);
            _ = _peer.NotifyAsync(Methods.BindingsError, new BindingErrorsParams(changed));
        }, TimeSpan.FromMilliseconds(250));
    }

    sealed class Unsubscribe(Action a) : IDisposable { public void Dispose() => a(); }

    void RegisterNetwork()
    {
        // Not UI-bound: run on the connection's thread pool.
        _peer.Handle<NetworkEnableParams, NetworkStatusResult>(Methods.NetworkEnable, (p, _) =>
        {
            _network.Enable(p);
            return Task.FromResult(new NetworkStatusResult(true, _network.Count));
        });
        _peer.Handle<Empty, NetworkStatusResult>(Methods.NetworkDisable, (_, _) =>
        {
            _network.Disable();
            return Task.FromResult(new NetworkStatusResult(false, _network.Count));
        });
        _peer.Handle<Empty, NetworkStatusResult>(Methods.NetworkStatus, (_, _) =>
            Task.FromResult(new NetworkStatusResult(_network.IsEnabled, _network.Count)));
        _peer.Handle<Empty, NetworkRequestsParams>(Methods.NetworkClear, (_, _) =>
        {
            _network.Clear();
            return Task.FromResult(new NetworkRequestsParams([]));
        });
        _peer.Handle<NetworkGetBodyParams, NetworkBodyResult>(Methods.NetworkGetBody, (p, _) => Task.FromResult(_network.GetBody(p)));
        _peer.Handle<NetworkSearchParams, NetworkSearchResult>(Methods.NetworkSearch, (p, _) => Task.FromResult(_network.Search(p)));
    }

    void RegisterPerf()
    {
        // Metrics, activity listeners and overlays are off until the client asks (Performance page / performance_sample).
        Handle<MetricsConfigureParams, MetricsStatusResult>(Methods.MetricsConfigure, _perf.Configure);
        Handle<OverlaysSetParams, OverlaysResult>(Methods.OverlaysSet, _perf.SetOverlays);
        Handle<NodeParams, OverlaysResult>(Methods.OverlaysGet, p => _perf.GetOverlays(p.Id));
    }

    // ---------- feature API (Spyglass.Agent.Advanced) ----------

    internal NodeRegistry Registry => _registry;
    internal ValueCodec Codec => _codec;
    internal void HandleOnUi<TP, TR>(string method, Func<TP, TR> onUiThread) => Handle(method, onUiThread);

    public JsonRpcPeer Peer => _peer;

    bool _pickFromApp;

    /// <summary>Inspect hotkey: start picking in the app and tell the client (UI thread).</summary>
    public void StartPickFromApp()
    {
        _pickFromApp = true;
        _pick.Start();
        _ = _peer.NotifyAsync(Methods.PickStarted, Empty.Instance);
    }

    static Task<T> Ui<T>(Func<T> f) => Dispatcher.UIThread.InvokeAsync(f).GetTask();

    void Handle<TP, TR>(string method, Func<TP, TR> onUiThread) =>
        _peer.Handle<TP, TR>(method, (p, _) => Ui(() => onUiThread(p)));

    void Register()
    {
        // ---- tree ----
        Handle<Empty, GetRootsResult>(Methods.TreeGetRoots, _ =>
        {
            var roots = _walker.Roots();
            _knownRoots = roots.Select(r => (object)r.TopLevel).ToList();   // baseline for rootAdded/rootRemoved
            return new GetRootsResult(roots.Select(ToRootInfo).ToList());
        });

        Handle<GetChildrenParams, GetChildrenResult>(Methods.TreeGetChildren, p =>
        {
            var node = _walker.Build(_registry.Get<object>(p.Id), p.Tree, p.Depth);
            _treeSync.Record(node, p.Tree);   // remember what the client now has, to push only differences later
            return new GetChildrenResult(node);
        });

        Handle<GetAncestorsParams, GetAncestorsResult>(Methods.TreeGetAncestors, p =>
        {
            var o = _registry.Get<object>(p.Id);
            var path = new List<NodeRef>();
            for (var cur = o; cur is not null; cur = TreeWalker.Parent(cur, p.Tree))
                path.Add(new NodeRef(_registry.IdOf(cur), cur.GetType().Name, (cur as StyledElement)?.Name));
            path.Reverse();
            return new GetAncestorsResult(path);
        });

        Handle<SearchParams, SearchResult>(Methods.TreeSearch, p =>
        {
            var query = new SelectorQuery(p.Query);
            IEnumerable<object> roots = p.RootId is { } rid ? [_registry.Get<object>(rid)] : _walker.Roots().Select(r => (object)r.TopLevel);
            var matches = query.Run(_walker, roots, p.Tree, p.IncludeTemplate).Take(p.Limit + 1).ToList();
            return new SearchResult(
                matches.Take(p.Limit).Select(o => new SearchMatch(_registry.IdOf(o), _walker.PathOf(o, p.Tree), _walker.Describe(o) with
                {
                    ChildCount = TreeWalker.Children(o, p.Tree).Count(),
                })).ToList(),
                matches.Count > p.Limit);
        });

        // ---- node ----
        Handle<NodeParams, DescribeResult>(Methods.NodeDescribe, p =>
        {
            var o = _registry.Get<object>(p.Id);
            var tl = _walker.TopLevelOf(o);
            return new DescribeResult(
                _walker.Describe(o) with { ChildCount = TreeWalker.Children(o, TreeKind.Visual).Count() },
                _walker.SelectorOf(o),
                (o as StyledElement)?.DataContext?.GetType().FullName,
                SourceOf(o),
                tl is null ? null : _registry.IdOf(tl));
        });

        Handle<ScreenshotParams, ScreenshotResult>(Methods.NodeScreenshot, p => Screenshot(_registry.Get<Visual>(p.Id), p.Scale));

        Handle<NodeParams, Empty>(Methods.NodeBringIntoView, p =>
        {
            (_registry.Get<Visual>(p.Id) as Control)?.BringIntoView();
            return Empty.Instance;
        });

        // ---- props / layout / styles ----
        Handle<PropsGetParams, PropsGetResult>(Methods.PropsGet, _props.Get);
        Handle<PropsSetParams, PropsSetResult>(Methods.PropsSet, p => new PropsSetResult(_props.Set(p)));
        Handle<PropsResetParams, Empty>(Methods.PropsReset, p => { _props.Reset(p); return Empty.Instance; });
        Handle<NodeParams, LayoutResult>(Methods.LayoutGet, p => _props.Layout(p.Id));
        Handle<NodeParams, LayoutExplainResult>(Methods.LayoutExplain, p => new LayoutExplainer(_registry, _props).Explain(p.Id));
        Handle<NodeParams, ClassesResult>(Methods.StylesGetClasses, p => _props.Classes(p.Id));
        Handle<SetClassesParams, Empty>(Methods.StylesSetClasses, p => { _props.SetClasses(p); return Empty.Instance; });
        Handle<SetPseudoClassParams, Empty>(Methods.StylesSetPseudoClass, p => { _props.SetPseudoClass(p); return Empty.Instance; });

        // ---- pick / highlight ----
        Handle<PickStartParams, Empty>(Methods.PickStart, _ => { _pick.Start(); return Empty.Instance; });
        Handle<Empty, Empty>(Methods.PickStop, _ => { _pick.Stop(); _highlighter.Hide(); return Empty.Instance; });
        Handle<HighlightShowParams, Empty>(Methods.HighlightShow, p =>
        {
            _highlighter.Show(_registry.Get<Visual>(p.Id), p.Style, p.DurationMs);
            return Empty.Instance;
        });
        Handle<Empty, Empty>(Methods.HighlightHide, _ => { _highlighter.Hide(); return Empty.Instance; });

        // ---- bindings ----
        Handle<GetBindingErrorsParams, BindingErrorsParams>(Methods.BindingsGetErrors, p =>
            new BindingErrorsParams(BindingErrorCollector.Instance?.Snapshot(_registry, p.TargetId, p.Since) ?? []));

        // ---- logs ----
        Handle<LogsConfigureParams, Empty>(Methods.LogsConfigure, p => { RequireLogs().Configure(p); return Empty.Instance; });
        Handle<LogsGetParams, LogsGetResult>(Methods.LogsGet, p =>
        {
            var logs = RequireLogs();
            var (entries, omitted) = logs.Snapshot(_registry, p.Since,
                p.MinLevel is null ? null : LogCollector.ParseLevel(p.MinLevel), p.Area, Math.Clamp(p.Limit, 0, LogCollector.DefaultCapacity));
            return new LogsGetResult(entries, omitted, LogCollector.LevelName(logs.MinLevel), logs.Areas);
        });
        // ---- events (tracing runs only between configure and disconnect / empty configure) ----
        Handle<Empty, ListRoutedResult>(Methods.EventsListRouted, _ => EventTracer.List());
        Handle<EventsConfigureParams, EventsConfigureResult>(Methods.EventsConfigure, _events.Configure);
        Handle<Empty, Empty>(Methods.EventsPause, _ => { _events.Pause(); return Empty.Instance; });
        Handle<Empty, Empty>(Methods.EventsResume, _ => { _events.Resume(); return Empty.Instance; });
        Handle<EventsGetParams, EventsRaisedParams>(Methods.EventsGet, _events.Get);

        // ---- source ----
        Handle<NodeParams, SourceResult>(Methods.SourceGet, p => new SourceResult(SourceOf(_registry.Get<object>(p.Id))));
    }

    static LogCollector RequireLogs() =>
        LogCollector.Instance ?? throw new SpyglassProtocolException(ErrorCodes.CapabilityMissing, "Log capture is not installed.");

    RootInfo ToRootInfo((TopLevel TopLevel, RootKind Kind, string? Title, StyledElement? Owner) r) =>
        new(_walker.Build(r.TopLevel, TreeKind.Visual, 0), r.Kind, r.Title,
            r.Owner is null ? null : _registry.IdOf(r.Owner),
            r.TopLevel is Window { IsActive: true });

    // ---------- multi-window tracking ----------

    List<object>? _knownRoots;   // null until the client has asked for roots
    bool _rootCheckQueued;
    readonly List<IDisposable> _subscriptions = new();

    /// <summary>
    /// Windows and popups come and go while the app runs (dialogs, tool windows, menus). Watch window
    /// open/close, window activation and popup open/close globally, then diff the root set and push
    /// tree/changed rootAdded/rootRemoved. Also re-hooks picking onto new windows.
    /// </summary>
    void TrackRoots()
    {
        _subscriptions.Add(Window.WindowOpenedEvent.Raised.Subscribe(new Observer<(object, RoutedEventArgs)>(_ => QueueRootCheck())));
        _subscriptions.Add(Window.WindowClosedEvent.Raised.Subscribe(new Observer<(object, RoutedEventArgs)>(_ => QueueRootCheck())));
        _subscriptions.Add(Popup.IsOpenProperty.Changed.Subscribe(new Observer<AvaloniaPropertyChangedEventArgs<bool>>(_ => QueueRootCheck())));
        _subscriptions.Add(WindowBase.IsActiveProperty.Changed.Subscribe(new Observer<AvaloniaPropertyChangedEventArgs<bool>>(e =>
        {
            if (_knownRoots is not null && e.NewValue.GetValueOrDefault() && e.Sender is Window w)
                _ = _peer.NotifyAsync(Methods.TreeChanged, new TreeChangedParams(TreeKind.Visual,
                    [new TreeOpUpdate(_registry.IdOf(w), w.Name, null, w.IsEffectivelyVisible) { IsActive = true }]));
        })));
    }

    void QueueRootCheck()
    {
        if (_rootCheckQueued) return;
        _rootCheckQueued = true;
        // Background priority: runs after the window/popup finished opening or closing.
        Dispatcher.UIThread.Post(CheckRoots, DispatcherPriority.Background);
    }

    void CheckRoots()
    {
        _rootCheckQueued = false;
        if (_pick.IsActive) _pick.HookNewRoots();
        if (_knownRoots is null) return;
        var now = _walker.Roots();
        var ops = new List<TreeOp>();
        foreach (var r in now.Where(r => !_knownRoots.Contains(r.TopLevel)))
            ops.Add(new TreeOpRootAdded(ToRootInfo(r)));
        foreach (var gone in _knownRoots.Where(k => now.All(r => !ReferenceEquals(r.TopLevel, k))))
            ops.Add(new TreeOpRootRemoved(_registry.IdOf(gone)));
        _knownRoots = now.Select(r => (object)r.TopLevel).ToList();
        if (ops.Count > 0) _ = _peer.NotifyAsync(Methods.TreeChanged, new TreeChangedParams(TreeKind.Visual, ops));
    }

    sealed class Observer<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => onNext(value);
    }

    static SourceInfo? SourceOf(object o) =>
        XamlSourceInfo.GetXamlSourceInfo(o) is { SourceUri: { } uri } si ? new SourceInfo(uri.ToString(), XamlLines.ToOneBased(si.LineNumber, o), si.LinePosition) : null;

    static ScreenshotResult Screenshot(Visual v, double scale)
    {
        scale = Math.Clamp(scale, 0.1, 4);
        var w = (int)Math.Ceiling(v.Bounds.Width * scale);
        var h = (int)Math.Ceiling(v.Bounds.Height * scale);
        if (w <= 0 || h <= 0)
            throw new SpyglassProtocolException(ErrorCodes.InvalidParams, $"{v.GetType().Name} has zero size ({v.Bounds.Width}×{v.Bounds.Height}); nothing to capture.");
        using var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96 * scale, 96 * scale));
        rtb.Render(v);
        using var ms = new MemoryStream();
#if AVALONIA11
        rtb.Save(ms);   // Avalonia 11: PNG is the default
#else
        rtb.Save(ms, PngBitmapEncoderOptions.Default);
#endif
        return new ScreenshotResult("png", w, h, Convert.ToBase64String(ms.ToArray()));
    }

    public Task SendHelloAsync(HelloParams hello) => _peer.RequestAsync<HelloParams, HelloResult>(Methods.SessionHello, hello);

    public async ValueTask DisposeAsync()
    {
        _network.Dispose();   // stop intercepting as soon as the client goes away (design/network §3)
        foreach (var s in _subscriptions) s.Dispose();
        await Dispatcher.UIThread.InvokeAsync(() => { _events.Dispose(); _perf.Dispose(); _treeSync.Dispose(); _pick.Stop(); _highlighter.Hide(); });
        await _peer.DisposeAsync();
    }
}
