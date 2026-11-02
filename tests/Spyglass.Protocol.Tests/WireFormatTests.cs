using System.Text;
using System.Text.Json;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;
using Xunit;

namespace Spyglass.Protocol.Tests;

/// <summary>Pins the wire format to design/protocol/01-methods.md.</summary>
public class WireFormatTests
{
    static JsonElement Wire(JsonRpcMessage m) => JsonDocument.Parse(SpyglassJson.Serialize(m)).RootElement;

    [Fact]
    public void Hello_request_uses_camelCase_and_string_enums()
    {
        var hello = new HelloParams(
            SpyglassProtocol.Version,
            new AgentInfo("Spyglass.Agent", "0.1.0", AgentFlavor.Advanced),
            new AppInfo("MyApp", 12345, "/app|t0", "12.1.4", ".NET 10.0.12", "macOS", "arm64", AppLifetimeKind.ClassicDesktop, true),
            [Capabilities.Tree, Capabilities.ValueFrames]);

        var json = Wire(SpyglassJson.Request(1, Methods.SessionHello, hello));

        Assert.Equal("2.0", json.GetProperty("jsonrpc").GetString());
        Assert.Equal("session/hello", json.GetProperty("method").GetString());
        var p = json.GetProperty("params");
        Assert.Equal("advanced", p.GetProperty("agent").GetProperty("flavor").GetString());
        Assert.Equal("classicDesktop", p.GetProperty("app").GetProperty("lifetime").GetString());
        Assert.True(p.GetProperty("app").GetProperty("isDebugBuild").GetBoolean());
        Assert.False(p.TryGetProperty("pairingCode", out _)); // nulls are omitted
    }

    [Fact]
    public void Tree_ops_are_polymorphic_on_op()
    {
        var changed = new TreeChangedParams(TreeKind.Visual,
        [
            new TreeOpAdd(12, 3, new NodeDto { Id = 42, Type = "Button", Name = "SaveButton" }),
            new TreeOpRemove(12, 57),
        ]);

        var msg = SpyglassJson.Notification(Methods.TreeChanged, changed);
        var ops = Wire(msg).GetProperty("params").GetProperty("ops");
        Assert.Equal("add", ops[0].GetProperty("op").GetString());
        Assert.Equal("remove", ops[1].GetProperty("op").GetString());
        Assert.Equal("visual", Wire(msg).GetProperty("params").GetProperty("tree").GetString());

        var roundTrip = SpyglassJson.Read<TreeChangedParams>(SpyglassJson.Deserialize(SpyglassJson.Serialize(msg)).Params);
        Assert.IsType<TreeOpAdd>(roundTrip.Ops[0]);
        Assert.Equal(57, Assert.IsType<TreeOpRemove>(roundTrip.Ops[1]).Id);
    }

    [Fact]
    public void Value_with_structured_data_round_trips()
    {
        var value = new ValueDto(ValueKind.Color, "#FF0562EF", SpyglassJson.ToElement(new ColorData(255, 5, 98, 239)), "Avalonia.Media.Color");
        var back = SpyglassJson.Read<ValueDto>(SpyglassJson.ToElement(value));

        Assert.Equal(ValueKind.Color, back.Kind);
        Assert.Equal(98, SpyglassJson.Read<ColorData>(back.Data).G);
        Assert.Contains("\"kind\":\"color\"", SpyglassJson.ToElement(value).GetRawText());
    }

    [Fact]
    public void Message_kinds_are_classified()
    {
        Assert.True(SpyglassJson.Request(1, Methods.PropsGet, new PropsGetParams(Id: 1)).IsRequest);
        Assert.True(SpyglassJson.Notification(Methods.PickHover, new PickHoverParams(1)).IsNotification);
        Assert.True(SpyglassJson.Response(1, Empty.Instance).IsResponse);
        var err = Wire(SpyglassJson.ErrorResponse(7, ErrorCodes.NodeNotFound, "gone"));
        Assert.Equal(-32001, err.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData("0.1", true)]
    [InlineData("0.9", true)]
    [InlineData("1.0", false)]
    [InlineData("garbage", false)]
    public void Version_compatibility_is_by_major(string v, bool ok) =>
        Assert.Equal(ok, SpyglassProtocol.IsCompatible(v));

    [Fact]
    public void Log_entries_carry_a_cursor_and_source()
    {
        var entry = new LogEntryDto(DateTimeOffset.UnixEpoch, "Warning", "Layout", "Layout cycle", 42) { Seq = 7, SourceType = "Button#Save" };
        var p = Wire(SpyglassJson.Response(3, new LogsGetResult([entry], 2, "Warning", null))).GetProperty("result");
        var e = p.GetProperty("entries")[0];
        Assert.Equal(7, e.GetProperty("seq").GetInt64());
        Assert.Equal(42, e.GetProperty("sourceId").GetInt32());
        Assert.Equal("Button#Save", e.GetProperty("sourceType").GetString());
        Assert.Equal(2, p.GetProperty("omitted").GetInt32());
        Assert.Equal("Warning", p.GetProperty("minLevel").GetString());
        Assert.False(p.TryGetProperty("areas", out _));

        var req = SpyglassJson.Read<LogsGetParams>(JsonDocument.Parse("""{"since":5,"minLevel":"Error"}""").RootElement);
        Assert.Equal(new LogsGetParams(5, "Error", null, 500), req);
    }

    [Fact]
    public void Garbage_input_is_a_protocol_error() =>
        Assert.ThrowsAny<Exception>(() => SpyglassJson.Deserialize(Encoding.UTF8.GetBytes("not json")));
}
