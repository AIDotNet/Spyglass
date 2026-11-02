namespace Spyglass.VisualStudio.Core;

public enum HubStatusKind { Off, Busy, Disconnected, Idle, Live, Error }

/// <summary>What the status display shows (VS Code: status bar item; Visual Studio: the Spyglass tool window).</summary>
public sealed record HubStatusView(HubStatusKind Kind, string Text, IReadOnlyList<string> Details);

/// <summary>Turns connection state, hotReload/status and the newest reload summary into display text.</summary>
public static class HubStatusPresenter
{
    public static HubStatusView Describe(bool enabled, HubConnectionState state, int port, HotReloadStatusParams? status, string? lastSummary, bool lastOk)
    {
        if (!enabled)
            return new(HubStatusKind.Off, "Spyglass: hot reload off", ["XAML hot reload is off (Extensions › Spyglass › Toggle XAML Hot Reload)."]);
        if (state is HubConnectionState.Starting or HubConnectionState.Connecting)
            return new(HubStatusKind.Busy, "Spyglass: connecting…", [state == HubConnectionState.Starting ? "Starting spyglass serve…" : "Connecting to Spyglass…"]);
        if (state != HubConnectionState.Connected)
            return new(HubStatusKind.Disconnected, "Spyglass: not running", [$"Spyglass is not running on port {port}. See Output › Spyglass for details."]);

        var all = status?.Apps ?? [];
        var live = all.Where(a => a.HotReload && a.Available).ToList();
        var lines = all.Select(a => $"{a.Name} (pid {a.Pid}): " + (!a.HotReload ? "no hot reload (add Spyglass.HotReload)"
            : a.Available ? $"{a.Documents} XAML file(s)" : $"unavailable: {a.Reason}")).ToList();
        if (lines.Count == 0) lines.Add("No app connected. Run an app that calls UseSpyglass().");
        var summary = lastSummary ?? status?.Last;
        if (summary is not null) { lines.Add(""); lines.Add(summary); }
        var ok = lastSummary is not null ? lastOk : status?.LastOk ?? true;
        var kind = !ok ? HubStatusKind.Error : live.Count > 0 ? HubStatusKind.Live : HubStatusKind.Idle;
        var text = "Spyglass" + (live.Count > 0 ? ": " + string.Join(", ", live.Select(a => a.Name)) : ": no app");
        return new(kind, text, lines);
    }
}
