using Avalonia.Input;
using Spyglass.Protocol;

namespace Spyglass.Agent;

public sealed class SpyglassAgentOptions
{
    /// <summary>Host where the Spyglass client listens. Default: SPYGLASS_HOST, else loopback. A LAN host also needs <see cref="PairingCode"/> (design/protocol §6).</summary>
    public string Host { get; set; } = Environment.GetEnvironmentVariable("SPYGLASS_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

    /// <summary>Default: SPYGLASS_PORT, else 29500.</summary>
    public int Port { get; set; } = SpyglassProtocol.Port;

    /// <summary>Required when the client accepts LAN connections. Falls back to SPYGLASS_PAIRING_CODE.</summary>
    public string? PairingCode { get; set; } = Environment.GetEnvironmentVariable("SPYGLASS_PAIRING_CODE");

    /// <summary>Name shown in the client; defaults to the entry assembly name.</summary>
    public string? ApplicationName { get; set; }

    /// <summary>Enables Avalonia's built-in meters/activities (AppContext switch) for the Performance page.</summary>
    public bool EnableAvaloniaDiagnostics { get; set; } = true;

    /// <summary>
    /// Opens Spyglass (brings it to the front; starts it if it is not running). Same as Chrome DevTools:
    /// Ctrl+Shift+I, or Cmd+Option+I on macOS. Null disables. Handled before the app's own key handlers.
    /// </summary>
    public KeyGesture? OpenGesture { get; set; } = OperatingSystem.IsMacOS()
        ? new KeyGesture(Key.I, KeyModifiers.Meta | KeyModifiers.Alt)
        : new KeyGesture(Key.I, KeyModifiers.Control | KeyModifiers.Shift);

    /// <summary>
    /// Starts element picking in the app, like Chrome's "inspect element": Ctrl+Shift+C, or Cmd+Shift+C on macOS.
    /// Null disables.
    /// </summary>
    public KeyGesture? InspectGesture { get; set; } = OperatingSystem.IsMacOS()
        ? new KeyGesture(Key.C, KeyModifiers.Meta | KeyModifiers.Shift)
        : new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift);

    /// <summary>Start the Spyglass client when a hotkey is pressed and it is not running (loopback host only).</summary>
    public bool LaunchClient { get; set; } = true;

    /// <summary>
    /// Client to start: an executable, a .dll (run with `dotnet`), or a macOS .app bundle. When null, Spyglass
    /// looks at SPYGLASS_CLIENT, then `spyglass` on PATH, then the platform's usual install location.
    /// </summary>
    public string? ClientPath { get; set; }

    internal List<IAgentFeature> Features { get; } = [];

    public SpyglassAgentOptions AddFeature(IAgentFeature feature)
    {
        Features.Add(feature);
        return this;
    }
}

/// <summary>Optional feature pack (e.g. Spyglass.Agent.Advanced) that adds capabilities and handlers.</summary>
public interface IAgentFeature
{
    IReadOnlyList<string> Capabilities { get; }

    /// <summary>True for features built on Avalonia private APIs (reported as the Advanced agent flavor).</summary>
    bool IsAdvanced => false;
}

/// <summary>A feature that registers request handlers on every new connection (internal: Spyglass packages only).</summary>
internal interface IConnectionFeature
{
    void Register(Internal.AgentConnection connection);
}
