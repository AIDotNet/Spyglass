using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Agent.Internal;

/// <summary>
/// Incremental tree updates (design/protocol/01-methods §2 tree/changed). The client asks for subtrees with
/// tree/getChildren; this remembers, per parent, which children it was sent. Controls loading/unloading and IsVisible
/// or Name changes mark parents dirty; after a 60 ms debounce each dirty parent is diffed against what was sent and
/// only the difference (add / remove / update) is pushed. Nothing is observed until the client first fetches a tree.
/// UI-thread only.
/// </summary>
internal sealed class TreeSync : IDisposable
{
    readonly NodeRegistry _registry;
    readonly TreeWalker _walker;
    readonly Action<TreeChangedParams> _send;
    readonly Dictionary<(TreeKind, int), List<int>> _sent = new();
    readonly Dictionary<(TreeKind, int), int> _parentOf = new();
    readonly HashSet<(TreeKind, int)> _dirty = new();
    readonly HashSet<int> _updates = new();
    readonly List<IDisposable> _subscriptions = new();
    bool _flushQueued;
    bool _observing;

    public TreeSync(NodeRegistry registry, TreeWalker walker, Action<TreeChangedParams> send)
    {
        _registry = registry;
        _walker = walker;
        _send = send;
    }

    /// <summary>Call with every subtree returned to the client; starts observing on first use.</summary>
    public void Record(NodeDto node, TreeKind kind)
    {
        if (!_observing) StartObserving();
        Remember(node, kind, null);
    }

    void Remember(NodeDto node, TreeKind kind, int? parentId)
    {
        if (parentId is { } p) _parentOf[(kind, node.Id)] = p;
        if (node.Children is null) return;
        _sent[(kind, node.Id)] = node.Children.Select(c => c.Id).ToList();
        foreach (var c in node.Children) Remember(c, kind, node.Id);
    }

    void StartObserving()
    {
        _observing = true;
        _subscriptions.Add(Control.LoadedEvent.Raised.Subscribe(new Obs<(object, RoutedEventArgs)>(t => OnLoaded(t.Item1))));
        _subscriptions.Add(Control.UnloadedEvent.Raised.Subscribe(new Obs<(object, RoutedEventArgs)>(t => OnUnloaded(t.Item1))));
        _subscriptions.Add(Visual.IsVisibleProperty.Changed.Subscribe(new Obs<AvaloniaPropertyChangedEventArgs<bool>>(e => OnUpdated(e.Sender))));
        _subscriptions.Add(StyledElement.NameProperty.Changed.Subscribe(new Obs<AvaloniaPropertyChangedEventArgs<string?>>(e => OnUpdated(e.Sender))));
    }

    void OnLoaded(object control)
    {
        if (control is Visual v && v.GetVisualParent() is { } vp && _registry.TryGetId(vp, out var vpid)) MarkDirty(TreeKind.Visual, vpid);
        if (control is Avalonia.LogicalTree.ILogical l && l.LogicalParent is { } lp && _registry.TryGetId(lp, out var lpid)) MarkDirty(TreeKind.Logical, lpid);
    }

    void OnUnloaded(object control)
    {
        if (!_registry.TryGetId(control, out var id)) return;
        foreach (var kind in new[] { TreeKind.Visual, TreeKind.Logical })
            if (_parentOf.TryGetValue((kind, id), out var parent)) MarkDirty(kind, parent);
    }

    void OnUpdated(object? sender)
    {
        if (sender is null || !_registry.TryGetId(sender, out var id)) return;
        if (!_parentOf.ContainsKey((TreeKind.Visual, id)) && !_parentOf.ContainsKey((TreeKind.Logical, id))) return;
        _updates.Add(id);
        Queue();
    }

    void MarkDirty(TreeKind kind, int parentId)
    {
        if (!_sent.ContainsKey((kind, parentId))) return;   // the client never expanded this node
        _dirty.Add((kind, parentId));
        Queue();
    }

    void Queue()
    {
        if (_flushQueued) return;
        _flushQueued = true;
        DispatcherTimer.RunOnce(Flush, TimeSpan.FromMilliseconds(60));
    }

    void Flush()
    {
        _flushQueued = false;
        var byKind = new Dictionary<TreeKind, List<TreeOp>>();
        List<TreeOp> Ops(TreeKind k) => byKind.TryGetValue(k, out var l) ? l : byKind[k] = new();

        foreach (var (kind, parentId) in _dirty.ToList())
        {
            if (!_registry.TryGet<object>(parentId, out var parent)) { Forget(kind, parentId); continue; }
            var current = TreeWalker.Children(parent, kind).ToList();
            var currentIds = current.Select(c => _registry.IdOf(c)).ToList();
            var previous = _sent[(kind, parentId)];

            foreach (var gone in previous.Except(currentIds))
            {
                Ops(kind).Add(new TreeOpRemove(parentId, gone));
                ForgetSubtree(kind, gone);
            }
            for (var i = 0; i < current.Count; i++)
            {
                if (previous.Contains(currentIds[i])) continue;
                var node = _walker.Build(current[i], kind, -1);
                Ops(kind).Add(new TreeOpAdd(parentId, i, node));
                Remember(node, kind, parentId);
            }
            _sent[(kind, parentId)] = currentIds;
        }
        _dirty.Clear();

        // Updates (visibility, name) apply to either tree: send once, on the visual tree.
        foreach (var id in _updates)
        {
            if (!_registry.TryGet<object>(id, out var o)) continue;
            var d = _walker.Describe(o);
            Ops(TreeKind.Visual).Add(new TreeOpUpdate(id, d.Name, d.Classes, d.IsVisible));
        }
        _updates.Clear();

        foreach (var (kind, ops) in byKind)
            if (ops.Count > 0) _send(new TreeChangedParams(kind, ops));
    }

    void ForgetSubtree(TreeKind kind, int id)
    {
        if (_sent.TryGetValue((kind, id), out var kids))
            foreach (var k in kids) ForgetSubtree(kind, k);
        Forget(kind, id);
    }

    void Forget(TreeKind kind, int id)
    {
        _sent.Remove((kind, id));
        _parentOf.Remove((kind, id));
    }

    public void Dispose()
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
    }

    sealed class Obs<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => onNext(value);
    }
}
