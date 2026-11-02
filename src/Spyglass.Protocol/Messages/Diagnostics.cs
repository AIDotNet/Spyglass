using System.Text.Json.Serialization;

namespace Spyglass.Protocol.Messages;

// ===== events (v0.2) =====

public enum RouteStrategy
{
    [JsonStringEnumMemberName("direct")] Direct,
    [JsonStringEnumMemberName("tunnel")] Tunnel,
    [JsonStringEnumMemberName("bubble")] Bubble,
}

public sealed record RoutedEventInfo(string Name, string Owner, IReadOnlyList<RouteStrategy> Routes);
public sealed record ListRoutedResult(IReadOnlyList<RoutedEventInfo> Events);

/// <summary>
/// events/configure. <see cref="Enabled"/> entries are "Owner.Name", e.g. "InputElement.PointerPressed".
/// null = the default set (Button.Click and the common key/pointer events, never PointerMoved); empty = stop tracing.
/// </summary>
public sealed record EventsConfigureParams(IReadOnlyList<string>? Enabled = null, int BufferSize = 5000);

/// <summary>What the agent now traces; <see cref="Unknown"/> lists names that are not registered routed events.
/// <see cref="Cursor"/> is the newest record's seq at the time of the call (pass as <c>since</c> to events/get).</summary>
public sealed record EventsConfigureResult(IReadOnlyList<string> Enabled, IReadOnlyList<string> Unknown, long Cursor);

/// <summary>One node the event visited, in route order. <see cref="Handled"/>: Handled became true at this node.</summary>
public sealed record RouteStep(int Id, string Type, string? Name, bool Handled, RouteStrategy Phase = RouteStrategy.Bubble);

/// <summary>One raised routed event, aggregated over its whole route (all of its <c>RoutedEvent.Raised</c> calls).</summary>
public sealed record EventRecord(
    long Seq,
    DateTimeOffset Time,
    string Event,
    int SourceId,
    string SourceType,
    string? SourceName,
    RouteStrategy Strategy,
    bool Handled,
    int? HandledById,
    IReadOnlyList<RouteStep> Route);

/// <summary><see cref="Dropped"/>: records lost since the previous push because the client fell behind.</summary>
public sealed record EventsRaisedParams(IReadOnlyList<EventRecord> Records, int Dropped = 0);

/// <summary>events/get: buffered records with seq &gt; <see cref="Since"/>, oldest first; optionally only those whose route passes <see cref="ElementId"/>.</summary>
public sealed record EventsGetParams(long Since = 0, int Limit = 500, int? ElementId = null);

// ===== bindings (v0.2) =====

public sealed record BindingErrorDto(
    /// <summary>Aggregation key: target id + property + path.</summary>
    string Key,
    int Count,
    DateTimeOffset LastTime,
    int? TargetId,
    string TargetType,
    string Property,
    string? Path,
    string? DataContextType,
    string Message,
    SourceInfo? Location)
{
    /// <summary>Cursor: increases every time this error is added or repeats. Pass the max as <c>since</c>.</summary>
    public long Seq { get; init; }
}

public sealed record BindingErrorsParams(IReadOnlyList<BindingErrorDto> Errors);
/// <summary>bindings/getErrors request. <see cref="TargetId"/> limits to one element.</summary>
public sealed record GetBindingErrorsParams(int? TargetId = null, long Since = 0);
public sealed record BindingDetailsParams(int Id, string Property);
public sealed record BindingDetailsResult(
    string Description, string? Mode, string Priority, string? ErrorType,
    string? Converter, ValueDto? FallbackValue, ValueDto Value);

// ===== logs (v0.2) =====

/// <summary>
/// logs/configure: what the agent records from now on. <see cref="MinLevel"/> is an Avalonia <c>LogEventLevel</c> name
/// (Verbose, Debug, Information, Warning, Error, Fatal); <see cref="Areas"/> null records every area.
/// </summary>
public sealed record LogsConfigureParams(string MinLevel = "Warning", IReadOnlyList<string>? Areas = null);

public sealed record LogEntryDto(DateTimeOffset Time, string Level, string Area, string Message, int? SourceId)
{
    /// <summary>Cursor: increases with every recorded entry. Pass the max as <c>since</c>.</summary>
    public long Seq { get; init; }
    /// <summary>The source object's type (and <c>#Name</c>), e.g. "TextBlock#Title"; null when the entry has no source.</summary>
    public string? SourceType { get; init; }
}

/// <summary>logs/entries notification (250 ms batches). <see cref="Dropped"/>: entries not sent in this batch (too many).</summary>
public sealed record LogEntriesParams(IReadOnlyList<LogEntryDto> Entries)
{
    public int Dropped { get; init; }
}

/// <summary>
/// logs/get: the newest <see cref="Limit"/> buffered entries after <see cref="Since"/>, oldest first.
/// <see cref="MinLevel"/> / <see cref="Area"/> filter the buffer (they do not change what is recorded).
/// </summary>
public sealed record LogsGetParams(long Since = 0, string? MinLevel = null, string? Area = null, int Limit = 500);

/// <param name="Entries">Oldest first.</param>
/// <param name="Omitted">Matching entries after <c>since</c> that were left out because of <c>limit</c> (the oldest ones).</param>
/// <param name="MinLevel">What the agent currently records (logs/configure).</param>
/// <param name="Areas">Areas the agent currently records; null = all.</param>
public sealed record LogsGetResult(IReadOnlyList<LogEntryDto> Entries, int Omitted, string MinLevel, IReadOnlyList<string>? Areas);

// ===== source (v0.2) =====

public sealed record SourceResult(SourceInfo? Element);

// ===== metrics / overlays (v0.3) =====

// Types for metrics/overlays (MetricsConfigureParams, HistogramSummary, MetricsSampleParams…) live in Perf.cs.

public enum DebugOverlay
{
    [JsonStringEnumMemberName("fps")] Fps,
    [JsonStringEnumMemberName("dirtyRects")] DirtyRects,
    [JsonStringEnumMemberName("layoutTimeGraph")] LayoutTimeGraph,
    [JsonStringEnumMemberName("renderTimeGraph")] RenderTimeGraph,
}

public sealed record OverlaysSetParams(int TopLevelId, IReadOnlyList<DebugOverlay> Overlays);
