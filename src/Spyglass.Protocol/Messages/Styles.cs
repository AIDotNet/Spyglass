using System.Text.Json.Serialization;

namespace Spyglass.Protocol.Messages;

// ---- styles/getClasses / setClasses / setPseudoClass ----
public sealed record ClassesResult(IReadOnlyList<string> Classes, IReadOnlyList<string> PseudoClasses, IReadOnlyList<string> KnownPseudoClasses);
public sealed record SetClassesParams(int Id, IReadOnlyList<string> Classes);
public sealed record SetPseudoClassParams(int Id, string PseudoClass, bool Active);

// ---- styles/getFrames (Advanced) ----
public sealed record FramesResult(IReadOnlyList<ValueFrameDto> Frames);

public sealed record ValueFrameDto(
    /// <summary>Local, Theme, Style, Template (IValueFrameDiagnostic.FrameType).</summary>
    string Type,
    string Priority,
    bool IsActive,
    FrameSource Source,
    IReadOnlyList<SetterDto> Setters);

public enum FrameSourceKind
{
    [JsonStringEnumMemberName("local")] Local,
    [JsonStringEnumMemberName("style")] Style,
    [JsonStringEnumMemberName("controlTheme")] ControlTheme,
    [JsonStringEnumMemberName("template")] Template,
    [JsonStringEnumMemberName("other")] Other,
}

public sealed record FrameSource(FrameSourceKind Kind, string? Selector = null, string? TargetType = null, SourceInfo? Location = null);

public sealed record SetterDto(string Property, ValueDto Value, bool IsOverridden);

// ---- pick/* ----
public enum PickMode
{
    [JsonStringEnumMemberName("element")] Element,
}

public sealed record PickStartParams(PickMode Mode = PickMode.Element);
public sealed record PickHoverParams(int Id);
/// <summary><see cref="Id"/> is null when picking was cancelled (Esc).</summary>
public sealed record PickSelectedParams(int? Id, IReadOnlyList<int>? Path);

// ---- highlight/* ----
public enum HighlightStyle
{
    [JsonStringEnumMemberName("box")] Box,
    [JsonStringEnumMemberName("layout")] Layout,
}

/// <summary><see cref="DurationMs"/> = 0 keeps the highlight until highlight/hide.</summary>
public sealed record HighlightShowParams(int Id, HighlightStyle Style = HighlightStyle.Box, int DurationMs = 1500);
