using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Spyglass.Protocol.Messages;

namespace Spyglass.Protocol;

/// <summary>Source-generated (trimming/AOT-safe) serializer metadata for every wire type.</summary>
// MaxDepth: tree/getChildren with Depth=-1 nests NodeDto.Children recursively (2 JSON levels per tree level),
// so real visual trees blow past the default limit of 64 ("CurrentDepth (64) is equal to or larger than the maximum allowed depth").
[JsonSourceGenerationOptions(
    MaxDepth = 1024,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(JsonRpcMessage))]
// hot reload
[JsonSerializable(typeof(HotReloadManifest))]
[JsonSerializable(typeof(HotReloadApplyParams))]
[JsonSerializable(typeof(HotReloadResult))]
[JsonSerializable(typeof(HotReloadCodeUpdate))]
[JsonSerializable(typeof(IdeHelloParams))]
[JsonSerializable(typeof(IdeHelloResult))]
[JsonSerializable(typeof(XamlDocumentParams))]
[JsonSerializable(typeof(XamlDiagnosticsParams))]
[JsonSerializable(typeof(HotReloadStatusParams))]
[JsonSerializable(typeof(EditorRevealParams))]
[JsonSerializable(typeof(EditorRevealResult))]
[JsonSerializable(typeof(XamlPrepareParams))]
[JsonSerializable(typeof(XamlCompletionParams))]
[JsonSerializable(typeof(XamlCompletionResult))]
[JsonSerializable(typeof(XamlHoverParams))]
[JsonSerializable(typeof(XamlHoverResult))]
[JsonSerializable(typeof(XamlDocumentationParams))]
[JsonSerializable(typeof(XamlDocumentationResult))]
[JsonSerializable(typeof(Empty))]
// session
[JsonSerializable(typeof(HelloParams))]
[JsonSerializable(typeof(HelloResult))]
[JsonSerializable(typeof(GoodbyeParams))]
[JsonSerializable(typeof(ActivateParams))]
// tree / node
[JsonSerializable(typeof(GetRootsResult))]
[JsonSerializable(typeof(GetChildrenParams))]
[JsonSerializable(typeof(GetChildrenResult))]
[JsonSerializable(typeof(TreeChangedParams))]
[JsonSerializable(typeof(GetAncestorsParams))]
[JsonSerializable(typeof(GetAncestorsResult))]
[JsonSerializable(typeof(SearchParams))]
[JsonSerializable(typeof(SearchResult))]
[JsonSerializable(typeof(NodeParams))]
[JsonSerializable(typeof(DescribeResult))]
[JsonSerializable(typeof(ScreenshotParams))]
[JsonSerializable(typeof(ScreenshotResult))]
// values / props / layout
[JsonSerializable(typeof(ValueDto))]
[JsonSerializable(typeof(ColorData))]
[JsonSerializable(typeof(BrushData))]
[JsonSerializable(typeof(ThicknessData))]
[JsonSerializable(typeof(CornerRadiusData))]
[JsonSerializable(typeof(SizeData))]
[JsonSerializable(typeof(PointData))]
[JsonSerializable(typeof(RectData))]
[JsonSerializable(typeof(EnumData))]
[JsonSerializable(typeof(RefData))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(PropsGetParams))]
[JsonSerializable(typeof(PropsGetResult))]
[JsonSerializable(typeof(PropsSetParams))]
[JsonSerializable(typeof(PropsSetResult))]
[JsonSerializable(typeof(PropsResetParams))]
[JsonSerializable(typeof(PropsChangedParams))]
[JsonSerializable(typeof(LayoutResult))]
[JsonSerializable(typeof(LayoutExplainResult))]
// styles / pick / highlight
[JsonSerializable(typeof(ClassesResult))]
[JsonSerializable(typeof(SetClassesParams))]
[JsonSerializable(typeof(SetPseudoClassParams))]
[JsonSerializable(typeof(FramesResult))]
[JsonSerializable(typeof(PickStartParams))]
[JsonSerializable(typeof(PickHoverParams))]
[JsonSerializable(typeof(PickSelectedParams))]
[JsonSerializable(typeof(HighlightShowParams))]
// events / bindings / logs / source / metrics
[JsonSerializable(typeof(ListRoutedResult))]
[JsonSerializable(typeof(EventsConfigureParams))]
[JsonSerializable(typeof(EventsConfigureResult))]
[JsonSerializable(typeof(EventsRaisedParams))]
[JsonSerializable(typeof(EventsGetParams))]
[JsonSerializable(typeof(BindingErrorsParams))]
[JsonSerializable(typeof(GetBindingErrorsParams))]
[JsonSerializable(typeof(BindingDetailsParams))]
[JsonSerializable(typeof(BindingDetailsResult))]
[JsonSerializable(typeof(LogsConfigureParams))]
[JsonSerializable(typeof(LogEntriesParams))]
[JsonSerializable(typeof(LogsGetParams))]
[JsonSerializable(typeof(LogsGetResult))]
[JsonSerializable(typeof(SourceResult))]
[JsonSerializable(typeof(MetricsConfigureParams))]
[JsonSerializable(typeof(MetricsSampleParams))]
[JsonSerializable(typeof(OverlaysSetParams))]
[JsonSerializable(typeof(MetricsStatusResult))]
[JsonSerializable(typeof(OverlaysResult))]
// input
[JsonSerializable(typeof(InputActParams))]
[JsonSerializable(typeof(InputActResult))]
[JsonSerializable(typeof(WaitUntilParams))]
[JsonSerializable(typeof(WaitUntilResult))]
[JsonSerializable(typeof(ChangesResetParams))]
[JsonSerializable(typeof(ChangesResetResult))]
// network
[JsonSerializable(typeof(NetworkEnableParams))]
[JsonSerializable(typeof(NetworkRequestsParams))]
[JsonSerializable(typeof(NetworkGetBodyParams))]
[JsonSerializable(typeof(NetworkBodyResult))]
[JsonSerializable(typeof(NetworkSearchParams))]
[JsonSerializable(typeof(NetworkSearchResult))]
[JsonSerializable(typeof(NetworkStatusResult))]
// resources
[JsonSerializable(typeof(ResourcesGetProvidersParams))]
[JsonSerializable(typeof(ResourcesGetProvidersResult))]
[JsonSerializable(typeof(ResourcesGetEntriesParams))]
[JsonSerializable(typeof(ResourcesGetEntriesResult))]
[JsonSerializable(typeof(ResourcesLookupParams))]
[JsonSerializable(typeof(ResourcesLookupResult))]
[JsonSerializable(typeof(ResourcesFindParams))]
[JsonSerializable(typeof(ResourcesFindResult))]
[JsonSerializable(typeof(ResourcesSetParams))]
[JsonSerializable(typeof(ResourcesSetResult))]
[JsonSerializable(typeof(ResourcesResetParams))]
public sealed partial class SpyglassJsonContext : JsonSerializerContext;

