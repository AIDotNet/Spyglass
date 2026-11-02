using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Spyglass.App.Core;
using Spyglass.App.Core.Network;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.App.ViewModels;

/// <summary>
/// Network panel (design/network/README.md §5, behaviour per 02-chrome-parity.md).
/// Recording starts the first time the panel is opened (Chrome records while DevTools is open), and keeps going
/// when switching to other panels; Ctrl/Cmd+E toggles.
/// </summary>
public sealed class NetworkViewModel : ObservableObject
{
    const double WaterfallWidth = 140;

    readonly AgentSession _session;
    readonly NetworkSettingsStore _store;
    readonly NetworkSettings _settings;
    readonly Dictionary<long, NetworkRowViewModel> _rows = new();
    /// <summary>Bodies of imported HAR entries (negative ids). Kept apart from the agent's cache and from live recording.</summary>
    readonly Dictionary<(long, string), NetworkBodyResult> _importedBodies = new();
    long _importSeq;
    bool _activated;
    bool _recording;
    bool _preserveLog;
    bool _invert;
    string _filter = "";
    NetworkRowViewModel? _selected;
    string _summary = "";
    string? _notice;
    NetworkSortSpec _sort = NetworkSortSpec.Chronological;
    WaterfallSort _lastWaterfall = WaterfallSort.StartTime;
    HashSet<NetworkColumn> _visible;
    DateTimeOffset? _rangeFrom, _rangeTo;
    OverviewModel _overview = OverviewModel.Empty;
    double? _selStartMs, _selEndMs;
    bool _searchOpen, _searchCase, _searchRegex;
    string _searchText = "", _searchStatus = "";
    List<long> _hits = [];

    public NetworkViewModel(AgentSession session, NetworkSettingsStore? store = null)
    {
        _session = session;
        _store = store ?? new NetworkSettingsStore();
        _settings = _store.Load();
        _visible = _settings.VisibleColumns;
        Details = new NetworkDetailsViewModel(session, text => CopyRequested?.Invoke(text)) { ImportedBody = (id, part) => _importedBodies.GetValueOrDefault((id, part)) };
        NameColumn = new ColumnViewModel(NetworkColumn.Name, "Name", 0);
        RebuildColumns();
        ToggleRecordCommand = new RelayCommand(ToggleRecordingAsync);
        ClearCommand = new RelayCommand(ClearAsync);
        Types = new ObservableCollection<TypeToggleViewModel>(
            new (string Label, ResourceType? Type)[]
            {
                ("All", null), ("Fetch/XHR", ResourceType.FetchXhr), ("Doc", ResourceType.Doc), ("CSS", ResourceType.Css),
                ("JS", ResourceType.Js), ("Font", ResourceType.Font), ("Img", ResourceType.Img), ("Media", ResourceType.Media),
                ("WS", ResourceType.WS), ("Wasm", ResourceType.Wasm), ("Other", ResourceType.Other),
            }.Select(t => new TypeToggleViewModel(t.Label, t.Type, this)));
        Types[0].IsOn = true;
        session.Network.Changed += updates => Dispatcher.UIThread.Post(() => Apply(updates));
        foreach (var r in session.Network.Requests) Upsert(r);
        Refilter();
    }

    public ObservableCollection<NetworkRowViewModel> Rows { get; } = new();
    public ObservableCollection<TypeToggleViewModel> Types { get; }
    public NetworkDetailsViewModel Details { get; }
    public RelayCommand ToggleRecordCommand { get; }
    public RelayCommand ClearCommand { get; }

    /// <summary>The view puts the text on the clipboard.</summary>
    public event Action<string>? CopyRequested;

    public bool IsRecording
    {
        get => _recording;
        private set { if (Set(ref _recording, value)) { Raise(nameof(RecordTip)); Raise(nameof(EmptyTitle)); Raise(nameof(EmptyDescription)); } }
    }

    public string RecordTip => IsRecording ? "Stop recording network log (Ctrl+E)" : "Record network log (Ctrl+E)";
    public string EmptyTitle => IsRecording ? "Recording network activity…" : "Recording is stopped";
    public string EmptyDescription => IsRecording
        ? "Perform a request in the app to see it here. You can also drop a .har file here."
        : "Press Ctrl+E (⌘E) to start recording. Requests are only intercepted while recording. You can also drop a .har file here.";

