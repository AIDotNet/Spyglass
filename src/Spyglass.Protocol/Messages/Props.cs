using System.Text.Json.Serialization;

namespace Spyglass.Protocol.Messages;

public enum PropertyKind
{
    [JsonStringEnumMemberName("styled")] Styled,
    [JsonStringEnumMemberName("direct")] Direct,
    [JsonStringEnumMemberName("attached")] Attached,
    [JsonStringEnumMemberName("clr")] Clr,
}

public sealed record PropertyDto
{
    public required string Name { get; init; }
    public required string Owner { get; init; }
    public required PropertyKind Kind { get; init; }
    public string? Group { get; init; }
    public required ValueDto Value { get; init; }
    /// <summary>Avalonia BindingPriority name from GetDiagnostic: Animation, LocalValue, StyleTrigger, Template, Style, Inherited, Unset.</summary>
    public string Priority { get; init; } = "Unset";
    public bool IsReadOnly { get; init; }
    public bool IsSet { get; init; }
    public bool HasBinding { get; init; }
    /// <summary>True when Spyglass changed this value in the current session (props/reset can undo it).</summary>
    public bool IsModifiedBySpyglass { get; init; }
}

public enum PropertyScope
{
    [JsonStringEnumMemberName("avalonia")] Avalonia,
    [JsonStringEnumMemberName("clr")] Clr,
    [JsonStringEnumMemberName("attached")] Attached,
}

// ---- props/get ----
/// <summary>Either <see cref="Id"/> (a node) or <see cref="Ref"/> (a drilled-into object) must be set.</summary>
public sealed record PropsGetParams(int? Id = null, int? Ref = null, IReadOnlyList<PropertyScope>? Include = null);
public sealed record PropsGetResult(IReadOnlyList<PropertyDto> Properties);

// ---- props/set / props/reset ----
public sealed record PropsSetParams(int Id, string Name, string Owner, ValueDto Value);
public sealed record PropsSetResult(PropertyDto Property);
public sealed record PropsResetParams(int? Id = null, string? Name = null, string? Owner = null, bool All = false);

// ---- props/changed (A→C ntf) ----
public sealed record PropsChangedParams(int Id, IReadOnlyList<PropertyDto> Properties);

// ---- layout/get ----
public sealed record LayoutResult
{
    public required RectData Bounds { get; init; }
    public required SizeData DesiredSize { get; init; }
    public required ThicknessData Margin { get; init; }
    public ThicknessData? BorderThickness { get; init; }
    public ThicknessData? Padding { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
    public double? MinWidth { get; init; }
    public double? MaxWidth { get; init; }
    public double? MinHeight { get; init; }
    public double? MaxHeight { get; init; }
    public required string HorizontalAlignment { get; init; }
    public required string VerticalAlignment { get; init; }
    /// <summary>Constraint names that actually limited the size (UI underlines them).</summary>
    public IReadOnlyList<string> ActiveConstraints { get; init; } = [];
}

// ---- layout/explain (design/mcp/01-tools.md #10) ----

/// <summary>One reason an element cannot be seen. <see cref="NodeId"/> is the culprit (self or an ancestor).</summary>
public sealed record VisibilityIssue(string Code, string Message, int? NodeId = null, string? NodeType = null);

/// <param name="Layout">The same box model as layout/get.</param>
/// <param name="ParentId">The layout parent (nearest Layoutable ancestor), null for a window.</param>
/// <param name="ParentType">E.g. "StackPanel"; its orientation/columns are described in <see cref="Notes"/>.</param>
/// <param name="MeasureConstraint">Size the parent offered during measure (infinity = unconstrained, sent as null).</param>
/// <param name="ArrangeSlot">The slot the parent arranged the element into.</param>
/// <param name="VisibilityIssues">Empty when the element is visible. Ordered most fundamental first.</param>
/// <param name="Notes">Plain-language explanations of how the final size came about.</param>
public sealed record LayoutExplainResult(
    LayoutResult Layout,
    int? ParentId,
    string? ParentType,
    SizeConstraintData? MeasureConstraint,
    RectData? ArrangeSlot,
    IReadOnlyList<VisibilityIssue> VisibilityIssues,
    IReadOnlyList<string> Notes);

/// <summary>Width/height offered by a parent; null = unconstrained (infinity).</summary>
public sealed record SizeConstraintData(double? W, double? H);
