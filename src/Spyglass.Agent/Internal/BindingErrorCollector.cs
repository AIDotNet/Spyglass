using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Logging;
using Avalonia.Markup.Xaml.Diagnostics;
using Spyglass.Protocol.Messages;

namespace Spyglass.Agent.Internal;

/// <summary>
/// Captures binding errors through Avalonia's public log sink (spike S1, B1/B2) and forwards everything to the
/// sink that was installed before. Installed at startup, so errors raised before Spyglass connects are kept.
/// Errors are aggregated by target + property + path (design/protocol/01-methods §9).
/// </summary>
internal sealed class BindingErrorCollector : ILogSink
{
    const int MaxEntries = 1000;

    sealed class Entry
    {
        public required WeakReference<AvaloniaObject> Target;
        public required string TargetType;
        public required string Property;
        public string? Path;
        public string? DataContextType;
        public required string Message;
        public SourceInfo? Location;
        public int Count;
        public DateTimeOffset LastTime;
        public long Seq;
    }

    readonly ILogSink? _inner;
    readonly Dictionary<string, Entry> _entries = new();
    readonly object _lock = new();
    long _seq;

    public static BindingErrorCollector? Instance { get; private set; }

    /// <summary>Cursor of the newest entry (pass to <see cref="Snapshot"/> as <c>since</c> to see what came after).</summary>
    public long LatestSeq { get { lock (_lock) return _seq; } }

    /// <summary>Raised (on the thread that logged, usually the UI thread) when an error is added or repeats.</summary>
    public event Action? Changed;

    BindingErrorCollector(ILogSink? inner) => _inner = inner;

    public static void Install()
    {
        if (Instance is not null) return;
        Instance = new BindingErrorCollector(Logger.Sink);
        Logger.Sink = Instance;
    }

    public bool IsEnabled(LogEventLevel level, string area) =>
        (area == LogArea.Binding && level >= LogEventLevel.Warning) || (_inner?.IsEnabled(level, area) ?? false);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        if (_inner?.IsEnabled(level, area) == true) _inner.Log(level, area, source, messageTemplate);
        if (area == LogArea.Binding) Record(source, messageTemplate, []);
    }

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (_inner?.IsEnabled(level, area) == true) _inner.Log(level, area, source, messageTemplate, propertyValues);
        if (area == LogArea.Binding) Record(source, messageTemplate, propertyValues);
    }

    void Record(object? source, string template, object?[] values)
    {
        if (source is not AvaloniaObject target) return;
        var named = Placeholders(template, values);
        var property = named.TryGetValue("Property", out var p) ? p?.ToString() ?? "?" : "?";
        var path = named.TryGetValue("Expression", out var x) ? x?.ToString() : null;
        var message = named.TryGetValue("Message", out var m) && m is not null ? m.ToString()! : Format(template, values);
        var key = $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(target)}|{property}|{path}";
        var location = XamlSourceInfo.GetXamlSourceInfo(target) is { SourceUri: { } uri } si
            ? new SourceInfo(uri.ToString(), XamlLines.ToOneBased(si.LineNumber, target), si.LinePosition) : null;

        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var e))
            {
                if (_entries.Count >= MaxEntries) return;
                e = _entries[key] = new Entry
                {
                    Target = new WeakReference<AvaloniaObject>(target),
                    TargetType = target.GetType().Name,
                    Property = property,
                    Path = path,
                    Message = message,
                    Location = location,
                };
            }
            e.Count++;
            e.LastTime = DateTimeOffset.Now;
            e.Message = message;
            e.DataContextType = (target as StyledElement)?.DataContext?.GetType().FullName;
            e.Seq = ++_seq;
        }
        Changed?.Invoke();
    }

    /// <param name="since">Only errors added or repeated after this cursor (the <c>seq</c> of a previous result).</param>
    public IReadOnlyList<BindingErrorDto> Snapshot(NodeRegistry registry, int? targetId, long since = 0)
    {
        lock (_lock)
        {
            var list = new List<BindingErrorDto>();
            foreach (var kv in _entries.OrderBy(kv => kv.Value.Seq))
            {
                var e = kv.Value;
                if (e.Seq <= since) continue;
                int? id = e.Target.TryGetTarget(out var t) ? registry.IdOf(t) : null;
                if (targetId is not null && id != targetId) continue;
                list.Add(new BindingErrorDto(kv.Key, e.Count, e.LastTime, id, e.TargetType, e.Property, e.Path, e.DataContextType, e.Message, e.Location) { Seq = e.Seq });
            }
            return list;
        }
    }

    static Dictionary<string, object?> Placeholders(string template, object?[] values)
    {
        var d = new Dictionary<string, object?>();
        var i = 0;
        foreach (Match m in Regex.Matches(template, @"\{(\w+)\}"))
            if (i < values.Length) d[m.Groups[1].Value] = values[i++];
        return d;
    }

    static string Format(string template, object?[] values)
    {
        var i = 0;
        return Regex.Replace(template, @"\{\w+\}", m => i < values.Length ? $"{values[i++]}" : m.Value);
    }
}
