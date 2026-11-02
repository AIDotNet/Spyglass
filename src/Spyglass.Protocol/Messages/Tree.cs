using System.Text.Json.Serialization;

namespace Spyglass.Protocol.Messages;

public enum TreeKind
{
    [JsonStringEnumMemberName("visual")] Visual,
    [JsonStringEnumMemberName("logical")] Logical,
}

/// <summary>An element in the visual or logical tree. <see cref="Children"/> is null when not expanded.</summary>
public sealed record NodeDto
{
    public required int Id { get; init; }
    public required string Type { get; init; }
    public string? TypeFull { get; init; }
    public string? Name { get; init; }
    public IReadOnlyList<string>? Classes { get; init; }
    public IReadOnlyList<string>? PseudoClasses { get; init; }
    /// <summary>True when the element was created by a control template (TemplatedParent != null).</summary>
    public bool IsTemplatePart { get; init; }
    /// <summary>IsEffectivelyVisible.</summary>
    public bool IsVisible { get; init; } = true;
    public bool IsEnabled { get; init; } = true;
    public bool IsFocused { get; init; }
    /// <summary>Visible text (TextBlock.Text, string Content, TextBox.Text…), truncated to 80 chars. For MCP snapshots.</summary>
    public string? Text { get; init; }
    /// <summary>Placeholder / watermark text of input controls.</summary>
    public string? Placeholder { get; init; }
    /// <summary>Bounds in the coordinates of the node's TopLevel.</summary>
    public RectData? Bounds { get; init; }
    public int ChildCount { get; init; }
    public IReadOnlyList<NodeDto>? Children { get; init; }
}

public enum RootKind
{
    [JsonStringEnumMemberName("window")] Window,
    [JsonStringEnumMemberName("mainView")] MainView,
    [JsonStringEnumMemberName("popup")] Popup,
}

/// <param name="OwnerId">Window: its owner window (dialogs, tool windows). Popup: the Popup element that opened it.</param>
/// <param name="IsActive">The window currently has OS focus (Window.IsActive). Tools default to the active window.</param>
public sealed record RootInfo(NodeDto Node, RootKind Kind, string? Title, int? OwnerId = null, bool IsActive = false);

// ---- tree/getRoots ----
public sealed record GetRootsResult(IReadOnlyList<RootInfo> Roots);

// ---- tree/getChildren ----
public sealed record GetChildrenParams(int Id, TreeKind Tree = TreeKind.Visual, int Depth = -1);
public sealed record GetChildrenResult(NodeDto Node);

// ---- tree/changed (A→C ntf) ----
public sealed record TreeChangedParams(TreeKind Tree, IReadOnlyList<TreeOp> Ops);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(TreeOpAdd), "add")]
[JsonDerivedType(typeof(TreeOpRemove), "remove")]
[JsonDerivedType(typeof(TreeOpUpdate), "update")]
[JsonDerivedType(typeof(TreeOpRootAdded), "rootAdded")]
[JsonDerivedType(typeof(TreeOpRootRemoved), "rootRemoved")]
public abstract record TreeOp;

public sealed record TreeOpAdd(int ParentId, int Index, NodeDto Node) : TreeOp;
public sealed record TreeOpRemove(int ParentId, int Id) : TreeOp;
public sealed record TreeOpUpdate(int Id, string? Name, IReadOnlyList<string>? Classes, bool IsVisible) : TreeOp
{
    /// <summary>Set when a window became the active window.</summary>
    public bool? IsActive { get; init; }
}
public sealed record TreeOpRootAdded(RootInfo Root) : TreeOp;
public sealed record TreeOpRootRemoved(int Id) : TreeOp;

// ---- tree/getAncestors ----
public sealed record GetAncestorsParams(int Id, TreeKind Tree = TreeKind.Visual);
public sealed record NodeRef(int Id, string Type, string? Name);
/// <summary>Root first.</summary>
public sealed record GetAncestorsResult(IReadOnlyList<NodeRef> Path);

// ---- tree/search ----
/// <summary>
/// <see cref="Query"/>: selector subset (Type, #Name, .class, :pseudo, descendant ' ', child '>', :nth(n)),
/// or <c>text=…</c> (visible text), or <c>xaml=File.axaml:line</c>. See design/mcp/README.md §5.
/// </summary>
public sealed record SearchParams(string Query, TreeKind Tree = TreeKind.Visual, int Limit = 200, bool IncludeTemplate = false, int? RootId = null);
public sealed record SearchMatch(int Id, IReadOnlyList<int> Path, NodeDto Node);
public sealed record SearchResult(IReadOnlyList<SearchMatch> Matches, bool Truncated);

// ---- node/* ----
public sealed record NodeParams(int Id);
public sealed record SourceInfo(string Uri, int Line, int Column);
public sealed record DescribeResult(NodeDto Node, string Selector, string? DataContextType, SourceInfo? Source, int? TopLevelId = null);
public sealed record ScreenshotParams(int Id, double Scale = 1);
public sealed record ScreenshotResult(string Format, int Width, int Height, string Base64);