/// <summary>Helpers for building and reading JSON-RPC messages with the source-generated context.</summary>
public static class SpyglassJson
{
    static SpyglassJsonContext Ctx => SpyglassJsonContext.Default;

    static JsonTypeInfo<T> Info<T>() =>
        (JsonTypeInfo<T>?)Ctx.GetTypeInfo(typeof(T))
        ?? throw new InvalidOperationException($"{typeof(T).Name} is not registered in {nameof(SpyglassJsonContext)}.");

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Info<T>());

    public static T Read<T>(JsonElement? element) =>
        element is { } e
            ? e.Deserialize(Info<T>()) ?? throw new SpyglassProtocolException(ErrorCodes.InvalidParams, $"null {typeof(T).Name}")
            : throw new SpyglassProtocolException(ErrorCodes.InvalidParams, $"missing {typeof(T).Name}");

    public static JsonRpcMessage Request<T>(long id, string method, T @params) =>
        new() { Id = id, Method = method, Params = ToElement(@params) };

    public static JsonRpcMessage Notification<T>(string method, T @params) =>
        new() { Method = method, Params = ToElement(@params) };

    public static JsonRpcMessage Response<T>(long id, T result) =>
        new() { Id = id, Result = ToElement(result) };

    public static JsonRpcMessage ErrorResponse(long? id, int code, string message) =>
        new() { Id = id, Error = new JsonRpcError(code, message) };

    public static byte[] Serialize(JsonRpcMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, Ctx.JsonRpcMessage);

    public static JsonRpcMessage Deserialize(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize(utf8, Ctx.JsonRpcMessage)
        ?? throw new SpyglassProtocolException(ErrorCodes.InvalidRequest, "empty message");
}
