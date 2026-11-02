using Avalonia.Threading;
using Spyglass.Agent.Internal;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Agent.HotReload;

/// <summary>Registers hotReload/* on every connection (design/hot-reload/README.md §7.2).</summary>
internal sealed class HotReloadFeature : IAgentFeature, IConnectionFeature
{
    public IReadOnlyList<string> Capabilities { get; } = [Protocol.Capabilities.HotReload];

    readonly XamlReloader _reloader = XamlReloader.Shared;
    readonly List<WeakReference<JsonRpcPeer>> _peers = [];

    public HotReloadFeature()
    {
        _reloader.Applied += r => Broadcast(Methods.HotReloadApplied, r);
        SpyglassHotReload.DocumentsChanged += () => Broadcast(Methods.HotReloadManifest, SpyglassHotReload.Manifest());
        SpyglassMetadataUpdateHandler.CodeUpdated += u => Broadcast(Methods.HotReloadCodeUpdated, u);
    }

    public void Register(AgentConnection connection)
    {
        lock (_peers)
        {
            _peers.RemoveAll(w => !w.TryGetTarget(out var p) || !p.IsOpen);
            _peers.Add(new WeakReference<JsonRpcPeer>(connection.Peer));
        }
        connection.Peer.Handle<Empty, HotReloadManifest>(Methods.HotReloadGetManifest, (_, _) =>
            Task.FromResult(SpyglassHotReload.Manifest()));
        connection.Peer.Handle<HotReloadApplyParams, HotReloadResult>(Methods.HotReloadApply, (p, _) =>
            Dispatcher.UIThread.InvokeAsync(() => _reloader.ApplyAsync(p.Uri, p.Xaml, HotReloadSource.Direct, p.Version)));
    }

    void Broadcast<T>(string method, T payload)
    {
        List<JsonRpcPeer> peers;
        lock (_peers) peers = _peers.Select(w => w.TryGetTarget(out var p) ? p : null).OfType<JsonRpcPeer>().Where(p => p.IsOpen).ToList();
        foreach (var p in peers) _ = p.NotifyAsync(method, payload);
    }
}
