using Avalonia;
using Spyglass.Agent.Internal;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Agent.Advanced;

public static class SpyglassAdvancedExtensions
{
    /// <summary>Attaches the agent with Advanced features (value frames, binding details). Pinned to Avalonia 12.1.4 (D2).</summary>
    public static AppBuilder UseSpyglassAdvanced(this AppBuilder builder, Action<SpyglassAgentOptions>? configure = null) =>
        builder.UseSpyglass(o =>
        {
            o.AddFeature(new AdvancedFeature());
            configure?.Invoke(o);
        });
}

internal sealed class AdvancedFeature : IAgentFeature, IConnectionFeature
{
    public IReadOnlyList<string> Capabilities { get; } = [Protocol.Capabilities.ValueFrames, Protocol.Capabilities.BindingDetails];
    public bool IsAdvanced => true;

    public void Register(AgentConnection connection)
    {
        var inspector = new ValueFrameInspector(connection.Registry, connection.Codec);
        connection.HandleOnUi<NodeParams, FramesResult>(Methods.StylesGetFrames, p => inspector.Frames(p.Id));
        connection.HandleOnUi<BindingDetailsParams, BindingDetailsResult>(Methods.BindingsGetDetails, p => inspector.Binding(p.Id, p.Property));
    }
}