    public bool PreserveLog { get => _preserveLog; set => Set(ref _preserveLog, value); }
    public bool Invert { get => _invert; set { if (Set(ref _invert, value)) Refilter(); } }
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) Refilter(); } }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public bool IsEmpty => Rows.Count == 0;
    public bool HasRows => !IsEmpty;
    public bool HasSelection => _selected is not null;
    /// <summary>Chrome shows the "Copy all listed …" entries only while a filter, type button or time range narrows the list.</summary>
    public bool IsFiltered => _filter.Trim().Length > 0 || !Types[0].IsOn || _rangeFrom is not null;

    /// <summary>Import/export problems (shown as an alert above the table).</summary>
    public string? Notice { get => _notice; set { if (Set(ref _notice, value)) Raise(nameof(HasNotice)); } }
    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    // "Add custom header…" dialog
    bool _addingHeader;
    string _newHeaderName = "";
    public bool IsAddingHeader { get => _addingHeader; set => Set(ref _addingHeader, value); }
    public string NewHeaderName { get => _newHeaderName; set => Set(ref _newHeaderName, value); }

    // Chrome collapses the table to the Name column while a request's details are open (bound in NetworkView).

    public NetworkRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (_selected is not null) _selected.IsSelected = false;
            if (!Set(ref _selected, value)) return;
            if (value is not null) value.IsSelected = true;
            Raise(nameof(HasSelection));
            _ = Details.LoadAsync(value?.Request);
        }
    }

    // ---- columns, sorting (parity §3) ----

    public ColumnViewModel NameColumn { get; }
    /// <summary>Header cells right of Name, in display order (visible ones only are shown).</summary>
    public ObservableCollection<ColumnViewModel> Columns { get; } = new();
    public IReadOnlyList<string> CustomHeaders => _settings.CustomHeaders;
    public NetworkSortSpec Sort => _sort;

    public bool IsVisible(NetworkColumn c) => c == NetworkColumn.Name || _visible.Contains(c);
    public bool ShowId => IsVisible(NetworkColumn.Id);
    public bool ShowMethod => IsVisible(NetworkColumn.Method);
    public bool ShowStatus => IsVisible(NetworkColumn.Status);
    public bool ShowType => IsVisible(NetworkColumn.Type);
    public bool ShowProtocol => IsVisible(NetworkColumn.Protocol);
    public bool ShowScheme => IsVisible(NetworkColumn.Scheme);
    public bool ShowDomain => IsVisible(NetworkColumn.Domain);
    public bool ShowRemote => IsVisible(NetworkColumn.Remote);
    public bool ShowConnection => IsVisible(NetworkColumn.Connection);
    public bool ShowInitiator => IsVisible(NetworkColumn.Initiator);
    public bool ShowCookies => IsVisible(NetworkColumn.Cookies);
    public bool ShowSetCookies => IsVisible(NetworkColumn.SetCookies);
    public bool ShowSize => IsVisible(NetworkColumn.Size);
    public bool ShowTime => IsVisible(NetworkColumn.Time);
    public bool ShowWaterfall => IsVisible(NetworkColumn.Waterfall);

    static readonly (NetworkColumn Column, string Title, double Width)[] FixedColumns =
    [
        (NetworkColumn.Id, "#", 50), (NetworkColumn.Method, "Method", 60), (NetworkColumn.Status, "Status", 150), (NetworkColumn.Type, "Type", 74),
        (NetworkColumn.Protocol, "Protocol", 70), (NetworkColumn.Scheme, "Scheme", 64), (NetworkColumn.Domain, "Domain", 150),
        (NetworkColumn.Remote, "Remote address", 130), (NetworkColumn.Connection, "Connection ID", 100), (NetworkColumn.Initiator, "Initiator", 150),
        (NetworkColumn.Cookies, "Cookies", 66), (NetworkColumn.SetCookies, "Set cookies", 84), (NetworkColumn.Size, "Size", 70), (NetworkColumn.Time, "Time", 70),
    ];

    public static string TitleOf(NetworkColumn c) => c switch
    {
        NetworkColumn.Name => "Name", NetworkColumn.Waterfall => "Waterfall",
        _ => FixedColumns.First(f => f.Column == c).Title,
    };

    /// <summary>Optional columns offered in the header menu, in Chrome's order.</summary>
    public static IReadOnlyList<NetworkColumn> OptionalColumns { get; } =
        [NetworkColumn.Id, NetworkColumn.Method, NetworkColumn.Status, NetworkColumn.Type, NetworkColumn.Protocol, NetworkColumn.Scheme, NetworkColumn.Domain,
         NetworkColumn.Remote, NetworkColumn.Connection, NetworkColumn.Initiator, NetworkColumn.Cookies, NetworkColumn.SetCookies, NetworkColumn.Size, NetworkColumn.Time, NetworkColumn.Waterfall];

    void RebuildColumns()
    {
        Columns.Clear();
        foreach (var (c, title, width) in FixedColumns.Where(f => f.Column != NetworkColumn.Size && f.Column != NetworkColumn.Time))
            Columns.Add(new ColumnViewModel(c, title, width) { IsVisible = IsVisible(c) });
        foreach (var f in FixedColumns.Where(f => f.Column is NetworkColumn.Size or NetworkColumn.Time))
            Columns.Add(new ColumnViewModel(f.Column, f.Title, f.Width) { IsVisible = IsVisible(f.Column) });
        foreach (var h in _settings.CustomHeaders) Columns.Add(new ColumnViewModel(null, h, 120) { Custom = h, IsVisible = true });
        Columns.Add(new ColumnViewModel(NetworkColumn.Waterfall, "Waterfall", 150) { IsVisible = IsVisible(NetworkColumn.Waterfall) });
        NameColumn.IsVisible = true;
        foreach (var c in Columns.Append(NameColumn)) c.ApplySort(_sort);
        foreach (var r in _rows.Values) r.SetCustomColumns(_settings.CustomHeaders);
        RaiseShow();
    }

    void RaiseShow()
    {
        foreach (var n in new[] { nameof(ShowId), nameof(ShowMethod), nameof(ShowStatus), nameof(ShowType), nameof(ShowProtocol), nameof(ShowScheme), nameof(ShowDomain),
                     nameof(ShowRemote), nameof(ShowConnection), nameof(ShowInitiator), nameof(ShowCookies), nameof(ShowSetCookies), nameof(ShowSize), nameof(ShowTime),
                     nameof(ShowWaterfall), nameof(CustomHeaders) }) Raise(n);
    }

    public void ToggleColumn(NetworkColumn c)
    {
        if (c == NetworkColumn.Name) return;
        if (!_visible.Remove(c)) _visible.Add(c);
        _settings.SetVisible(c, _visible.Contains(c));
        _store.Save(_settings);
        RebuildColumns();
    }

    public void ResetColumns()
    {
        _settings.Columns = NetworkSettings.DefaultColumns.Select(c => c.ToString()).ToList();
        _visible = _settings.VisibleColumns;
        _store.Save(_settings);
        RebuildColumns();
    }

    public void AddCustomHeader(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || _settings.CustomHeaders.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        _settings.CustomHeaders.Add(name);
        _store.Save(_settings);
        RebuildColumns();
    }

    public void RemoveCustomHeader(string name)
    {
        if (_settings.CustomHeaders.RemoveAll(h => h.Equals(name, StringComparison.OrdinalIgnoreCase)) == 0) return;
        if (_sort.Custom is not null && _sort.Custom.Equals(name, StringComparison.OrdinalIgnoreCase)) _sort = NetworkSortSpec.Chronological;
        _store.Save(_settings);
        RebuildColumns();
        Refilter();
    }

    /// <summary>Header click: sort by the column; click again reverses (Chrome). The Waterfall column sorts by the order chosen in its menu.</summary>
    public void ToggleSort(ColumnViewModel c)
    {
        var next = c.Custom is { } custom
            ? (_sort.Custom == custom ? _sort with { Descending = !_sort.Descending } : new NetworkSortSpec(Custom: custom))
            : c.Column == _sort.Column && _sort.Custom is null
                ? _sort with { Descending = !_sort.Descending }
                : new NetworkSortSpec(c.Column, false, _lastWaterfall);
        SetSort(next);
    }

    public void SetWaterfallSort(WaterfallSort w) { _lastWaterfall = w; SetSort(new NetworkSortSpec(NetworkColumn.Waterfall, false, w)); }

    public void SetSort(NetworkSortSpec spec)
    {
        _sort = spec;
        foreach (var c in Columns.Append(NameColumn)) c.ApplySort(spec);
        Raise(nameof(Sort));
        Refilter();
    }

    // ---- settings (gear menu) ----

    public bool ShowOverview
    {
        get => _settings.ShowOverview;
        set { if (_settings.ShowOverview != value) { _settings.ShowOverview = value; _store.Save(_settings); Raise(); if (!value) SetRange(null, null); } }
    }

    public bool AllowSensitiveHar
    {
        get => _settings.AllowSensitiveHar;
        set { if (_settings.AllowSensitiveHar != value) { _settings.AllowSensitiveHar = value; _store.Save(_settings); Raise(); } }
    }

    // ---- overview (parity §5, §2.5) ----

    public OverviewModel Overview { get => _overview; private set => Set(ref _overview, value); }
    public double? SelStartMs { get => _selStartMs; private set => Set(ref _selStartMs, value); }
    public double? SelEndMs { get => _selEndMs; private set => Set(ref _selEndMs, value); }

    /// <summary>Drag-select in the overview (ms from the overview origin); null clears (double click).</summary>
    public void SetRange(double? startMs, double? endMs)
    {
        if (startMs is null || endMs is null) { _rangeFrom = _rangeTo = null; }
        else
        {
            var (a, b) = startMs <= endMs ? (startMs.Value, endMs.Value) : (endMs.Value, startMs.Value);
            _rangeFrom = Overview.At(a);
            _rangeTo = Overview.At(b);
        }
        Refilter();
    }

    // ---- search across requests (parity §8) ----

    public bool SearchOpen { get => _searchOpen; set { if (Set(ref _searchOpen, value) && !value) { _hits = []; MarkHits(); SearchStatus = ""; } } }
    public string SearchText { get => _searchText; set => Set(ref _searchText, value); }
    public bool SearchMatchCase { get => _searchCase; set => Set(ref _searchCase, value); }
    public bool SearchRegex { get => _searchRegex; set => Set(ref _searchRegex, value); }
    public string SearchStatus { get => _searchStatus; private set => Set(ref _searchStatus, value); }

    public async Task SearchAsync()
    {
        if (string.IsNullOrEmpty(_searchText)) { _hits = []; MarkHits(); SearchStatus = ""; return; }
        var ids = new HashSet<long>();
        try
        {
            if (_session.Has(Capabilities.Network))
                foreach (var m in (await _session.SearchNetworkAsync(new NetworkSearchParams(_searchText, _searchCase, _searchRegex))).Matches) ids.Add(m.Id);
            foreach (var r in _rows.Values.Where(r => r.IsImported))
                if (MatchesImported(r.Request)) ids.Add(r.Id);
        }
        catch (Exception ex) { SearchStatus = ex.Message; return; }
        _hits = Rows.Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToList();
        MarkHits();
        SearchStatus = ids.Count == 0 ? "No matches" : $"{ids.Count} request{(ids.Count == 1 ? "" : "s")}" + (_hits.Count < ids.Count ? $" ({_hits.Count} listed)" : "");
        if (_hits.Count > 0) SelectHit(0);
    }

    bool MatchesImported(NetworkRequestDto r)
    {
        Func<string, bool> match;
        try
        {
            var rx = _searchRegex ? new Regex(_searchText, _searchCase ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)) : null;
            var cmp = _searchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            match = t => rx is not null ? rx.IsMatch(t) : t.Contains(_searchText, cmp);
        }
        catch (ArgumentException) { return false; }
        return r.RequestHeaders.Concat(r.ResponseHeaders).Any(h => match($"{h.Name}: {h.Value}"))
               || _importedBodies.Where(b => b.Key.Item1 == r.Id && b.Value.Encoding == "text").Any(b => match(b.Value.Content));
    }

    int _hitIndex = -1;

    void SelectHit(int i)
    {
        if (_hits.Count == 0) return;
        _hitIndex = ((i % _hits.Count) + _hits.Count) % _hits.Count;
        if (_rows.TryGetValue(_hits[_hitIndex], out var row)) Selected = row;
    }

    public void NextHit(int delta = 1) => SelectHit(_hitIndex + delta);

    void MarkHits()
    {
        _hitIndex = -1;
        var set = _hits.ToHashSet();
        foreach (var r in _rows.Values) r.IsMatch = set.Contains(r.Id);
    }

    /// <summary>Called when the panel is shown. First time: start recording (Chrome: records while DevTools is open).</summary>
    public async Task ActivateAsync()
    {
        if (_activated) return;
        _activated = true;
        if (!_session.Has(Capabilities.Network)) return;
        // Settings › Network: Chrome records while DevTools is open; turn this off to start recording by hand (Ctrl/Cmd+E).
        if (!Spyglass.App.Core.Settings.SettingsService.Shared.Current.AutoRecordNetwork) return;
        await SetRecordingAsync(true);
    }

    public Task ToggleRecordingAsync() => SetRecordingAsync(!IsRecording);

    async Task SetRecordingAsync(bool on)
    {
        try
        {
            var st = Spyglass.App.Core.Settings.SettingsService.Shared.Current;
            var status = on
                ? await _session.EnableNetworkAsync(new NetworkEnableParams(st.CaptureBodies, st.BodyLimitKb * 1024, st.CaptureInitiator))
                : await _session.DisableNetworkAsync();
            IsRecording = status.Enabled;
        }
        catch (Exception ex) { Summary = ex.Message; }
    }

    async Task ClearAsync()
    {
        _session.Network.Clear();
        _rows.Clear();
        _importedBodies.Clear();
        Rows.Clear();
        Selected = null;
        Notice = null;
        _hits = [];
        SetRange(null, null);
        try { await _session.ClearNetworkAsync(); } catch { /* disconnected */ }
        Refilter();
    }

    /// <summary>Type buttons: click selects one type; Ctrl/Cmd+click toggles several (Chrome).</summary>
    internal void ToggleType(TypeToggleViewModel t, bool additive)
    {
        if (t.Type is null || !additive)
        {
            foreach (var x in Types) x.IsOn = ReferenceEquals(x, t);
        }
        else
        {
            t.IsOn = !t.IsOn;
            Types[0].IsOn = Types.Skip(1).All(x => !x.IsOn);
        }
        Refilter();
    }

    void Apply(IReadOnlyList<NetworkRequestDto> updates)
    {
        foreach (var r in updates) Upsert(r);
        Refilter();
        if (_selected is not null && updates.Any(u => u.Id == _selected.Id)) _ = Details.LoadAsync(_selected.Request);
    }

    void Upsert(NetworkRequestDto r)
    {
        if (_rows.TryGetValue(r.Id, out var row)) row.Update(r);
        else _rows[r.Id] = new NetworkRowViewModel(r) { IsMatch = _hits.Contains(r.Id) }.WithCustom(_settings.CustomHeaders);
    }

    void Refilter()
    {
        var filter = new NetworkFilter(_filter);
        var types = Types.Where(t => t.IsOn && t.Type is not null).Select(t => t.Type!.Value).ToHashSet();
        bool Visible(NetworkRowViewModel r)
        {
            var text = filter.IsEmpty || filter.Matches(r.Request);
            if (_invert && !filter.IsEmpty) text = !text;
            return text && (types.Count == 0 || types.Contains(NetworkClassifier.TypeOf(r.Request)));
        }

        var now = DateTimeOffset.Now;
        // The overview shows everything the text/type filters let through; the selected range then narrows the table.
        var candidates = _rows.Values.Where(Visible).ToList();
        Overview = NetworkOverview.Build(candidates.Select(r => r.Request).ToList(), now);
        if (_rangeFrom is { } from && _rangeTo is { } to)
        {
            SelStartMs = (from - Overview.Origin).TotalMilliseconds;
            SelEndMs = (to - Overview.Origin).TotalMilliseconds;
        }
        else SelStartMs = SelEndMs = null;
        var inRange = candidates.Where(r => NetworkOverview.InRange(r.Request, _rangeFrom, _rangeTo, now)).ToDictionary(r => r.Request);
        var visible = NetworkSort.Sort(inRange.Keys, _sort).Select(r => inRange[r]).ToList();

        // Waterfall scale: from the first request's start to the latest end.
        if (visible.Count > 0)
        {
            var start = visible.Min(r => r.Request.StartTime);
            var end = visible.Max(r => r.Request.EndTime ?? now);
            var total = Math.Max(1, (end - start).TotalMilliseconds);
            foreach (var r in visible) r.Layout(start, total, WaterfallWidth);
        }
        if (!visible.SequenceEqual(Rows))
        {
            Rows.Clear();
            foreach (var r in visible) Rows.Add(r);
        }
        if (_selected is not null && !visible.Contains(_selected)) Selected = null;
        UpdateSummary(visible);
        Raise(nameof(IsEmpty));
        Raise(nameof(HasRows));
    }

    void UpdateSummary(List<NetworkRowViewModel> visible)
    {
        var all = _rows.Values.ToList();
        string Count(int shown, int total) => shown == total ? $"{total} requests" : $"{shown} / {total} requests";
        var resources = visible.Sum(r => r.Request.ResponseSize ?? 0);
        var finish = visible.Count == 0 ? "" :
            $"  │  Finish: {NetworkCopy.Duration(((visible.Max(r => r.Request.EndTime ?? r.Request.StartTime)) - visible.Min(r => r.Request.StartTime)).TotalMilliseconds)}";
        var imported = all.Count(r => r.IsImported) is > 0 and var n ? $"  │  {n} imported" : "";
        Summary = $"{Count(visible.Count, all.Count)}  │  {NetworkCopy.Size(resources)} resources{finish}{imported}";
    }

    public void Copy(string what)
    {
        if (_selected is null) return;
        _ = Details.CopyAsync(what);
    }

    // ---- HAR and "Copy all" (parity §7, §10) ----

    List<NetworkRequestDto> Requests(bool listedOnly) =>
        (listedOnly ? Rows.Select(r => r.Request) : _rows.Values.Select(r => r.Request)).OrderBy(r => r, Comparer<NetworkRequestDto>.Create(NetworkSort.Chronological)).ToList();

    async Task<Dictionary<(long, string), NetworkBodyResult>> BodiesFor(IReadOnlyList<NetworkRequestDto> requests)
    {
        var bodies = await HarExport.CollectBodiesAsync(_session, requests.Where(r => r.Id > 0));
        foreach (var r in requests.Where(r => r.Id < 0))
            foreach (var part in new[] { "request", "response" })
                if (_importedBodies.TryGetValue((r.Id, part), out var b)) bodies[(r.Id, part)] = b;
        return bodies;
    }

    /// <summary>HAR JSON for "Save/Copy all as HAR". Sanitized unless <paramref name="sensitive"/> (only honoured when the setting allows it).</summary>
    public async Task<string> BuildHarAsync(bool sensitive, bool listedOnly = false)
    {
        var requests = Requests(listedOnly);
        var bodies = await BodiesFor(requests);
        return HarConverter.Build(requests, bodies, new HarOptions(IncludeSensitive: sensitive && AllowSensitiveHar));
    }

    public async Task CopyAllAsync(string kind, bool listedOnly)
    {
        var requests = Requests(listedOnly);
        if (requests.Count == 0) return;
        if (kind is "har" or "har-sensitive") { CopyRequested?.Invoke(await BuildHarAsync(kind == "har-sensitive", listedOnly)); return; }
        var bodies = await BodiesFor(requests);
        CopyRequested?.Invoke(NetworkCopyAll.Join(kind, requests,
            r => bodies.TryGetValue((r.Id, "request"), out var b) && b.Encoding == "text" ? b.Content : null));
    }

    /// <summary>Imports a HAR as ordinary rows. Imported entries get their own (negative) ids and never touch the agent or the live recording log.</summary>
    public int ImportHar(string json)
    {
        HarImport import;
        try { import = HarConverter.Parse(json); }
        catch (FormatException ex) { Notice = "Import failed: " + ex.Message; return 0; }
        Notice = null;
        var map = import.Requests.ToDictionary(r => r.Id, _ => --_importSeq);
        foreach (var r in import.Requests)
        {
            var id = map[r.Id];
            var row = new NetworkRowViewModel(r with { Id = id, RedirectedFromId = r.RedirectedFromId is { } f && map.TryGetValue(f, out var nf) ? nf : null })
                .WithCustom(_settings.CustomHeaders);
            _rows[id] = row;
        }
        foreach (var ((oldId, part), body) in import.Bodies) _importedBodies[(map[oldId], part)] = body;
        Refilter();
        return import.Requests.Count;
    }

    public string HarFileName() => $"spyglass-{DateTime.Now:yyyyMMdd-HHmmss}.har";
}

