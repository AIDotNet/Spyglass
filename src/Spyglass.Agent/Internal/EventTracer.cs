using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Spyglass.Protocol.Messages;

namespace Spyglass.Agent.Internal;

/// <summary>
/// Routed-event tracing (design/protocol/01-methods §8, wireframe W3). Subscribes <see cref="RoutedEvent.Raised"/>
/// only for the enabled events. <c>Raised</c> fires once per node and phase on the route, after that node's
/// handlers ran (verified by the integration tests), so the calls are aggregated per <see cref="RoutedEventArgs"/>
/// instance into one <see cref="EventRecord"/>: the first node at which Handled is seen turning true handled it. A record is finalised by a
/// dispatcher post, which runs after the (synchronous) raise has completed. Records go to a ring buffer and are
/// pushed in 100 ms batches. UI-thread only.
/// </summary>
internal sealed class EventTracer : IDisposable
{
    /// <summary>Traced when tracing starts without a list (W3). PointerMoved is never traced unless asked for.</summary>
    public static readonly IReadOnlyList<string> Defaults =
    [
        "Button.Click", "InputElement.KeyDown", "InputElement.KeyUp", "InputElement.TextInput",
        "InputElement.PointerPressed", "InputElement.PointerReleased",
    ];

    const int PushIntervalMs = 100;
    const int MaxPerPush = 1000;

    readonly NodeRegistry _registry;
    readonly Action<EventsRaisedParams> _push;
    readonly Dictionary<RoutedEvent, IDisposable> _subscriptions = new();
    readonly Dictionary<RoutedEventArgs, Pending> _pending = new(ReferenceEqualityComparer.Instance);
    readonly Queue<EventRecord> _buffer = new();
    readonly List<EventRecord> _outbox = new();
    int _bufferSize = 5000;
    int _dropped;
    bool _pushQueued;
    bool _paused;
    long _seq;

    public EventTracer(NodeRegistry registry, Action<EventsRaisedParams> push)
    {
        _registry = registry;
        _push = push;
    }

    public static string Key(RoutedEvent e) => $"{e.OwnerType.Name}.{e.Name}";

    public bool IsTracing => _subscriptions.Count > 0;

    public static ListRoutedResult List() =>
        new(RoutedEventRegistry.Instance.GetAllRegistered()
            .Select(e => new RoutedEventInfo(e.Name, e.OwnerType.Name, Routes(e.RoutingStrategies)))
            .DistinctBy(e => (e.Owner, e.Name))
            .OrderBy(e => e.Owner, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList());

    static List<RouteStrategy> Routes(RoutingStrategies s)
    {
        var r = new List<RouteStrategy>(3);
        if (s.HasFlag(RoutingStrategies.Direct)) r.Add(RouteStrategy.Direct);
        if (s.HasFlag(RoutingStrategies.Tunnel)) r.Add(RouteStrategy.Tunnel);
        if (s.HasFlag(RoutingStrategies.Bubble)) r.Add(RouteStrategy.Bubble);
        return r;
    }

    /// <summary>Sets the traced events (null = defaults, empty = stop) and resumes recording.</summary>
    public EventsConfigureResult Configure(EventsConfigureParams p)
    {
        _bufferSize = Math.Clamp(p.BufferSize, 100, 100_000);
        while (_buffer.Count > _bufferSize) _buffer.Dequeue();

        var wanted = (p.Enabled ?? Defaults).Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var byKey = new Dictionary<string, RoutedEvent>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in RoutedEventRegistry.Instance.GetAllRegistered()) byKey.TryAdd(Key(e), e);

        var enabled = new HashSet<RoutedEvent>();
        var unknown = new List<string>();
        foreach (var name in wanted)
        {
            if (byKey.TryGetValue(name, out var e)) enabled.Add(e);
            else unknown.Add(name);
        }

        foreach (var gone in _subscriptions.Keys.Where(e => !enabled.Contains(e)).ToList())
        {
            _subscriptions[gone].Dispose();
            _subscriptions.Remove(gone);
        }
        foreach (var e in enabled.Where(e => !_subscriptions.ContainsKey(e)))
        {
            var ev = e;
            _subscriptions[ev] = ev.Raised.Subscribe(new Observer(x => OnRaised(x.Item1, x.Item2)));
        }
        if (_subscriptions.Count == 0) _pending.Clear();
        _paused = false;
        return new EventsConfigureResult(_subscriptions.Keys.Select(Key).Order(StringComparer.Ordinal).ToList(), unknown, _seq);
    }

