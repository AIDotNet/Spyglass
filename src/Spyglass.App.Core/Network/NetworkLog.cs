using System.Collections.Concurrent;
using Spyglass.Protocol.Messages;

namespace Spyglass.App.Core.Network;

/// <summary>
/// Client-side store of captured requests for one session (upsert by id from pushed snapshots).
/// Shared by the Network panel and the MCP tools. Thread-safe; <see cref="Changed"/> fires on a background thread.
/// </summary>
public sealed class NetworkLog
{
    readonly ConcurrentDictionary<long, NetworkRequestDto> _requests = new();

    public event Action<IReadOnlyList<NetworkRequestDto>>? Changed;
    public event Action? Cleared;

    public IReadOnlyList<NetworkRequestDto> Requests => _requests.Values.OrderBy(r => r.Id).ToList();
    public int Count => _requests.Count;

    public NetworkRequestDto? Get(long id) => _requests.TryGetValue(id, out var r) ? r : null;

    internal void Apply(IReadOnlyList<NetworkRequestDto> updates)
    {
        foreach (var u in updates) _requests[u.Id] = u;
        Changed?.Invoke(updates);
    }

    public void Clear()
    {
        _requests.Clear();
        Cleared?.Invoke();
    }
}