public sealed class ColumnViewModel(NetworkColumn? column, string title, double width) : ObservableObject
{
    bool _visible = true, _asc, _desc;
    public NetworkColumn? Column { get; } = column;
    public string? Custom { get; init; }
    public string Title { get; } = title;
    public double Width { get; } = width;
    /// <summary>Row cells carry an 8 px right margin outside their width; the Waterfall cell is last and has none.</summary>
    public double OuterWidth => IsFlexible ? double.NaN : Column == NetworkColumn.Waterfall ? Width : Width + 8;
    /// <summary>Name has no fixed width (fills the row).</summary>
    public bool IsFlexible => Column == NetworkColumn.Name;
    public bool IsVisible { get => _visible; set => Set(ref _visible, value); }
    public bool IsAscending { get => _asc; private set => Set(ref _asc, value); }
    public bool IsDescending { get => _desc; private set => Set(ref _desc, value); }

    public void ApplySort(NetworkSortSpec s)
    {
        var active = Custom is not null ? s.Custom == Custom : s.Custom is null && Column is not null && s.Column == Column;
        IsAscending = active && !s.Descending;
        IsDescending = active && s.Descending;
    }
}

public sealed class TypeToggleViewModel(string label, ResourceType? type, NetworkViewModel owner) : ObservableObject
{
    bool _on;
    public string Label { get; } = label;
    public ResourceType? Type { get; } = type;
    public bool IsOn { get => _on; set { if (Set(ref _on, value)) Raise(nameof(Variant)); } }
    public TKCode.Arc.ButtonVariant Variant => IsOn ? TKCode.Arc.ButtonVariant.Primary : TKCode.Arc.ButtonVariant.Ghost;
    public void Click(bool additive) => owner.ToggleType(this, additive);
}

