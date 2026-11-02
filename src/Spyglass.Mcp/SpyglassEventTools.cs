using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Spyglass.App.Core;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Mcp;

/// <summary>`events_trace` (design/mcp/01-tools.md #11): record routed events, read them with a cursor, stop.</summary>
[McpServerToolType]
public sealed class SpyglassEventTools(SpyglassHub hub)
{
    const string AppParam = "Connected app (id like 'a1', name, or pid). Optional when only one app is connected.";
    const int MaxRouteNodes = 8;

    sealed class TraceState
    {
        public long Cursor;
        public int? ElementId;
    }

    /// <summary>Per app session: the read cursor and the element filter given at start.</summary>
    static readonly ConditionalWeakTable<AgentSession, TraceState> States = new();

    [McpServerTool(Name = "events_trace", Title = "Trace routed events", ReadOnly = true, Idempotent = false, OpenWorld = false)]
    [Description("Records routed events in the app (clicks, key and pointer input, …): which element raised each one, the route it took and which element handled it. " +
                 "action='start' begins recording (default events: Button.Click, KeyDown, KeyUp, TextInput, PointerPressed, PointerReleased; never PointerMoved unless listed), " +
                 "then interact with the app (or ask the user to), then action='read' returns the events since the last read, and action='stop' ends recording. " +
                 "Only changes what Spyglass observes, never the app's UI. Use it to find out why a click or key press does nothing (handled earlier on the route?).")]
    public async Task<string> EventsTrace(
        [Description("'start', 'read' or 'stop'.")] string action,
        [Description("start: events to record as 'Owner.Name', e.g. ['Button.Click','InputElement.PointerPressed']. Default: common input events.")] string[]? events = null,
        [Description("start: only record events whose route passes this element (reference like 'e42' or a selector like 'Button#Save').")] string? element = null,
        [Description("read: cursor from a previous read; default continues after the last read.")] long? since = null,
        [Description("read: maximum events returned. Default 50.")] int limit = 50,
        [Description(AppParam)] string? app = null)
    {
        var s = ElementResolver.App(hub, app);
        if (!s.Has(Capabilities.Events))
            throw new McpException($"{s.App.Name}'s agent cannot trace events; update Spyglass.Agent.");
        var state = States.GetOrCreateValue(s);

        switch (action.Trim().ToLowerInvariant())
        {
            case "start":
            {
                int? elementId = element is null ? null : await ElementResolver.ElementAsync(s, element);
                var r = await s.ConfigureEventsAsync(events is { Length: > 0 } ? events : null);
                if (r.Enabled.Count == 0)
                    throw new McpException($"None of the events are known routed events: {string.Join(", ", r.Unknown)}. Names are 'Owner.Name', e.g. 'Button.Click', 'InputElement.KeyDown'.");
                state.Cursor = r.Cursor;
                state.ElementId = elementId;
                var sb = new StringBuilder($"Recording {string.Join(", ", r.Enabled)}");
                if (elementId is { } id) sb.Append($" — only events whose route passes [e{id}]");
                sb.Append($". (cursor: {r.Cursor})\n");
                if (r.Unknown.Count > 0) sb.Append($"Unknown events ignored: {string.Join(", ", r.Unknown)}.\n");
                sb.Append("Interact with the app, then call events_trace with action='read'.");
                return sb.ToString();
            }
            case "read":
            {
                var from = since ?? state.Cursor;
                var take = Math.Clamp(limit, 1, 500);
                var records = await s.GetEventsAsync(from, take + 1, state.ElementId);
                if (records.Count == 0)
                    return $"No events since cursor {from}. Is recording started (action='start') and has the app received input since?";
                var shown = records.Take(take).ToList();
                state.Cursor = shown[^1].Seq;
                var sb = new StringBuilder($"{shown.Count} event{(shown.Count == 1 ? "" : "s")} (cursor: {state.Cursor}){(records.Count > take ? "; more available, read again" : "")}:\n");
                foreach (var e in shown) sb.Append(Line(e)).Append('\n');
                return sb.ToString().TrimEnd();
            }
            case "stop":
            {
                await s.StopEventsAsync();
                var unread = (await s.GetEventsAsync(state.Cursor, 5000, state.ElementId)).Count;
                // The cursor and filter stay: unread events remain readable after stopping.
                return unread > 0
                    ? $"Stopped recording routed events. {unread} recorded event{(unread == 1 ? "" : "s")} not read yet (action='read')."
                    : "Stopped recording routed events.";
            }
            default:
                throw new McpException($"Unknown action '{action}'. Use 'start', 'read' or 'stop'.");
        }
    }

    static string Line(EventRecord e)
    {
        var name = e.Event[(e.Event.LastIndexOf('.') + 1)..];
        var source = Label(e.SourceId, e.SourceType, e.SourceName);
        var handler = e.HandledById is { } h && e.Route.FirstOrDefault(r => r.Handled) is { } step
            ? $"handled by {Label(h, step.Type, step.Name)}"
            : e.Handled ? "handled (before the traced route)" : "not handled";
        // Route of the main phase (bubble, else direct/tunnel) without repeats.
        var phase = e.Route.Any(r => r.Phase == RouteStrategy.Bubble) ? RouteStrategy.Bubble : e.Route[^1].Phase;
        var nodes = e.Route.Where(r => r.Phase == phase).Select(r => $"e{r.Id}").ToList();
        var route = string.Join(" → ", nodes.Take(MaxRouteNodes)) + (nodes.Count > MaxRouteNodes ? $" → … (+{nodes.Count - MaxRouteNodes})" : "");
        return $"#{e.Seq} {e.Time.ToLocalTime():HH:mm:ss.fff} {name,-15} source {source}  {e.Strategy.ToString().ToLowerInvariant()}  {handler}  route: {route}";
    }

    static string Label(int id, string type, string? name) => $"[e{id}] {type}{(string.IsNullOrEmpty(name) ? "" : "#" + name)}";
}
