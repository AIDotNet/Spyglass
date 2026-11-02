using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Logging;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Agent.Internal;

/// <summary>
/// Records Avalonia's own log output (every area: Binding, Layout, Visual, Property, Control, Animations…) at or above
/// a configurable level into a ring buffer, through the public log sink. Wraps the sink installed before it (so
/// <see cref="BindingErrorCollector"/> and e.g. <c>LogToTrace()</c> keep working). Installed at startup, so entries
/// logged before Spyglass connects are kept. Thread-safe: Avalonia may log from any thread.
/// </summary>
internal sealed class LogCollector : ILogSink
{
    public const int DefaultCapacity = 2000;
    const int MaxMessageLength = 4000;

    sealed class Entry
    {
        public required long Seq;
        public required DateTimeOffset Time;
        public required LogEventLevel Level;
        public required string Area;
        public required string Message;
        public WeakReference<AvaloniaObject>? Source;
        public string? SourceType;
    }

    readonly ILogSink? _inner;
    readonly Queue<Entry> _buffer = new();
    readonly object _lock = new();
    readonly int _capacity;
    long _seq;
    volatile LogEventLevel _minLevel = LogEventLevel.Warning;
    volatile HashSet<string>? _areas;   // null = all areas

    public static LogCollector? Instance { get; private set; }

    /// <summary>Raised on the thread that logged, after an entry was recorded.</summary>
    public event Action? Changed;

    internal LogCollector(ILogSink? inner, int capacity = DefaultCapacity)
    {
        _inner = inner;
        _capacity = Math.Max(1, capacity);
    }

    public static void Install()
    {
        if (Instance is not null) return;
        Instance = new LogCollector(Logger.Sink);
        Logger.Sink = Instance;
    }

    public LogEventLevel MinLevel => _minLevel;
    public long LatestSeq { get { lock (_lock) return _seq; } }
    public IReadOnlyList<string>? Areas => _areas?.Order().ToList();

    /// <summary>logs/configure. Applies to entries logged from now on.</summary>
    public void Configure(LogsConfigureParams p)
    {
        _minLevel = ParseLevel(p.MinLevel);
        _areas = p.Areas is null ? null : new HashSet<string>(p.Areas, StringComparer.OrdinalIgnoreCase);
    }

    bool Accepts(LogEventLevel level, string area) =>
        level >= _minLevel && (_areas is not { } a || a.Contains(area));

    // Only say yes to what we record or the inner sink wants, so Avalonia does not format Verbose messages for nothing.
    public bool IsEnabled(LogEventLevel level, string area) =>
        Accepts(level, area) || (_inner?.IsEnabled(level, area) ?? false);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        if (_inner?.IsEnabled(level, area) == true) _inner.Log(level, area, source, messageTemplate);
        if (Accepts(level, area)) Record(level, area, source, messageTemplate, []);
    }

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (_inner?.IsEnabled(level, area) == true) _inner.Log(level, area, source, messageTemplate, propertyValues);
        if (Accepts(level, area)) Record(level, area, source, messageTemplate, propertyValues);
    }

    void Record(LogEventLevel level, string area, object? source, string template, object?[] values)
    {
        string message;
        try { message = Format(template, values); }
        catch (Exception ex) { message = $"{template} (formatting failed: {ex.Message})"; }
        if (message.Length > MaxMessageLength) message = message[..MaxMessageLength] + "…";

        var entry = new Entry
        {
            Seq = 0,
            Time = DateTimeOffset.Now,
            Level = level,
            Area = area,
            Message = message,
            Source = source is AvaloniaObject ao ? new WeakReference<AvaloniaObject>(ao) : null,
            SourceType = Describe(source),
        };
        lock (_lock)
        {
            entry.Seq = ++_seq;
            _buffer.Enqueue(entry);
            while (_buffer.Count > _capacity) _buffer.Dequeue();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// The newest <paramref name="limit"/> entries after <paramref name="since"/> that match the filters, oldest first.
    /// Source ids are resolved now (UI thread: the registry is UI-thread only).
    /// </summary>
    public (IReadOnlyList<LogEntryDto> Entries, int Omitted) Snapshot(NodeRegistry registry, long since = 0,
        LogEventLevel? minLevel = null, string? area = null, int limit = 500)
    {
        List<Entry> matches;
        lock (_lock)
            matches = _buffer.Where(e => e.Seq > since
                                         && (minLevel is not { } ml || e.Level >= ml)
                                         && (area is null || string.Equals(e.Area, area, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        limit = Math.Max(0, limit);
        var omitted = Math.Max(0, matches.Count - limit);
        var list = matches.Skip(omitted).Select(e => new LogEntryDto(e.Time, LevelName(e.Level), e.Area, e.Message,
            e.Source is { } w && w.TryGetTarget(out var t) ? registry.IdOf(t) : null)
        {
            Seq = e.Seq,
            SourceType = e.SourceType,
        }).ToList();
        return (list, omitted);
    }

    public static LogEventLevel ParseLevel(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        null or "" => LogEventLevel.Warning,
        "verbose" or "trace" or "vrb" => LogEventLevel.Verbose,
        "debug" or "dbg" => LogEventLevel.Debug,
        "information" or "info" or "inf" => LogEventLevel.Information,
        "warning" or "warn" or "wrn" => LogEventLevel.Warning,
        "error" or "err" => LogEventLevel.Error,
        "fatal" or "ftl" or "critical" => LogEventLevel.Fatal,
        _ => throw new SpyglassProtocolException(ErrorCodes.InvalidParams,
            $"Unknown log level '{level}'. Use Verbose, Debug, Information, Warning, Error or Fatal."),
    };

    public static string LevelName(LogEventLevel level) => level.ToString();

    static string? Describe(object? source) => source switch
    {
        null => null,
        StyledElement { Name: { Length: > 0 } name } se => $"{se.GetType().Name}#{name}",
        _ => source.GetType().Name,
    };

    /// <summary>Fills Serilog-style placeholders ({Name}, {@Name}, {Name:format}) with the values, in order.</summary>
    static string Format(string template, object?[] values)
    {
        if (values.Length == 0) return template;
        var i = 0;
        return Regex.Replace(template, @"\{[@$]?\w+(?::[^}]*)?\}", m => i < values.Length ? FormatValue(values[i++]) : m.Value);
    }

    static string FormatValue(object? v) => v switch
    {
        null => "(null)",
        string s => s,
        StyledElement { Name: { Length: > 0 } name } se => $"{se.GetType().Name}#{name}",
        Exception ex => ex.Message,
        _ => v.ToString() ?? "",
    };
}
