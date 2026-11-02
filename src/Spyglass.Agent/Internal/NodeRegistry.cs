using System.Runtime.CompilerServices;
using Spyglass.Protocol;

namespace Spyglass.Agent.Internal;

/// <summary>
/// Session-scoped ids for live objects (design/protocol §5.1). Weak on both sides so it never keeps the app's
/// visuals alive. Ids are never reused within a session. UI-thread only.
/// </summary>
internal sealed class NodeRegistry
{
    sealed class Box(int id) { public readonly int Id = id; }

    readonly ConditionalWeakTable<object, Box> _ids = new();
    readonly Dictionary<int, WeakReference<object>> _objects = new();
    int _next;

    public int IdOf(object o) =>
        _ids.GetValue(o, key =>
        {
            var id = ++_next;
            _objects[id] = new WeakReference<object>(key);
            if (_objects.Count % 4096 == 0) Purge();
            return new Box(id);
        }).Id;

    /// <summary>Id of an object only if one was already assigned (does not allocate).</summary>
    public bool TryGetId(object o, out int id)
    {
        if (_ids.TryGetValue(o, out var box)) { id = box.Id; return true; }
        id = 0;
        return false;
    }

    public bool TryGet<T>(int id, out T value) where T : class
    {
        if (_objects.TryGetValue(id, out var w) && w.TryGetTarget(out var o) && o is T t)
        {
            value = t;
            return true;
        }
        value = null!;
        return false;
    }

    public T Get<T>(int id) where T : class =>
        TryGet<T>(id, out var v)
            ? v
            : throw new SpyglassProtocolException(ErrorCodes.NodeNotFound,
                $"Node {id} not found. It may have been removed from the tree; fetch the tree again.");

    void Purge()
    {
        foreach (var dead in _objects.Where(kv => !kv.Value.TryGetTarget(out _)).Select(kv => kv.Key).ToList())
            _objects.Remove(dead);
    }
}
