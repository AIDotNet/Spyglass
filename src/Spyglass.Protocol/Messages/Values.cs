using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spyglass.Protocol.Messages;

/// <summary>
/// A property value as it crosses the wire (design/protocol/README.md §5.3).
/// <see cref="Text"/> is always displayable; <see cref="Data"/> is structured and kind-specific.
/// </summary>
public sealed record ValueDto(ValueKind Kind, string Text, JsonElement? Data = null, string? Type = null);

public enum ValueKind
{
    [JsonStringEnumMemberName("null")] Null,
    [JsonStringEnumMemberName("unset")] Unset,
    [JsonStringEnumMemberName("bool")] Bool,
    [JsonStringEnumMemberName("number")] Number,
    [JsonStringEnumMemberName("string")] String,
    [JsonStringEnumMemberName("enum")] Enum,
    [JsonStringEnumMemberName("color")] Color,
    [JsonStringEnumMemberName("brush")] Brush,
    [JsonStringEnumMemberName("thickness")] Thickness,
    [JsonStringEnumMemberName("cornerRadius")] CornerRadius,
    [JsonStringEnumMemberName("size")] Size,
    [JsonStringEnumMemberName("point")] Point,
    [JsonStringEnumMemberName("rect")] Rect,
    [JsonStringEnumMemberName("object")] Object,
    [JsonStringEnumMemberName("collection")] Collection,
    [JsonStringEnumMemberName("json")] Json,
}

// Structured payloads for ValueDto.Data.
public sealed record ColorData(byte A, byte R, byte G, byte B);
public sealed record BrushData(ColorData? Color);
public sealed record ThicknessData(double L, double T, double R, double B);
public sealed record CornerRadiusData(double Tl, double Tr, double Br, double Bl);
public sealed record SizeData(double W, double H);
public sealed record PointData(double X, double Y);
public sealed record RectData(double X, double Y, double W, double H);
public sealed record EnumData(string Value, IReadOnlyList<string> Values, bool Flags);
/// <summary>Handle for drilling into an object / collection with props/get { ref }.</summary>
public sealed record RefData(int Ref, string Summary, int? Count = null);