public sealed class NetworkRowViewModel : ObservableObject
{
    bool _selected, _match;
    double _left, _wait, _download;

    public NetworkRowViewModel(NetworkRequestDto r) => Request = r;

    public NetworkRequestDto Request { get; private set; }
    public long Id => Request.Id;
    public string Name => NetworkCopy.Name(Request);
    public string Url => Request.Url;
    public string Method => Request.Method;
    public string Status => NetworkCopy.Status(Request);
    public bool IsError => Request.Error is not null || Request.Status >= 400;
    public bool IsPending => Request.State == NetworkState.Pending;
    public double Opacity => IsPending ? 0.6 : 1;
    public string Type => NetworkClassifier.TypeLabel(Request);
    public string Initiator => NetworkCells.Initiator(Request);
    public string IdText => Id > 0 ? Id.ToString() : "";
    public string Protocol => NetworkCells.Protocol(Request);
    public string Scheme => NetworkCells.Scheme(Request);
    public string Domain => NetworkCells.Domain(Request);
    public string Remote => NetworkCells.Remote(Request);
    public string Connection => Request.ConnectionId?.ToString() ?? "";
    public string Cookies => NetworkCells.CookieCount(Request).ToString();
    public string SetCookies => NetworkCells.SetCookieCount(Request).ToString();
    /// <summary>Row came from an imported HAR (negative id); its bodies live in the panel, not in the agent.</summary>
    public bool IsImported => Id < 0;
    public bool IsMatch { get => _match; set => Set(ref _match, value); }
    public IReadOnlyList<string> CustomCells { get; private set; } = [];
    IReadOnlyList<string> _customNames = [];

