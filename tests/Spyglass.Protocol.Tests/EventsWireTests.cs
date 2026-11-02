using System.Text.Json;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;
using Xunit;

namespace Spyglass.Protocol.Tests;

/// <summary>Pins the events wire format to design/protocol/01-methods.md §8.</summary>
public class EventsWireTests
{
    static JsonElement Wire(JsonRpcMessage m) => JsonDocument.Parse(SpyglassJson.Serialize(m)).RootElement;

    [Fact]
    public void Raised_records_use_camelCase_and_string_phases()
    {
        var record = new EventRecord(7, DateTimeOffset.UnixEpoch, "InputElement.PointerPressed", 41, "ContentPresenter", null,
            RouteStrategy.Bubble, true, 42,
            [new RouteStep(1, "TopLevelHost", null, false, RouteStrategy.Tunnel), new RouteStep(42, "Button", "Save", true)]);
        var p = Wire(SpyglassJson.Notification(Methods.EventsRaised, new EventsRaisedParams([record], 3))).GetProperty("params");

        Assert.Equal(3, p.GetProperty("dropped").GetInt32());
        var r = p.GetProperty("records")[0];
        Assert.Equal("InputElement.PointerPressed", r.GetProperty("event").GetString());
        Assert.Equal("bubble", r.GetProperty("strategy").GetString());
        Assert.Equal(42, r.GetProperty("handledById").GetInt32());
        Assert.False(r.TryGetProperty("sourceName", out _));
        Assert.Equal("tunnel", r.GetProperty("route")[0].GetProperty("phase").GetString());
        Assert.Equal("Save", r.GetProperty("route")[1].GetProperty("name").GetString());

        var back = SpyglassJson.Read<EventsRaisedParams>(p);
        Assert.Equal(record.Route, back.Records[0].Route);
    }

    [Fact]
    public void Configure_without_enabled_means_defaults()
    {
        var p = Wire(SpyglassJson.Request(1, Methods.EventsConfigure, new EventsConfigureParams())).GetProperty("params");
        Assert.False(p.TryGetProperty("enabled", out _));
        Assert.Null(SpyglassJson.Read<EventsConfigureParams>(p).Enabled);
        Assert.Empty(SpyglassJson.Read<EventsConfigureParams>(JsonDocument.Parse("""{"enabled":[]}""").RootElement).Enabled!);
    }
}
