using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spyglass.Protocol;

/// <summary>
/// One JSON-RPC 2.0 message (request, notification, or response). Payloads stay as <see cref="JsonElement"/>
/// so the envelope can be parsed before the method is known; typed payloads are read via <see cref="SpyglassJson"/>.
/// </summary>
public sealed record JsonRpcMessage
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    /// <summary>Present on requests and responses; absent on notifications.</summary>
    public long? Id { get; init; }

    /// <summary>Present on requests and notifications.</summary>
    public string? Method { get; init; }

    public JsonElement? Params { get; init; }
    public JsonElement? Result { get; init; }
    public JsonRpcError? Error { get; init; }

    [JsonIgnore] public bool IsRequest => Method is not null && Id is not null;
    [JsonIgnore] public bool IsNotification => Method is not null && Id is null;
    [JsonIgnore] public bool IsResponse => Method is null && Id is not null;
}

public sealed record JsonRpcError(int Code, string Message, JsonElement? Data = null);

/// <summary>Thrown by handlers to produce a JSON-RPC error response.</summary>
public sealed class SpyglassProtocolException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>Empty params / result object (<c>{}</c>).</summary>
public sealed record Empty
{
    public static readonly Empty Instance = new();
}