    public NetworkRowViewModel WithCustom(IReadOnlyList<string> names) { SetCustomColumns(names); return this; }

    public void SetCustomColumns(IReadOnlyList<string> names)
    {
        _customNames = names.ToList();
        CustomCells = _customNames.Select(n => NetworkCells.ResponseHeader(Request, n)).ToList();
        Raise(nameof(CustomCells));
    }
    public string InitiatorTip => Request.Initiator is { } i ? string.Join("\n", i.Stack.Take(12)) : "";
    public string Size => Request.IsUpgrade ? "—" : NetworkCopy.Size(Request.ResponseSize);
    public string SizeTip => "Resource size (decoded). HttpClient decompresses before Spyglass sees the body, so the transferred size is not available.";
    public string Time => IsPending ? "(pending)" : NetworkCopy.Duration(Request.Timing?.Total ?? -1);
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }

    // Waterfall bar (pixels): lighter part = waiting, darker part = downloading (Chrome).
    public double BarLeft => _left;
    public double BarWait => _wait;
    public double BarDownload => _download;

    public void Update(NetworkRequestDto r)
    {
        Request = r;
        CustomCells = _customNames.Select(n => NetworkCells.ResponseHeader(r, n)).ToList();
        Raise(string.Empty);
    }

    public void Layout(DateTimeOffset start, double totalMs, double width)
    {
        var t = Request.Timing;
        var total = t?.Total ?? ((Request.EndTime ?? DateTimeOffset.Now) - Request.StartTime).TotalMilliseconds;
        var download = Math.Max(0, t?.Download ?? 0);
        _left = Math.Round((Request.StartTime - start).TotalMilliseconds / totalMs * width, 1);
        var bar = Math.Max(2, total / totalMs * width);
        _download = Math.Min(bar, download / totalMs * width);
        _wait = Math.Max(1, bar - _download);
        Raise(nameof(BarLeft));
        Raise(nameof(BarWait));
        Raise(nameof(BarDownload));
    }
}

