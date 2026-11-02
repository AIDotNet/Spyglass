using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.App.Core;

// Hot reload (design/hot-reload/README.md §7.2). The hub-wide coordinator (file watching, routing) is HotReloadCoordinator.
public sealed partial class AgentSession
{
    /// <summary>The agent's XAML documents and whether hot reload is available; null until fetched (or no capability).</summary>
    public HotReloadManifest? HotReload { get; private set; }

    /// <summary>Raised from a background thread when the manifest is first fetched or the agent reports new documents.</summary>
    public event Action<HotReloadManifest>? HotReloadManifestChanged;

    /// <summary>Raised from a background thread after every reload in the app, whatever started it.</summary>
    public event Action<HotReloadResult>? HotReloadApplied;

    internal void RaiseHotReloadManifest(HotReloadManifest m)
    {
        HotReload = m;
        HotReloadManifestChanged?.Invoke(m);
    }

    internal void RaiseHotReloadApplied(HotReloadResult r) => HotReloadApplied?.Invoke(r);

    /// <summary>Raised from a background thread after a .NET Hot Reload (C#) delta was applied in the app.</summary>
    public event Action<HotReloadCodeUpdate>? HotReloadCodeUpdated;

    internal void RaiseHotReloadCodeUpdated(HotReloadCodeUpdate u) => HotReloadCodeUpdated?.Invoke(u);

    public async Task<HotReloadManifest> GetHotReloadManifestAsync(CancellationToken ct = default)
    {
        var m = await Call<Empty, HotReloadManifest>(Methods.HotReloadGetManifest, Empty.Instance, ct).ConfigureAwait(false);
        RaiseHotReloadManifest(m);
        return m;
    }

    public Task<HotReloadResult> ApplyXamlAsync(string uri, string xaml, long version = 0, CancellationToken ct = default) =>
        Call<HotReloadApplyParams, HotReloadResult>(Methods.HotReloadApply, new HotReloadApplyParams(uri, xaml, version), ct);
}
