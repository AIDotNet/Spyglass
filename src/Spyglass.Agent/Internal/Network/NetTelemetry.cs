using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace Spyglass.Agent.Internal.Network;

/// <summary>
/// System.Net.* EventSource telemetry for Chrome's Timing phases (spike S5). With TPL activity-id flow enabled,
/// every event (DNS, socket connect, TLS, queueing, headers, content) carries an activity id that chains back,
/// via RelatedActivityId on Start events, to the request activity captured when the request started.
/// Created only while network capture is on.
/// </summary>
internal sealed class NetTelemetry : EventListener
{
    public sealed record Evt(string Name, Guid Activity, long Ts, IReadOnlyList<string> PayloadNames, IReadOnlyList<object?> Payload)
    {
        public object? Get(string name) { var i = PayloadNames.ToList().IndexOf(name); return i >= 0 ? Payload[i] : null; }
    }

    readonly ConcurrentQueue<Evt> _events = new();
    readonly ConcurrentDictionary<Guid, Guid> _parent = new();
    readonly ConcurrentDictionary<long, string> _remoteByConnection = new();
    const int MaxEvents = 20_000;

    /// <summary>Remote address of a pooled connection (requests reusing a connection have no ConnectionEstablished event).</summary>
    public string? RemoteOf(long connectionId) => _remoteByConnection.TryGetValue(connectionId, out var r) ? r : null;

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "System.Threading.Tasks.TplEventSource")
            EnableEvents(source, EventLevel.LogAlways, (EventKeywords)0x80);   // TasksFlowActivityIds
        else if (source.Name is "System.Net.Http" or "System.Net.NameResolution" or "System.Net.Sockets" or "System.Net.Security")
            EnableEvents(source, EventLevel.Informational, EventKeywords.All);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!e.EventSource.Name.StartsWith("System.Net", StringComparison.Ordinal) || e.EventName is null) return;
        if (e.RelatedActivityId != Guid.Empty) _parent[e.ActivityId] = e.RelatedActivityId;
        if (e.EventName == "ConnectionEstablished" && e.PayloadNames is { } names && e.Payload is { } payload)
        {
            var id = names.IndexOf("connectionId");
            var addr = names.IndexOf("remoteAddress");
            if (id >= 0 && addr >= 0 && payload[id] is { } cid) _remoteByConnection[Convert.ToInt64(cid)] = payload[addr]?.ToString() ?? "";
        }
        _events.Enqueue(new Evt(e.EventName, e.ActivityId, Stopwatch.GetTimestamp(),
            e.PayloadNames ?? (IReadOnlyList<string>)[], e.Payload ?? (IReadOnlyList<object?>)[]));
        while (_events.Count > MaxEvents && _events.TryDequeue(out _)) { }
    }

    /// <summary>Events of a request (its activity tree) within a time window (redirect hops share one activity).</summary>
    public List<Evt> For(Guid root, long fromTs, long toTs) =>
        root == Guid.Empty ? [] : _events.Where(e => e.Ts >= fromTs && e.Ts <= toTs && IsUnder(e.Activity, root)).ToList();

    bool IsUnder(Guid a, Guid root)
    {
        for (var i = 0; i < 32 && a != Guid.Empty; i++)
        {
            if (a == root) return true;
            if (!_parent.TryGetValue(a, out a)) return false;
        }
        return false;
    }
}