/// <summary>Details tabs: Headers, Payload, Preview, Response, Initiator, Timing, Cookies (Chrome).</summary>
public sealed class NetworkDetailsViewModel(AgentSession session, Action<string> copy) : ObservableObject
{
    NetworkRequestDto? _r;
    string? _requestBody;
    /// <summary>Bodies of imported HAR rows (negative ids); the agent knows nothing about them.</summary>
    public Func<long, string, NetworkBodyResult?>? ImportedBody { get; init; }

    Task<NetworkBodyResult> Fetch(long id, string part) =>
        id < 0 ? Task.FromResult(ImportedBody?.Invoke(id, part) ?? throw new SpyglassProtocolException(-1, "This imported entry has no body in the HAR."))
               : session.GetNetworkBodyAsync(id, part);
    string? _responseText;
    string? _json;
    Bitmap? _image;
    string _responseLanguage = "text";
    int _version;

    public ObservableCollection<KeyValueViewModel> General { get; } = new();
    public ObservableCollection<HeaderRowViewModel> RequestHeaders { get; } = new();
    public ObservableCollection<HeaderRowViewModel> ResponseHeaders { get; } = new();
    public ObservableCollection<KeyValueViewModel> QueryParams { get; } = new();
    public ObservableCollection<TimingRowViewModel> Timing { get; } = new();
    public ObservableCollection<string> Stack { get; } = new();
    public ObservableCollection<KeyValueViewModel> Cookies { get; } = new();

