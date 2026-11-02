using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.App.Core;

/// <summary>One connected agent (one debugged process) with a typed request API (design/protocol/01-methods).</summary>
public sealed partial class AgentSession
{
    readonly JsonRpcPeer _peer;
    static int _nextShortId;

    internal AgentSession(string sessionId, HelloParams hello, JsonRpcPeer peer)
    {
        SessionId = sessionId;
        ShortId = "a" + Interlocked.Increment(ref _nextShortId);
        App = hello.App;
        Agent = hello.Agent;
        Capabilities = hello.Capabilities.ToHashSet();
        _peer = peer;
    }

    public string SessionId { get; }
    /// <summary>Short, human-friendly id used by MCP tools ("a1").</summary>
    public string ShortId { get; }
    public AppInfo App { get; }
    public AgentInfo Agent { get; }
    public IReadOnlySet<string> Capabilities { get; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.Now;
    public bool IsConnected => _peer.IsOpen;

    public bool Has(string capability) => Capabilities.Contains(capability);

    /// <summary>Raised from a background thread.</summary>
    public event Action<int>? PickHover;
    /// <summary>Raised from a background thread. Id is null when picking was cancelled.</summary>
    public event Action<PickSelectedParams>? PickSelected;

    /// <summary>Raised from a background thread (windows/popups opened or closed, window activated…).</summary>
    public event Action<TreeChangedParams>? TreeChanged;

    /// <summary>Raised from a background thread: the user pressed the open hotkey in the app, or picked an element there.</summary>
    public event Action<ActivateParams>? ActivateRequested;
    /// <summary>Raised from a background thread: picking was started from inside the app (inspect hotkey).</summary>
    public event Action? PickStarted;

    /// <summary>Raised from a background thread: new or repeated binding errors (pushed by the agent, 250 ms batches).</summary>
    public event Action<IReadOnlyList<BindingErrorDto>>? BindingErrors;

    /// <summary>Raised from a background thread: new Avalonia log entries recorded since the session started (250 ms batches).</summary>
    public event Action<LogEntriesParams>? LogEntries;

    internal void RaiseLogEntries(LogEntriesParams p) => LogEntries?.Invoke(p);
    /// <summary>Raised from a background thread: traced routed events (100 ms batches) while tracing is configured.</summary>
    public event Action<EventsRaisedParams>? EventsRaised;

    internal void RaiseEventsRaised(EventsRaisedParams p) => EventsRaised?.Invoke(p);
    internal void RaiseBindingErrors(IReadOnlyList<BindingErrorDto> e) => BindingErrors?.Invoke(e);
    internal void RaiseActivate(ActivateParams p) => ActivateRequested?.Invoke(p);
    internal void RaisePickStarted() => PickStarted?.Invoke();
    internal void RaiseTreeChanged(TreeChangedParams p) => TreeChanged?.Invoke(p);
    internal void RaisePickHover(int id) => PickHover?.Invoke(id);
    internal void RaisePickSelected(PickSelectedParams p) => PickSelected?.Invoke(p);

    Task<TR> Call<TP, TR>(string method, TP p, CancellationToken ct) => _peer.RequestAsync<TP, TR>(method, p, ct);

    // ---- tree / node ----
    public Task<GetRootsResult> GetRootsAsync(CancellationToken ct = default) => Call<Empty, GetRootsResult>(Methods.TreeGetRoots, Empty.Instance, ct);
    public async Task<NodeDto> GetTreeAsync(int id, TreeKind tree = TreeKind.Visual, int depth = -1, CancellationToken ct = default) =>
        (await Call<GetChildrenParams, GetChildrenResult>(Methods.TreeGetChildren, new GetChildrenParams(id, tree, depth), ct)).Node;
    public async Task<IReadOnlyList<NodeRef>> GetAncestorsAsync(int id, TreeKind tree = TreeKind.Visual, CancellationToken ct = default) =>
        (await Call<GetAncestorsParams, GetAncestorsResult>(Methods.TreeGetAncestors, new GetAncestorsParams(id, tree), ct)).Path;
    public Task<SearchResult> SearchAsync(SearchParams p, CancellationToken ct = default) => Call<SearchParams, SearchResult>(Methods.TreeSearch, p, ct);
    public Task<DescribeResult> DescribeAsync(int id, CancellationToken ct = default) => Call<NodeParams, DescribeResult>(Methods.NodeDescribe, new NodeParams(id), ct);
    public Task<ScreenshotResult> ScreenshotAsync(int id, double scale = 1, CancellationToken ct = default) =>
        Call<ScreenshotParams, ScreenshotResult>(Methods.NodeScreenshot, new ScreenshotParams(id, scale), ct);
    public Task BringIntoViewAsync(int id, CancellationToken ct = default) => Call<NodeParams, Empty>(Methods.NodeBringIntoView, new NodeParams(id), ct);

    // ---- props / layout / styles ----
    public async Task<IReadOnlyList<PropertyDto>> GetPropertiesAsync(int id, CancellationToken ct = default) =>
        (await Call<PropsGetParams, PropsGetResult>(Methods.PropsGet, new PropsGetParams(Id: id), ct)).Properties;
    public async Task<IReadOnlyList<PropertyDto>> GetObjectPropertiesAsync(int refId, CancellationToken ct = default) =>
        (await Call<PropsGetParams, PropsGetResult>(Methods.PropsGet, new PropsGetParams(Ref: refId), ct)).Properties;
    public async Task<PropertyDto> SetPropertyAsync(int id, string name, string owner, ValueDto value, CancellationToken ct = default) =>
        (await Call<PropsSetParams, PropsSetResult>(Methods.PropsSet, new PropsSetParams(id, name, owner, value), ct)).Property;
    public Task ResetPropertiesAsync(PropsResetParams p, CancellationToken ct = default) => Call<PropsResetParams, Empty>(Methods.PropsReset, p, ct);
    public Task<LayoutExplainResult> ExplainLayoutAsync(int id, CancellationToken ct = default) =>
        Call<NodeParams, LayoutExplainResult>(Methods.LayoutExplain, new NodeParams(id), ct);
    public Task<LayoutResult> GetLayoutAsync(int id, CancellationToken ct = default) => Call<NodeParams, LayoutResult>(Methods.LayoutGet, new NodeParams(id), ct);
    public Task<ClassesResult> GetClassesAsync(int id, CancellationToken ct = default) => Call<NodeParams, ClassesResult>(Methods.StylesGetClasses, new NodeParams(id), ct);
    public Task SetClassesAsync(int id, IReadOnlyList<string> classes, CancellationToken ct = default) =>
        Call<SetClassesParams, Empty>(Methods.StylesSetClasses, new SetClassesParams(id, classes), ct);
    public Task SetPseudoClassAsync(int id, string pseudoClass, bool active, CancellationToken ct = default) =>
        Call<SetPseudoClassParams, Empty>(Methods.StylesSetPseudoClass, new SetPseudoClassParams(id, pseudoClass, active), ct);

    // ---- pick / highlight ----
    public Task StartPickAsync(CancellationToken ct = default) => Call<PickStartParams, Empty>(Methods.PickStart, new PickStartParams(), ct);
    public Task StopPickAsync(CancellationToken ct = default) => Call<Empty, Empty>(Methods.PickStop, Empty.Instance, ct);
    public Task HighlightAsync(int id, HighlightStyle style = HighlightStyle.Box, int durationMs = 1500, CancellationToken ct = default) =>
        Call<HighlightShowParams, Empty>(Methods.HighlightShow, new HighlightShowParams(id, style, durationMs), ct);
    public Task HideHighlightAsync(CancellationToken ct = default) => Call<Empty, Empty>(Methods.HighlightHide, Empty.Instance, ct);

    // ---- diagnostics ----
    public async Task<IReadOnlyList<BindingErrorDto>> GetBindingErrorsAsync(int? targetId = null, long since = 0, CancellationToken ct = default) =>
        (await Call<GetBindingErrorsParams, BindingErrorsParams>(Methods.BindingsGetErrors, new GetBindingErrorsParams(targetId, since), ct)).Errors;
    /// <summary>logs/get: buffered Avalonia log entries after <paramref name="since"/> (newest <paramref name="limit"/>, oldest first).</summary>
    public Task<LogsGetResult> GetLogsAsync(long since = 0, string? minLevel = null, string? area = null, int limit = 500, CancellationToken ct = default) =>
        Call<LogsGetParams, LogsGetResult>(Methods.LogsGet, new LogsGetParams(since, minLevel, area, limit), ct);
    /// <summary>logs/configure: what the agent records from now on (global to the app, not per session).</summary>
    public Task ConfigureLogsAsync(string minLevel, IReadOnlyList<string>? areas = null, CancellationToken ct = default) =>
        Call<LogsConfigureParams, Empty>(Methods.LogsConfigure, new LogsConfigureParams(minLevel, areas), ct);
    public Task<FramesResult> GetValueFramesAsync(int id, CancellationToken ct = default) =>
        Call<NodeParams, FramesResult>(Methods.StylesGetFrames, new NodeParams(id), ct);
    public Task<BindingDetailsResult> GetBindingDetailsAsync(int id, string property, CancellationToken ct = default) =>
        Call<BindingDetailsParams, BindingDetailsResult>(Methods.BindingsGetDetails, new BindingDetailsParams(id, property), ct);
    // ---- network (design/network) ----
    public Task<NetworkStatusResult> EnableNetworkAsync(NetworkEnableParams? p = null, CancellationToken ct = default) =>
        Call<NetworkEnableParams, NetworkStatusResult>(Methods.NetworkEnable, p ?? new NetworkEnableParams(), ct);
    public Task<NetworkStatusResult> DisableNetworkAsync(CancellationToken ct = default) => Call<Empty, NetworkStatusResult>(Methods.NetworkDisable, Empty.Instance, ct);
    public Task<NetworkStatusResult> GetNetworkStatusAsync(CancellationToken ct = default) => Call<Empty, NetworkStatusResult>(Methods.NetworkStatus, Empty.Instance, ct);
    public Task ClearNetworkAsync(CancellationToken ct = default) => Call<Empty, NetworkRequestsParams>(Methods.NetworkClear, Empty.Instance, ct);
    public Task<NetworkBodyResult> GetNetworkBodyAsync(long id, string part = "response", CancellationToken ct = default) =>
        Call<NetworkGetBodyParams, NetworkBodyResult>(Methods.NetworkGetBody, new NetworkGetBodyParams(id, part), ct);
    public Task<NetworkSearchResult> SearchNetworkAsync(NetworkSearchParams p, CancellationToken ct = default) =>
        Call<NetworkSearchParams, NetworkSearchResult>(Methods.NetworkSearch, p, ct);

    /// <summary>Raised from a background thread: request snapshots (started / response / finished / failed).</summary>
    public event Action<IReadOnlyList<NetworkRequestDto>>? NetworkUpdated;
    internal void RaiseNetwork(IReadOnlyList<NetworkRequestDto> r)
    {
        Network.Apply(r);
        NetworkUpdated?.Invoke(r);
    }

    /// <summary>Requests captured in this session (shared by the Network panel and MCP).</summary>
    public Network.NetworkLog Network { get; } = new();
    // ---- events (design/protocol/01-methods §8) ----
    public async Task<IReadOnlyList<RoutedEventInfo>> ListRoutedEventsAsync(CancellationToken ct = default) =>
        (await Call<Empty, ListRoutedResult>(Methods.EventsListRouted, Empty.Instance, ct)).Events;
    /// <summary>Starts (or changes) tracing. <paramref name="enabled"/> null = the default set; empty = stop tracing.</summary>
    public Task<EventsConfigureResult> ConfigureEventsAsync(IReadOnlyList<string>? enabled, int bufferSize = 5000, CancellationToken ct = default) =>
        Call<EventsConfigureParams, EventsConfigureResult>(Methods.EventsConfigure, new EventsConfigureParams(enabled, bufferSize), ct);
    public Task StopEventsAsync(CancellationToken ct = default) => ConfigureEventsAsync([], ct: ct);
    public Task PauseEventsAsync(CancellationToken ct = default) => Call<Empty, Empty>(Methods.EventsPause, Empty.Instance, ct);
    public Task ResumeEventsAsync(CancellationToken ct = default) => Call<Empty, Empty>(Methods.EventsResume, Empty.Instance, ct);
    public async Task<IReadOnlyList<EventRecord>> GetEventsAsync(long since = 0, int limit = 500, int? elementId = null, CancellationToken ct = default) =>
        (await Call<EventsGetParams, EventsRaisedParams>(Methods.EventsGet, new EventsGetParams(since, limit, elementId), ct)).Records;

    // ---- resources (design/protocol/01-methods §13) ----
    public Task<ResourcesGetProvidersResult> GetResourceProvidersAsync(int? elementId = null, CancellationToken ct = default) =>
        Call<ResourcesGetProvidersParams, ResourcesGetProvidersResult>(Methods.ResourcesGetProviders, new ResourcesGetProvidersParams(elementId), ct);
    public Task<ResourcesGetEntriesResult> GetResourceEntriesAsync(int providerId, string? themeVariant = null, string? query = null, int limit = 3000, CancellationToken ct = default) =>
        Call<ResourcesGetEntriesParams, ResourcesGetEntriesResult>(Methods.ResourcesGetEntries, new ResourcesGetEntriesParams(providerId, themeVariant, query, limit), ct);
    public Task<ResourcesLookupResult> LookupResourceAsync(string key, int? elementId = null, string? themeVariant = null, CancellationToken ct = default) =>
        Call<ResourcesLookupParams, ResourcesLookupResult>(Methods.ResourcesLookup, new ResourcesLookupParams(key, elementId, themeVariant), ct);
    public Task<ResourcesFindResult> FindResourcesAsync(string query, int? elementId = null, string? themeVariant = null, int limit = 10, CancellationToken ct = default) =>
        Call<ResourcesFindParams, ResourcesFindResult>(Methods.ResourcesFind, new ResourcesFindParams(query, elementId, themeVariant, limit), ct);
    public async Task<ResourceEntryDto> SetResourceAsync(int providerId, string key, ValueDto value, CancellationToken ct = default) =>
        (await Call<ResourcesSetParams, ResourcesSetResult>(Methods.ResourcesSet, new ResourcesSetParams(providerId, key, value), ct)).Entry;
    public Task ResetResourcesAsync(ResourcesResetParams p, CancellationToken ct = default) => Call<ResourcesResetParams, Empty>(Methods.ResourcesReset, p, ct);

    public async Task<SourceInfo?> GetSourceAsync(int id, CancellationToken ct = default) =>
        (await Call<NodeParams, SourceResult>(Methods.SourceGet, new NodeParams(id), ct)).Element;

    // ---- input (design/protocol/01-methods §13) ----
    public Task<InputActResult> ActAsync(InputActParams p, CancellationToken ct = default) => Call<InputActParams, InputActResult>(Methods.InputAct, p, ct);
    public Task<WaitUntilResult> WaitUntilAsync(WaitUntilParams p, CancellationToken ct = default) => Call<WaitUntilParams, WaitUntilResult>(Methods.WaitUntil, p, ct);
    public Task<ChangesResetResult> ResetChangesAsync(int? id = null, CancellationToken ct = default) =>
        Call<ChangesResetParams, ChangesResetResult>(Methods.ChangesReset, new ChangesResetParams(id), ct);
}
