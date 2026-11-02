using System.Collections.ObjectModel;
using Avalonia.Threading;
using Spyglass.App.Core;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.App.ViewModels;

/// <summary>
/// Events page (wireframe W3): pick routed event types, record them, inspect the route of one record.
/// Tracing starts the first time the page is opened (<see cref="EnsureStartedAsync"/>), never before.
/// </summary>
public sealed class EventsViewModel : ObservableObject
{
    /// <summary>Same as the agent's default set (design W3); PointerMoved is never on by default.</summary>
    static readonly HashSet<string> DefaultTypes =
    [
        "Button.Click", "InputElement.KeyDown", "InputElement.KeyUp", "InputElement.TextInput",
        "InputElement.PointerPressed", "InputElement.PointerReleased",
    ];

    const int MaxRows = 5000;

    readonly AgentSession _session;
    readonly Action<int> _showElement;
    readonly List<EventTypeViewModel> _allTypes = new();
    bool _started;
    bool _isRecording;
    bool _configureQueued;
    bool _suspendConfigure;
    string _typeFilter = "";
    string? _error;
    int _dropped;
    EventRowViewModel? _selected;

    public EventsViewModel(AgentSession session, Action<int> showElement)
    {
        _session = session;
        _showElement = showElement;
        RecordCommand = new RelayCommand(ToggleRecordingAsync);
        ClearCommand = new RelayCommand(Clear);
        DefaultsCommand = new RelayCommand(() => SetChecked(t => DefaultTypes.Contains(t.Key)));
        AllCommand = new RelayCommand(() => { var visible = Types.ToHashSet(); SetChecked(t => t.IsChecked || visible.Contains(t)); });
        NoneCommand = new RelayCommand(() => SetChecked(_ => false));
        ShowSourceCommand = new RelayCommand(() => { if (Selected is { } s) _showElement(s.Record.SourceId); });
        session.EventsRaised += p => Dispatcher.UIThread.Post(() => Add(p));
    }

    public bool IsSupported => _session.Has(Capabilities.Events);

    // ---------- event types (left card) ----------

    public ObservableCollection<EventTypeViewModel> Types { get; } = new();

    public string TypeFilter
    {
        get => _typeFilter;
        set { if (Set(ref _typeFilter, value)) ApplyTypeFilter(); }
    }

    public string TypesSummary => $"{_allTypes.Count(t => t.IsChecked)} of {_allTypes.Count} traced";

    public RelayCommand DefaultsCommand { get; }
    public RelayCommand AllCommand { get; }
    public RelayCommand NoneCommand { get; }

    void ApplyTypeFilter()
    {
        var f = _typeFilter.Trim();
        Types.Clear();
        foreach (var t in _allTypes.Where(t => f.Length == 0 || t.Key.Contains(f, StringComparison.OrdinalIgnoreCase)))
            Types.Add(t);
    }

    void SetChecked(Func<EventTypeViewModel, bool> isChecked)
    {
        _suspendConfigure = true;
        foreach (var t in _allTypes) t.IsChecked = isChecked(t);
        _suspendConfigure = false;
        OnTypeToggled();
    }

    internal void OnTypeToggled()
    {
        if (_suspendConfigure) return;
        Raise(nameof(TypesSummary));
        if (_configureQueued) return;
        _configureQueued = true;
        // Checkbox clicks come in bursts: send one configure after they settle.
        DispatcherTimer.RunOnce(() => { _configureQueued = false; _ = ConfigureAsync(); }, TimeSpan.FromMilliseconds(250));
    }

    // ---------- records (center card) ----------