    public string Title => _r is null ? "" : NetworkCopy.Name(_r);
    public bool Provisional => _r?.ProvisionalHeaders == true;
    public string? RequestBody => _requestBody;
    public bool HasRequestBody => !string.IsNullOrEmpty(_requestBody);
    public bool HasQuery => QueryParams.Count > 0;
    public string ResponseText => _responseText ?? "";
    public string ResponseLanguage => _responseLanguage;
    public string? Json => _json;
    public bool IsJson => _json is not null;
    public Bitmap? Image => _image;
    public bool IsImage => _image is not null;
    public bool IsTextPreview => !IsJson && !IsImage;
    public string InitiatorFrame => _r?.Initiator?.Frame ?? "Other (framework code)";
    public string TimingTotal => _r?.Timing is { } t ? $"Total  {NetworkCopy.Duration(t.Total)}" : "";
    public string Remote => _r?.RemoteAddress is { } a ? $"{a}{(_r.ConnectionId is { } c ? $"  ·  connection {c}" : "")}" : "—";
    public bool HasCookies => Cookies.Count > 0;

    public async Task LoadAsync(NetworkRequestDto? r)
    {
        var version = ++_version;
        _r = r;
        General.Clear(); RequestHeaders.Clear(); ResponseHeaders.Clear(); QueryParams.Clear(); Timing.Clear(); Stack.Clear(); Cookies.Clear();
        _requestBody = _responseText = _json = null;
        _image = null;
        if (r is not null)
        {
            General.Add(new("Request URL", r.Url));
            General.Add(new("Request Method", r.Method));
            General.Add(new("Status Code", r.Status is { } s ? $"{s} {r.StatusText}" : NetworkCopy.Status(r)));
            General.Add(new("Remote Address", r.RemoteAddress ?? "—"));
            if (r.HttpVersion is { } v) General.Add(new("Protocol", v));
            if (r.RedirectedFromId is { } from) General.Add(new("Redirected from", $"#{from}"));
            if (r.ErrorDetail is { } ed) General.Add(new("Error", ed));
            foreach (var h in r.ResponseHeaders.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)) ResponseHeaders.Add(new HeaderRowViewModel(h));
            foreach (var h in r.RequestHeaders.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)) RequestHeaders.Add(new HeaderRowViewModel(h));
            if (Uri.TryCreate(r.Url, UriKind.Absolute, out var u) && u.Query.Length > 1)
                foreach (var pair in u.Query[1..].Split('&'))
                {
                    var kv = pair.Split('=', 2);
                    QueryParams.Add(new(Uri.UnescapeDataString(kv[0]), kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : ""));
                }
            if (r.Timing is { } t)
            {
                var max = Math.Max(1, t.Total);
                void Phase(string name, double ms) { if (ms >= 0) Timing.Add(new TimingRowViewModel(name, ms, ms / max * 220)); }
                Phase("Queueing", t.Queueing); Phase("DNS Lookup", t.Dns); Phase("Initial connection", t.Connect); Phase("SSL", t.Ssl);
                Phase("Request sent", t.Send); Phase("Waiting for server response", t.Wait); Phase("Content Download", t.Download);
            }
            foreach (var f in r.Initiator?.Stack ?? []) Stack.Add(f);
            foreach (var c in NetworkFilter.RequestCookies(r)) Cookies.Add(new($"→ {c.Name}", c.Value));
            foreach (var c in NetworkFilter.SetCookies(r))
                Cookies.Add(new($"← {c.Name}", $"{c.Value}   {string.Join("  ", new[] { c.Domain, c.Path, c.Expires, c.HttpOnly ? "HttpOnly" : null, c.Secure ? "Secure" : null, c.SameSite }.Where(x => x is not null))}"));