    public void Pause() => _paused = true;
    public void Resume() => _paused = false;

    public EventsRaisedParams Get(EventsGetParams p)
    {
        var limit = Math.Clamp(p.Limit, 1, 5000);
        var records = _buffer
            .Where(r => r.Seq > p.Since)
            .Where(r => p.ElementId is not { } id || r.SourceId == id || r.Route.Any(s => s.Id == id))
            .Take(limit)
            .ToList();
        return new EventsRaisedParams(records);
    }

    // ---------- aggregation ----------

    sealed class Pending(DateTimeOffset time, bool ignored)
    {
        public readonly DateTimeOffset Time = time;
        public readonly bool Ignored = ignored;
        public readonly List<RouteStep> Steps = new();
        public bool LastHandled;
        public int HandlerIndex = -1;
    }

    void OnRaised(object sender, RoutedEventArgs e)
    {
        if (_paused || e.RoutedEvent is null) return;
        if (!_pending.TryGetValue(e, out var p))
        {
            p = new Pending(DateTimeOffset.Now, IsOwn(e.Source) || IsOwn(sender));
            _pending[e] = p;
            Dispatcher.UIThread.Post(() => Complete(e), DispatcherPriority.Send);
        }
        if (p.Ignored) return;
        // Raised runs after this node's handlers: Handled turning true since the previous node means this node handled it.
        var handledHere = e.Handled && !p.LastHandled && p.HandlerIndex < 0;
        p.LastHandled = e.Handled;
        if (handledHere) p.HandlerIndex = p.Steps.Count;
        p.Steps.Add(new RouteStep(_registry.IdOf(sender), sender.GetType().Name, (sender as StyledElement)?.Name, handledHere, Phase(e.Route)));
    }

    void Complete(RoutedEventArgs e)
    {
        if (!_pending.Remove(e, out var p) || p.Ignored || p.Steps.Count == 0) return;
        var source = e.Source;
        var sourceId = source is null ? p.Steps[0].Id : _registry.IdOf(source);
        var handler = p.HandlerIndex >= 0 ? p.Steps[p.HandlerIndex] : null;
        var record = new EventRecord(
            ++_seq, p.Time, Key(e.RoutedEvent!),
            sourceId, source?.GetType().Name ?? p.Steps[0].Type, (source as StyledElement)?.Name,
            handler?.Phase ?? p.Steps[^1].Phase,
            e.Handled, handler?.Id, p.Steps);

        _buffer.Enqueue(record);
        while (_buffer.Count > _bufferSize) _buffer.Dequeue();
        _outbox.Add(record);
        if (_outbox.Count > _bufferSize)
        {
            _dropped += _outbox.Count - _bufferSize;
            _outbox.RemoveRange(0, _outbox.Count - _bufferSize);
        }
        QueuePush();
    }

    void QueuePush()
    {
        if (_pushQueued) return;
        _pushQueued = true;
        DispatcherTimer.RunOnce(Flush, TimeSpan.FromMilliseconds(PushIntervalMs));
    }

    void Flush()
    {
        _pushQueued = false;
        if (_outbox.Count == 0) return;
        var batch = _outbox.Take(MaxPerPush).ToList();
        _outbox.RemoveRange(0, batch.Count);
        var dropped = _dropped;
        _dropped = 0;
        _push(new EventsRaisedParams(batch, dropped));
        if (_outbox.Count > 0) QueuePush();
    }

    static RouteStrategy Phase(RoutingStrategies r) =>
        r.HasFlag(RoutingStrategies.Tunnel) ? RouteStrategy.Tunnel
        : r.HasFlag(RoutingStrategies.Bubble) ? RouteStrategy.Bubble
        : RouteStrategy.Direct;

    /// <summary>Spyglass's own in-app visuals (highlight adorner, toast) are never traced.</summary>
    static bool IsOwn(object? o) =>
        o is not null && (o.GetType().Assembly == typeof(EventTracer).Assembly || AgentToast.Contains(o));

    public void Dispose()
    {
        foreach (var s in _subscriptions.Values) s.Dispose();
        _subscriptions.Clear();
        _pending.Clear();
        _outbox.Clear();
    }

    sealed class Observer(Action<(object, RoutedEventArgs)> onNext) : IObserver<(object, RoutedEventArgs)>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext((object, RoutedEventArgs) value) => onNext(value);
    }
}
