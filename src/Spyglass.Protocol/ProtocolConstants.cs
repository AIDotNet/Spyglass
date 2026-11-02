namespace Spyglass.Protocol;

/// <summary>Protocol-level constants. See design/protocol/README.md.</summary>
public static class SpyglassProtocol
{
    /// <summary>"major.minor". Major mismatch is incompatible; minor mismatch degrades by capability.</summary>
    public const string Version = "0.1";

    /// <summary>Client listens here by default (the official Avalonia tool uses 29414).</summary>
    public const int DefaultPort = 29500;

    /// <summary>Port from the SPYGLASS_PORT environment variable, else <see cref="DefaultPort"/>. Used by agent and client.</summary>
    public static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable("SPYGLASS_PORT"), out var p) && p is > 0 and < 65536 ? p : DefaultPort;

    public const string AgentPath = "/agent";

    public static bool IsCompatible(string agentVersion) =>
        Major(agentVersion) is { } m && m == Major(Version);

    static int? Major(string v) =>
        int.TryParse(v.Split('.')[0], out var m) ? m : null;
}

/// <summary>JSON-RPC method names. Direction: C→A = client to agent, A→C = agent to client.</summary>
public static class Methods
{
    // session
    public const string SessionHello = "session/hello";            // A→C req
    public const string SessionGoodbye = "session/goodbye";        // A→C ntf
    /// <summary>The user pressed the "open DevTools" hotkey in the app: bring the client to the front.</summary>
    public const string SessionActivate = "session/activate";      // A→C ntf

    // tree
    public const string TreeGetRoots = "tree/getRoots";            // C→A req
    public const string TreeGetChildren = "tree/getChildren";      // C→A req
    public const string TreeChanged = "tree/changed";              // A→C ntf
    public const string TreeGetAncestors = "tree/getAncestors";    // C→A req
    public const string TreeSearch = "tree/search";                // C→A req

    // node
    public const string NodeDescribe = "node/describe";
    public const string NodeScreenshot = "node/screenshot";
    public const string NodeBringIntoView = "node/bringIntoView";

    // props
    public const string PropsGet = "props/get";
    public const string PropsSet = "props/set";
    public const string PropsReset = "props/reset";
    public const string PropsSubscribe = "props/subscribe";
    public const string PropsUnsubscribe = "props/unsubscribe";
    public const string PropsChanged = "props/changed";            // A→C ntf

    // layout
    public const string LayoutGet = "layout/get";
    public const string LayoutExplain = "layout/explain";

    // styles
    public const string StylesGetClasses = "styles/getClasses";
    public const string StylesSetClasses = "styles/setClasses";
    public const string StylesSetPseudoClass = "styles/setPseudoClass";
    public const string StylesGetFrames = "styles/getFrames";      // Advanced

    // pick / highlight
    public const string PickStart = "pick/start";
    public const string PickStop = "pick/stop";
    public const string PickHover = "pick/hover";                  // A→C ntf
    /// <summary>Picking was started from inside the app (inspect hotkey), not by the client.</summary>
    public const string PickStarted = "pick/started";              // A→C ntf
    public const string PickSelected = "pick/selected";            // A→C ntf
    public const string HighlightShow = "highlight/show";
    public const string HighlightHide = "highlight/hide";

    // events (v0.2)
    public const string EventsListRouted = "events/listRouted";
    public const string EventsConfigure = "events/configure";
    public const string EventsPause = "events/pause";
    public const string EventsResume = "events/resume";
    /// <summary>Buffered records since a cursor (used by MCP `events_trace read`).</summary>
    public const string EventsGet = "events/get";
    public const string EventsRaised = "events/raised";            // A→C ntf

    // bindings (v0.2)
    public const string BindingsGetErrors = "bindings/getErrors";
    public const string BindingsError = "bindings/error";          // A→C ntf
    public const string BindingsGetDetails = "bindings/getDetails";// Advanced

    // logs (v0.2)
    public const string LogsConfigure = "logs/configure";
    public const string LogsGet = "logs/get";
    public const string LogsEntries = "logs/entries";              // A→C ntf

    // source (v0.2)
    public const string SourceGet = "source/get";

    // network (v0.2, design/network/README.md §6) — capture runs only between enable and disable
    public const string NetworkEnable = "network/enable";
    public const string NetworkDisable = "network/disable";
    public const string NetworkRequestStarted = "network/requestStarted";   // A→C ntf
    public const string NetworkResponseReceived = "network/responseReceived"; // A→C ntf
    public const string NetworkFinished = "network/finished";               // A→C ntf
    public const string NetworkFailed = "network/failed";                   // A→C ntf
    public const string NetworkGetBody = "network/getBody";
    public const string NetworkClear = "network/clear";
    public const string NetworkSearch = "network/search";
    public const string NetworkStatus = "network/status";

    // resources (v0.3)
    public const string ResourcesGetProviders = "resources/getProviders";
    public const string ResourcesGetEntries = "resources/getEntries";
    public const string ResourcesLookup = "resources/lookup";
    public const string ResourcesFind = "resources/find";
    public const string ResourcesSet = "resources/set";
    public const string ResourcesReset = "resources/reset";
    // input (v0.3, design/protocol/01-methods.md §13)
    public const string InputAct = "input/act";
    public const string WaitUntil = "wait/until";
    public const string ChangesReset = "changes/reset";

    // metrics / overlays (v0.3)
    public const string MetricsConfigure = "metrics/configure";
    public const string MetricsSample = "metrics/sample";          // A→C ntf
    public const string OverlaysSet = "overlays/set";
    public const string OverlaysGet = "overlays/get";
}

/// <summary>Capabilities an agent declares in <see cref="Methods.SessionHello"/>.</summary>
public static class Capabilities
{
    // Basic agent (public Avalonia APIs only)
    public const string Tree = "tree";
    public const string Props = "props";
    public const string Pick = "pick";
    public const string Highlight = "highlight";
    public const string Screenshot = "screenshot";
    public const string PseudoClasses = "pseudoClasses";
    public const string Events = "events";
    public const string BindingErrors = "bindingErrors";
    /// <summary>Avalonia log capture: logs/configure, logs/get, logs/entries.</summary>
    public const string Logs = "logs";
    public const string XamlSource = "xamlSource";
    public const string Metrics = "metrics";
    public const string Activities = "activities";
    public const string Overlays = "overlays";
    public const string Network = "network";
    /// <summary>Resource dictionary browsing and live editing: resources/*.</summary>
    public const string Resources = "resources";
    /// <summary>input/act, wait/until, changes/reset.</summary>
    public const string Input = "input";

    // Advanced agent ([PrivateApi], exact Avalonia version)
    public const string ValueFrames = "valueFrames";
    public const string BindingDetails = "bindingDetails";
}

/// <summary>JSON-RPC error codes. -32700…-32600 are the JSON-RPC standard codes.</summary>
public static class ErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    public const int NodeNotFound = -32001;
    public const int NodeDetached = -32002;
    public const int PropertyReadOnly = -32003;
    public const int ConversionFailed = -32004;
    public const int CapabilityMissing = -32005;
    public const int SetValueFailed = -32006;
    /// <summary>The element cannot perform the requested input action (disabled, hidden, unsupported control…).</summary>
    public const int ActionFailed = -32007;
    public const int VersionMismatch = -32010;
    public const int Unauthorized = -32011;
    public const int BodyEvicted = -32020;
}
