using System.Text.Json.Serialization;

namespace Spyglass.Protocol.Messages;

// design/protocol/01-methods.md §13 — input/act, wait/until, changes/reset (capability "input").

public enum InputAction
{
    [JsonStringEnumMemberName("invoke")] Invoke,
    [JsonStringEnumMemberName("setText")] SetText,
    [JsonStringEnumMemberName("typeText")] TypeText,
    [JsonStringEnumMemberName("toggle")] Toggle,
    [JsonStringEnumMemberName("select")] Select,
    [JsonStringEnumMemberName("expand")] Expand,
    [JsonStringEnumMemberName("collapse")] Collapse,
    [JsonStringEnumMemberName("focus")] Focus,
    [JsonStringEnumMemberName("key")] Key,
    [JsonStringEnumMemberName("scrollIntoView")] ScrollIntoView,
    [JsonStringEnumMemberName("pointer")] Pointer,
}

/// <summary>
/// One semantic UI action on a node.
/// <see cref="Text"/>: setText/typeText content, or the item text for select. <see cref="Key"/>: "Enter", "Ctrl+S"…
/// <see cref="Index"/>: item index for select/scrollIntoView. <see cref="State"/>: toggle to this state (null = flip).
/// <see cref="X"/>/<see cref="Y"/>: pointer position relative to the node (null = centre). <see cref="Replace"/>: typeText selects all first.
/// </summary>
public sealed record InputActParams(int Id, InputAction Action)
{
    public string? Text { get; init; }
    public string? Key { get; init; }
    public int? Index { get; init; }
    public bool? State { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public string? Button { get; init; }
    public int ClickCount { get; init; } = 1;
    public bool Replace { get; init; }
}

/// <param name="Via">How it was done: "peer:IInvokeProvider", "property:Text", "synth:pointer"…</param>
/// <param name="Node">The target after the action (and a short settle).</param>
/// <param name="State">Control-specific state, e.g. "checked", "selected 'B' (index 1 of 3)".</param>
/// <param name="BindingErrors">Binding errors added or repeated during the action.</param>
/// <param name="LogWarnings">Warning+ log entries (other than binding) recorded during the action.</param>
public sealed record InputActResult(string Via, NodeDto Node, string? State, int BindingErrors, int LogWarnings)
{
    public IReadOnlyList<BindingErrorDto>? NewBindingErrors { get; init; }
    public IReadOnlyList<LogEntryDto>? NewLogs { get; init; }
    public string? Note { get; init; }
}

public enum WaitCondition
{
    [JsonStringEnumMemberName("exists")] Exists,
    [JsonStringEnumMemberName("gone")] Gone,
    [JsonStringEnumMemberName("visible")] Visible,
    [JsonStringEnumMemberName("hidden")] Hidden,
    [JsonStringEnumMemberName("enabled")] Enabled,
    [JsonStringEnumMemberName("propertyEquals")] PropertyEquals,
    [JsonStringEnumMemberName("idle")] Idle,
}

/// <summary>Either <see cref="Id"/> or <see cref="Selector"/> (any tree/search query) identifies the element; not needed for idle.</summary>
public sealed record WaitUntilParams(WaitCondition Condition)
{
    public int? Id { get; init; }
    public string? Selector { get; init; }
    public string? Property { get; init; }
    public string? Value { get; init; }
    public int TimeoutMs { get; init; } = 3000;
}

/// <param name="Met">False on timeout; the other fields then describe the state at the deadline.</param>
/// <param name="Actual">propertyEquals: the current value text; otherwise a short description ("2 matches", "not in the tree").</param>
public sealed record WaitUntilResult(bool Met, int ElapsedMs, int Matches)
{
    public NodeDto? Node { get; init; }
    public string? Actual { get; init; }
}

/// <summary>Undo Spyglass runtime changes (property edits, forced classes/pseudo-classes) of one node, or all when <see cref="Id"/> is null.</summary>
public sealed record ChangesResetParams(int? Id = null);

/// <param name="Kind">"property", "class" or "pseudoClass".</param>
public sealed record ChangeDto(int Id, string Type, string Kind, string Name, string Detail);

public sealed record ChangesResetResult(IReadOnlyList<ChangeDto> Restored);