    public ObservableCollection<EventRowViewModel> Records { get; } = new();
    public bool HasRecords => Records.Count > 0;
    public bool IsEmpty => Records.Count == 0;
    public string CountText => $"{Records.Count:N0} events (max {MaxRows:N0}){(_dropped > 0 ? $" · {_dropped:N0} dropped" : "")}";
    public string EmptyDescription => !IsSupported
        ? "This app's agent cannot trace events. Update Spyglass.Agent."
        : _error ?? (IsRecording ? "Interact with the app: clicks, key presses and the other checked events appear here." : "Recording is paused. Press Record to capture events.");

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (!Set(ref _isRecording, value)) return;
            Raise(nameof(RecordLabel));
            Raise(nameof(RecordVariant));
            Raise(nameof(IsPaused));
            Raise(nameof(EmptyDescription));
        }
    }

    public bool IsPaused => !IsRecording;
    public string RecordLabel => IsRecording ? "Pause" : "Record";
    public TKCode.Arc.ButtonVariant RecordVariant => IsRecording ? TKCode.Arc.ButtonVariant.Secondary : TKCode.Arc.ButtonVariant.Primary;

    public RelayCommand RecordCommand { get; }
    public RelayCommand ClearCommand { get; }

    public EventRowViewModel? Selected
    {
        get => _selected;
        set
        {
            var old = _selected;
            if (!Set(ref _selected, value)) return;
            if (old is not null) old.IsSelected = false;
            if (value is not null) value.IsSelected = true;
            Raise(nameof(HasSelection));
            Raise(nameof(NoSelection));
        }
    }

    public bool HasSelection => Selected is not null;
    public bool NoSelection => Selected is null;
    public RelayCommand ShowSourceCommand { get; }
    internal void ShowElement(int id) => _showElement(id);

    /// <summary>First opening of the page: load the event types and start tracing the defaults.</summary>
    public async Task EnsureStartedAsync()
    {
        if (_started || !IsSupported) return;
        _started = true;
        try
        {
            var events = await _session.ListRoutedEventsAsync();
            _suspendConfigure = true;
            foreach (var e in events)
            {
                var vm = new EventTypeViewModel(this, e);
                vm.IsChecked = DefaultTypes.Contains(vm.Key);
                _allTypes.Add(vm);
            }
            _suspendConfigure = false;
            ApplyTypeFilter();
            Raise(nameof(TypesSummary));
            await ConfigureAsync();
        }
        catch (Exception ex) { SetError(ex); }
    }

    async Task ConfigureAsync()
    {
        try
        {
            await _session.ConfigureEventsAsync(_allTypes.Where(t => t.IsChecked).Select(t => t.Key).ToList());
            IsRecording = true;   // configure resumes recording
            SetError(null);
        }
        catch (Exception ex) { SetError(ex); }
    }

    async Task ToggleRecordingAsync()
    {
        try
        {
            if (IsRecording) await _session.PauseEventsAsync();
            else await _session.ResumeEventsAsync();
            IsRecording = !IsRecording;
        }
        catch (Exception ex) { SetError(ex); }
    }

    void Clear()
    {
        Selected = null;
        Records.Clear();
        _dropped = 0;
        RaiseRecords();
    }

    void Add(EventsRaisedParams p)
    {
        _dropped += p.Dropped;
        foreach (var r in p.Records) Records.Insert(0, new EventRowViewModel(this, r));   // newest first
        while (Records.Count > MaxRows)
        {
            if (ReferenceEquals(Records[^1], Selected)) Selected = null;
            Records.RemoveAt(Records.Count - 1);
        }
        RaiseRecords();
    }

    void RaiseRecords()
    {
        Raise(nameof(HasRecords));
        Raise(nameof(IsEmpty));
        Raise(nameof(CountText));
    }

    void SetError(Exception? ex)
    {
        _error = ex is null ? null : $"Could not configure tracing: {ex.Message}";
        Raise(nameof(EmptyDescription));
    }
}

public sealed class EventTypeViewModel : ObservableObject
{
    readonly EventsViewModel _owner;
    bool _isChecked;

    public EventTypeViewModel(EventsViewModel owner, RoutedEventInfo e)
    {
        _owner = owner;
        Key = $"{e.Owner}.{e.Name}";
        Name = e.Name;
        Owner = e.Owner;
        Routes = string.Join(" · ", e.Routes.Select(r => r.ToString().ToLowerInvariant()));
    }

    public string Key { get; }
    public string Name { get; }
    public string Owner { get; }
    public string Routes { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set { if (Set(ref _isChecked, value)) _owner.OnTypeToggled(); }
    }
}

public sealed class EventRowViewModel : ObservableObject
{
    readonly EventsViewModel _owner;
    bool _isSelected;
    IReadOnlyList<RouteStepViewModel>? _route;

    public EventRowViewModel(EventsViewModel owner, EventRecord r)
    {
        _owner = owner;
        Record = r;
        Time = r.Time.ToLocalTime().ToString("HH:mm:ss.fff");
        var dot = r.Event.LastIndexOf('.');
        Name = r.Event[(dot + 1)..];
        Owner = dot > 0 ? r.Event[..dot] : "";
        Source = Label(r.SourceType, r.SourceName);
        Strategy = r.Strategy.ToString().ToLowerInvariant();
        var handler = r.Route.FirstOrDefault(s => s.Handled);
        HandledBy = handler is not null ? Label(handler.Type, handler.Name) : r.Handled ? "handled" : "not handled";
        SelectCommand = new RelayCommand(() => owner.Selected = this);
    }

    public EventRecord Record { get; }
    public string Time { get; }
    public string Name { get; }
    public string Owner { get; }
    public string Source { get; }
    public string Strategy { get; }
    public bool Handled => Record.Handled;
    public string HandledBy { get; }
    public string Title => $"{Owner}.{Name}";
    public string Summary => $"{Strategy} · source {Source} · {HandledBy}";
    public RelayCommand SelectCommand { get; }

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>Route steps for the details card (created on first selection).</summary>
    public IReadOnlyList<RouteStepViewModel> Route => _route ??= Record.Route
        .Select((s, i) => new RouteStepViewModel(s, i == Record.Route.Count - 1, s.Id == Record.SourceId, _owner))
        .ToList();

    internal static string Label(string type, string? name) => string.IsNullOrEmpty(name) ? type : $"{type}#{name}";
}

public sealed class RouteStepViewModel(RouteStep s, bool isLast, bool isSource, EventsViewModel owner)
{
    public string Label { get; } = EventRowViewModel.Label(s.Type, s.Name);
    public string Phase { get; } = s.Phase.ToString().ToLowerInvariant();
    public bool IsTunnel { get; } = s.Phase == RouteStrategy.Tunnel;
    public bool IsBubble { get; } = s.Phase == RouteStrategy.Bubble;
    public bool IsDirect { get; } = s.Phase == RouteStrategy.Direct;
    public bool IsHandler { get; } = s.Handled;
    public bool IsNotHandler => !IsHandler;
    public bool IsSource { get; } = isSource;
    public bool HasConnector { get; } = !isLast;
    public string Id { get; } = $"e{s.Id}";
    public RelayCommand ShowCommand { get; } = new(() => owner.ShowElement(s.Id));
}
