using System.Text.Json;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;
using Xunit;

namespace Spyglass.Protocol.Tests;

/// <summary>Pins the resources wire format to design/protocol/01-methods.md §13.</summary>
public class ResourcesWireTests
{
    static JsonElement Wire(JsonRpcMessage m) => JsonDocument.Parse(SpyglassJson.Serialize(m)).RootElement;

    [Fact]
    public void Provider_tree_and_lookup_round_trip_with_camelCase()
    {
        var provider = new ResourceProviderDto
        {
            Id = 8, Kind = "themeDictionary", Name = "ThemeDictionaries[Dark]", KeyCount = 2, Variant = "Dark",
            Source = new SourceInfo("file:///App.axaml", 12, 9),
        };
        var root = new ResourceProviderDto { Id = 1, Kind = "application", Name = "App", Children = [provider] };
        var p = Wire(SpyglassJson.Response(1, new ResourcesGetProvidersResult([root], "Dark", ["Default", "Light", "Dark"]))).GetProperty("result");
        Assert.Equal("themeDictionary", p.GetProperty("providers")[0].GetProperty("children")[0].GetProperty("kind").GetString());
        Assert.Equal("Dark", p.GetProperty("activeVariant").GetString());
        Assert.False(p.GetProperty("providers")[0].TryGetProperty("variant", out _));   // nulls are omitted

        var step = new ResourceStepDto { Provider = provider, Path = "Application › ThemeDictionaries[Dark]", Hit = true, Winner = true, Value = new ValueDto(ValueKind.Color, "#ff0562ef") };
        var lookup = new ResourcesLookupResult { Key = "Test.Accent", Variant = "Dark", Found = true, Chain = [step] };
        var back = SpyglassJson.Read<ResourcesLookupResult>(Wire(SpyglassJson.Response(2, lookup)).GetProperty("result"));
        Assert.True(back.Chain[0].Winner);
        Assert.Equal("#ff0562ef", back.Chain[0].Value!.Text);
    }

    [Fact]
    public void Set_params_and_entries_use_camelCase()
    {
        var m = Wire(SpyglassJson.Request(3, Methods.ResourcesSet, new ResourcesSetParams(8, "Test.Accent", new ValueDto(ValueKind.Color, "#ff112233"))));
        Assert.Equal("resources/set", m.GetProperty("method").GetString());
        Assert.Equal(8, m.GetProperty("params").GetProperty("providerId").GetInt32());
        Assert.Equal("color", m.GetProperty("params").GetProperty("value").GetProperty("kind").GetString());

        var entry = new ResourceEntryDto { Key = "Test.Gap", Value = new ValueDto(ValueKind.Number, "12"), Editable = true, IsModifiedBySpyglass = true };
        var e = Wire(SpyglassJson.Response(4, new ResourcesSetResult(entry))).GetProperty("result").GetProperty("entry");
        Assert.True(e.GetProperty("isModifiedBySpyglass").GetBoolean());
        Assert.Equal("string", e.GetProperty("keyType").GetString());
    }
}