            try
            {
                if (r.RequestBodySize > 0) _requestBody = Text(await Fetch(r.Id, "request"));
                if (r.State != NetworkState.Pending && r.ResponseSize > 0)
                {
                    var body = await Fetch(r.Id, "response");
                    if (version != _version) return;
                    if (body.Encoding == "base64" && r.MimeType?.StartsWith("image/") == true && r.MimeType != "image/svg+xml")
                    {
                        using var ms = new MemoryStream(Convert.FromBase64String(body.Content));
                        try { _image = new Bitmap(ms); } catch { /* not decodable */ }
                    }
                    _responseText = body.Encoding == "text" ? body.Content : $"(binary, {NetworkCopy.Size(body.TotalBytes)})";
                    if (body.Truncated) _responseText += $"\n\n… truncated by the agent ({NetworkCopy.Size(body.TotalBytes)} total)";
                    var trimmed = body.Encoding == "text" ? body.Content.TrimStart() : "";
                    if (r.MimeType?.Contains("json") == true || trimmed.StartsWith('{') || trimmed.StartsWith('[')) _json = body.Content;
                    _responseLanguage = r.MimeType switch
                    {
                        { } m when m.Contains("json") => "json",
                        { } m when m.Contains("xml") => "xml",
                        { } m when m.Contains("html") => "html",
                        { } m when m.Contains("javascript") => "js",
                        { } m when m.Contains("css") => "css",
                        _ => "text",
                    };
                }
            }
            catch (SpyglassProtocolException ex) { _responseText = ex.Message; }
        }
        if (version != _version) return;
        Raise(string.Empty);
    }

    static string Text(NetworkBodyResult b) => b.Encoding == "text" ? b.Content : $"(binary, {NetworkCopy.Size(b.TotalBytes)})";

    public async Task CopyAsync(string what)
    {
        if (_r is null) return;
        var body = _requestBody;
        if (body is null && _r.RequestBodySize > 0)
            try { body = Text(await Fetch(_r.Id, "request")); } catch { }
        copy(what switch
        {
            "url" => _r.Url,
            "curl" => NetworkCopy.Curl(_r, body),
            "powershell" => NetworkCopy.PowerShell(_r, body),
            "fetch" => NetworkCopy.Fetch(_r, body),
            "fetch-node" => NetworkCopy.Fetch(_r, body, node: true),
            "csharp" => NetworkCopy.CSharp(_r, body),
            "response" => _responseText ?? "",
            "stack" => string.Join("\n", _r.Initiator?.Stack ?? []),
            _ => _r.Url,
        });
    }
}

public sealed record KeyValueViewModel(string Key, string Value);

public sealed class HeaderRowViewModel : ObservableObject
{
    readonly HeaderDto _h;
    bool _revealed;
    public HeaderRowViewModel(HeaderDto h) { _h = h; RevealCommand = new RelayCommand(() => { _revealed = !_revealed; Raise(nameof(Value)); Raise(nameof(RevealText)); }); }
    public string Name => _h.Name;
    /// <summary>Credentials are masked until revealed (design/network §4).</summary>
    public string Value => _h.IsSensitive && !_revealed ? $"•••••• (length {_h.Value.Length})" : _h.Value;
    public bool IsSensitive => _h.IsSensitive;
    public string RevealText => _revealed ? "Hide" : "Show";
    public RelayCommand RevealCommand { get; }
}

public sealed record TimingRowViewModel(string Phase, double Ms, double BarWidth)
{
    public string Duration => NetworkCopy.Duration(Ms);
}
