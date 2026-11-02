using System.Collections.ObjectModel;
using Avalonia.Threading;
using Spyglass.App.Core;
using Spyglass.Protocol.Messages;
using TKCode.Arc;

namespace Spyglass.App.ViewModels;

/// <summary>
/// Logs page: Avalonia's own log output from the app, pushed live by the agent. The level control sets what the agent
/// records (logs/configure) and also filters the list; area, text and Clear only affect this view.
/// </summary>
public sealed class LogsViewModel : ObservableObject
{
    const int MaxEntries = 5000;
    static readonly string[] Levels = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    readonly AgentSession _session;
    readonly Action<int> _showElement;
    readonly List<LogRowViewModel> _all = new();   // oldest first
    long _cursor;         // highest seq seen
    string _minLevel = "Warning";
    string? _area;
    string _search = "";
    IDisposable? _searchDebounce;

    public LogsViewModel(AgentSession session, Action<int> showElement)
    {
        _session = session;
        _showElement = showElement;
        session.LogEntries += p => Dispatcher.UIThread.Post(() => Merge(p.Entries));
        ClearCommand = new RelayCommand(Clear);
        AreaOptions.Add(AllAreas);
    }

    /// <summary>Newest first, filtered.</summary>
    public ObservableCollection<LogRowViewModel> Entries { get; } = new();
    public const string AllAreas = "All areas";
    /// <summary>"All areas" followed by every area seen so far, sorted.</summary>
    public ObservableCollection<string> AreaOptions { get; } = new();
    public RelayCommand ClearCommand { get; }

    /// <summary>Avalonia LogEventLevel name. Changing it reconfigures what the agent records.</summary>
    public string MinLevel
    {
        get => _minLevel;
        set
        {
            if (string.IsNullOrEmpty(value) || !Set(ref _minLevel, value)) return;
            Raise(nameof(RecordingText));
            Refilter();
            _ = ConfigureAsync(value);
        }
    }

    /// <summary>Selected area option (<see cref="AllAreas"/> = no filter).</summary>
    public string Area
    {
        get => _area ?? AllAreas;
        set
        {
            var area = string.IsNullOrEmpty(value) || value == AllAreas ? null : value;
            if (Set(ref _area, area)) Refilter();
        }
    }

    public string SearchText
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value ?? "")) return;
            _searchDebounce?.Dispose();
            _searchDebounce = DispatcherTimer.RunOnce(Refilter, TimeSpan.FromMilliseconds(150));
        }
    }

    public int Count => Entries.Count;
    public string CountText => Entries.Count == _all.Count ? $"{Count}" : $"{Count} of {_all.Count}";
    public bool IsEmpty => Entries.Count == 0;
    public bool HasEntries => !IsEmpty;
    public string EmptyTitle => _all.Count == 0 ? "No log entries" : "No matching entries";
    public string EmptyDescription => _all.Count == 0
        ? $"Avalonia warnings and errors (layout, rendering, properties, bindings…) appear here as they happen. Recording {LevelLabel(_minLevel)} and above."
        : "Change the level, area or search filter to see more.";
    public string RecordingText => $"Recording {LevelLabel(_minLevel)} and above";

    /// <summary>Warnings and errors received (for the sidebar badge).</summary>
    public int ProblemCount { get; private set; }

    public async Task LoadAsync()
    {
        try
        {
            var r = await _session.GetLogsAsync(_cursor, limit: 2000);
            if (Set(ref _minLevel, r.MinLevel, nameof(MinLevel))) Raise(nameof(RecordingText));
            Merge(r.Entries);
        }
        catch { /* agent without the logs capability, or disconnected */ }
    }

    async Task ConfigureAsync(string level)
    {
        try { await _session.ConfigureLogsAsync(level); }
        catch { /* older agent or disconnected */ }
    }

    void Merge(IReadOnlyList<LogEntryDto> entries)
    {
        var added = false;
        foreach (var e in entries.OrderBy(e => e.Seq))
        {
            if (e.Seq <= _cursor) continue;   // also skips entries from before Clear, and duplicates of logs/get + push
            _cursor = e.Seq;
            var row = new LogRowViewModel(e, _showElement);
            _all.Add(row);
            if (row.IsProblem) ProblemCount++;
            if (!AreaOptions.Contains(e.Area)) InsertArea(e.Area);
            if (Matches(row)) Entries.Insert(0, row);
            added = true;
        }
        if (!added) return;
        if (_all.Count > MaxEntries)
        {
            var drop = _all.Count - MaxEntries;
            foreach (var old in _all.Take(drop))
            {
                Entries.Remove(old);
                if (old.IsProblem) ProblemCount--;
            }
            _all.RemoveRange(0, drop);
        }
        RaiseCounts();
    }

    void InsertArea(string area)
    {
        var i = 1;
        while (i < AreaOptions.Count && string.CompareOrdinal(AreaOptions[i], area) < 0) i++;
        AreaOptions.Insert(i, area);
    }

    bool Matches(LogRowViewModel r) =>
        r.LevelRank >= Rank(_minLevel)
        && (_area is null || r.Area == _area)
        && (_search.Length == 0
            || r.Message.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || r.Area.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || (r.SourceText?.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false));

    void Refilter()
    {
        Entries.Clear();
        for (var i = _all.Count - 1; i >= 0; i--)
            if (Matches(_all[i])) Entries.Add(_all[i]);
        RaiseCounts();
    }

    void Clear()
    {
        _all.Clear();
        Entries.Clear();
        ProblemCount = 0;
        RaiseCounts();
    }

    void RaiseCounts()
    {
        Raise(nameof(Count));
        Raise(nameof(CountText));
        Raise(nameof(IsEmpty));
        Raise(nameof(HasEntries));
        Raise(nameof(EmptyTitle));
        Raise(nameof(EmptyDescription));
        Raise(nameof(ProblemCount));
    }

    internal static int Rank(string level) => Array.IndexOf(Levels, level) is var i and >= 0 ? i : 3;
    static string LevelLabel(string level) => level == "Information" ? "Info" : level;
}

public sealed class LogRowViewModel(LogEntryDto e, Action<int> showElement)
{
    public long Seq { get; } = e.Seq;
    public string Time { get; } = e.Time.ToString("HH:mm:ss.fff");
    public string Level { get; } = e.Level == "Information" ? "Info" : e.Level;
    public int LevelRank { get; } = LogsViewModel.Rank(e.Level);
    public bool IsProblem => LevelRank >= 3;
    public BadgeTone LevelTone { get; } = e.Level switch
    {
        "Fatal" or "Error" => BadgeTone.Danger,
        "Warning" => BadgeTone.Warning,
        "Information" => BadgeTone.Info,
        _ => BadgeTone.Neutral,
    };
    public string Area { get; } = e.Area;
    public string Message { get; } = e.Message;
    public string? SourceText { get; } = e.SourceType;
    public bool HasSource => SourceText is not null;
    public bool CanShow { get; } = e.SourceId is not null;
    public string ShowTip => CanShow ? "Show this element in Elements" : "This object is not in the element tree (or no longer exists)";
    public RelayCommand ShowCommand { get; } = new(() => { if (e.SourceId is { } id) showElement(id); });
}
