using System.Text.Json.Serialization;

namespace Spyglass.Protocol.Messages;

// ---- session/hello (A→C req) ----

public sealed record HelloParams(
    string ProtocolVersion,
    AgentInfo Agent,
    AppInfo App,
    IReadOnlyList<string> Capabilities,
    string? PairingCode = null);

public sealed record AgentInfo(string Name, string Version, AgentFlavor Flavor);

public enum AgentFlavor
{
    [JsonStringEnumMemberName("basic")] Basic,
    [JsonStringEnumMemberName("advanced")] Advanced,
}

public sealed record AppInfo(
    string Name,
    int Pid,
    /// <summary>Executable path + process start time; identifies "the same app instance" across reconnects.</summary>
    string InstanceKey,
    string AvaloniaVersion,
    string Runtime,
    string Os,
    string Arch,
    AppLifetimeKind Lifetime,
    bool IsDebugBuild);

public enum AppLifetimeKind
{
    [JsonStringEnumMemberName("classicDesktop")] ClassicDesktop,
    [JsonStringEnumMemberName("singleView")] SingleView,
    [JsonStringEnumMemberName("other")] Other,
}

public sealed record HelloResult(string SessionId, string ProtocolVersion);

// ---- session/activate (A→C ntf) ----

/// <param name="Reason">"hotkey" (open-DevTools gesture) or "pick" (an element was picked in the app).</param>
/// <param name="NodeId">Element to select, if any.</param>
public sealed record ActivateParams(string Reason, int? NodeId = null);

// ---- session/goodbye (A→C ntf) ----

public sealed record GoodbyeParams(string Reason);
