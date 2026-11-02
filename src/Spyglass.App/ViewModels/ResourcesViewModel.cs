using System.Collections.ObjectModel;
using Avalonia.Media;
using Spyglass.App.Core;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.App.ViewModels;

/// <summary>
/// Resources page (design/ux/01 Resources): provider tree (Application, theme dictionaries, styles, and the selected
/// element's own and ancestor resources), the entries of the selected provider, live editing of existing values,
/// and "where does this key resolve from here" lookups with the provider chain.
/// </summary>
public sealed class ResourcesViewModel : ObservableObject
{
    readonly AgentSession _session;
    readonly ElementsViewModel _elements;
    ProviderNodeViewModel? _selectedProvider;
    EntryRowViewModel? _selectedEntry;
    List<EntryRowViewModel> _all = [];
    string _search = "";
    bool _isLoading;
    bool _isLoadingEntries;
    bool _includeElement = true;
    bool _truncated;
    string? _error;
    string _contextText = "";
    string _activeVariant = "";
    int? _contextElementId;
    CancellationTokenSource? _entriesCts;

    public ResourcesViewModel(AgentSession session, ElementsViewModel elements)
    {
        _session = session;
        _elements = elements;
        RefreshCommand = new RelayCommand(LoadAsync);
        ResetAllCommand = new RelayCommand(ResetAllAsync);
        CommitCommand = new RelayCommand(CommitTextAsync);
        ResetEntryCommand = new RelayCommand(ResetEntryAsync);
        ResolveCommand = new RelayCommand(() => LookupAsync());
        ResolveSelectedCommand = new RelayCommand(() =>
        {
            if (SelectedEntry is { } e) { LookupKey = e.Key; TabKey = "lookup"; return LookupAsync(); }
            return Task.CompletedTask;
        });
        ClearErrorCommand = new RelayCommand(() => Error = null);
    }

    /// <summary>Asks the view to expand the tree down to a node.</summary>
    public event Action<ProviderNodeViewModel>? RevealRequested;

    public ObservableCollection<ProviderNodeViewModel> Roots { get; } = new();
    public ObservableCollection<EntryRowViewModel> Entries { get; } = new();
    public ObservableCollection<LookupStepViewModel> Steps { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ResetAllCommand { get; }
    public RelayCommand CommitCommand { get; }
    public RelayCommand ResetEntryCommand { get; }
    public RelayCommand ResolveCommand { get; }
    public RelayCommand ResolveSelectedCommand { get; }
    public RelayCommand ClearErrorCommand { get; }

    // ---------- page state ----------

    public bool IsLoading { get => _isLoading; private set { if (Set(ref _isLoading, value)) RaiseStates(); } }
    public bool IsLoadingEntries { get => _isLoadingEntries; private set { if (Set(ref _isLoadingEntries, value)) RaiseStates(); } }
    public string? Error { get => _error; private set { if (Set(ref _error, value)) { Raise(nameof(HasError)); } } }
    public bool HasError => !string.IsNullOrEmpty(_error);
    public bool HasProviders => Roots.Count > 0;
    public bool NoProviders => !IsLoading && Roots.Count == 0;
    public bool NoSelection => SelectedProvider is null && Roots.Count > 0;
    public bool NoEntries => SelectedProvider is not null && !IsLoadingEntries && Entries.Count == 0;
    public bool HasEntries => Entries.Count > 0;
    public string EmptyEntriesTitle => _all.Count == 0 ? "This provider has no keys of its own" : "No keys match";
    public string EmptyEntriesText => _all.Count == 0
        ? "Its keys live in the dictionaries listed below it in the tree."
        : "Nothing in this provider contains that text in its key or value. Use Lookup to search the whole app.";

    void RaiseStates()
    {
        Raise(nameof(HasProviders)); Raise(nameof(NoProviders)); Raise(nameof(NoSelection));
        Raise(nameof(NoEntries)); Raise(nameof(HasEntries)); Raise(nameof(EmptyEntriesTitle)); Raise(nameof(EmptyEntriesText));
        Raise(nameof(CountText)); Raise(nameof(AnyModified));
    }

    /// <summary>Which element the "from element" providers and lookups start at; empty when none is selected in Elements.</summary>
    public string ContextText { get => _contextText; private set { if (Set(ref _contextText, value)) Raise(nameof(HasContext)); } }
    public bool HasContext => _elements.SelectedNode is not null;
    public string ActiveVariant { get => _activeVariant; private set => Set(ref _activeVariant, value); }

    public bool IncludeElement
    {
        get => _includeElement;
        set { if (Set(ref _includeElement, value)) _ = LoadAsync(); }
    }

    public string CountText => !HasEntries && _all.Count == 0 ? "" : Entries.Count == _all.Count ? $"{_all.Count} keys" : $"{Entries.Count} of {_all.Count} keys{(_truncated ? "+" : "")}";
    public bool AnyModified => _all.Any(e => e.IsModified);

    // ---------- provider tree ----------

    public async Task LoadAsync()
    {
        IsLoading = true;
        Error = null;
        var selectedId = _selectedProvider?.Id;
        try
        {
            _contextElementId = IncludeElement ? _elements.SelectedNode?.Id : null;
            ContextText = _elements.SelectedNode is { } n && IncludeElement ? n.Crumb : "";
            var r = await _session.GetResourceProvidersAsync(_contextElementId);
            ActiveVariant = r.ActiveVariant;
            var roots = new List<ProviderNodeViewModel>();
            foreach (var p in r.Providers) roots.Add(ProviderNodeViewModel.Build(p, null));
            Roots.Clear();
            foreach (var n2 in roots) Roots.Add(n2);
            var again = selectedId is { } id ? roots.SelectMany(x => x.Descendants()).FirstOrDefault(x => x.Id == id) : null;
            // Default: where the keys are (the first provider that has any), not an empty Application root.
            SelectedProvider = again ?? roots.SelectMany(x => x.Descendants()).FirstOrDefault(x => x.Dto.KeyCount > 0) ?? roots.FirstOrDefault();
            if (SelectedProvider is { } sel) RevealRequested?.Invoke(sel);
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsLoading = false; Raise(nameof(HasContext)); }
    }

    public ProviderNodeViewModel? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (!Set(ref _selectedProvider, value)) return;
            _ = LoadEntriesAsync();
        }
    }

    async Task LoadEntriesAsync()
    {
        _entriesCts?.Cancel();
        var cts = _entriesCts = new CancellationTokenSource();
        var provider = _selectedProvider;
        SelectedEntry = null;
        _all = [];
        Entries.Clear();
        RaiseStates();
        if (provider is null) return;
        IsLoadingEntries = true;
        try
        {
            var r = await _session.GetResourceEntriesAsync(provider.Id, ct: cts.Token);
            if (cts.IsCancellationRequested) return;
            _truncated = r.Truncated;
            _all = r.Entries.Select(e => new EntryRowViewModel(e, provider.Id)).ToList();
            foreach (var row in _all) { var rr = row; rr.SelectCommand = new RelayCommand(() => SelectedEntry = rr); }
            ApplyFilter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) Error = ex.Message; }
        finally { if (!cts.IsCancellationRequested) IsLoadingEntries = false; }
    }

    // ---------- entries ----------

    public string SearchText { get => _search; set { if (Set(ref _search, value)) ApplyFilter(); } }

    void ApplyFilter()
    {
        var q = _search.Trim();
        var keep = SelectedEntry?.Key;
        Entries.Clear();
        foreach (var e in _all)
            if (q.Length == 0 || e.Key.Contains(q, StringComparison.OrdinalIgnoreCase) || e.ValueText.Contains(q, StringComparison.OrdinalIgnoreCase))
                Entries.Add(e);
        RaiseStates();
    }

    public EntryRowViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            var old = _selectedEntry;
            if (!Set(ref _selectedEntry, value)) return;
            if (old is not null) old.IsSelected = false;
            if (value is not null) value.IsSelected = true;
            LoadEditor();
            Raise(nameof(HasSelectedEntry));
        }
    }

    public bool HasSelectedEntry => _selectedEntry is not null;

    // ---------- editing ----------

    string _editText = "";
    Color _editColor;
    bool _suppress;
    bool _setInFlight;
    string? _pendingColor;
    string? _editError;

    public string EditText { get => _editText; set => Set(ref _editText, value); }
    public string? EditError { get => _editError; private set { if (Set(ref _editError, value)) Raise(nameof(HasEditError)); } }
    public bool HasEditError => !string.IsNullOrEmpty(_editError);
    public bool CanEditText => SelectedEntry is { Editable: true };
    public bool CanEditColor => SelectedEntry is { Editable: true, EditableColor: not null };
    public string EditorTitle => SelectedEntry is { } e ? e.Key : "";
    public string EditorMeta => SelectedEntry is { } e ? $"{e.TypeText}{(e.VariantText is { Length: > 0 } v ? " · " + v : "")} · {(e.Editable ? "editable" : "read-only")}" : "";

    public Color EditColor
    {
        get => _editColor;
        set
        {
            if (!Set(ref _editColor, value) || _suppress || SelectedEntry is not { Editable: true } e) return;
            _ = SetColorAsync($"#{value.ToUInt32():x8}");
        }
    }

    void LoadEditor()
    {
        _suppress = true;
        EditError = null;
        EditText = SelectedEntry?.ValueText ?? "";
        EditColor = SelectedEntry?.EditableColor ?? default;
        _suppress = false;
        Raise(nameof(CanEditText)); Raise(nameof(CanEditColor)); Raise(nameof(EditorTitle)); Raise(nameof(EditorMeta));
    }

    Task CommitTextAsync() => SelectedEntry is { Editable: true } ? SetAsync(EditText) : Task.CompletedTask;

    /// <summary>Colour drags fire many changes: only one edit is in flight, the latest value follows.</summary>
    async Task SetColorAsync(string hex)
    {
        _pendingColor = hex;
        if (_setInFlight) return;
        _setInFlight = true;
        try
        {
            while (_pendingColor is { } next)
            {
                _pendingColor = null;
                await SetAsync(next, refreshEditor: false);
            }
        }
        finally { _setInFlight = false; }
    }

    async Task SetAsync(string text, bool refreshEditor = true)
    {
        if (SelectedEntry is not { Editable: true } row) return;
        try
        {
            EditError = null;
            var updated = await _session.SetResourceAsync(row.ProviderId, row.RawKey, new ValueDto(row.Kind, text));
            row.Update(updated);
            if (refreshEditor)
            {
                _suppress = true;
                EditText = row.ValueText;
                EditColor = row.EditableColor ?? EditColor;
                _suppress = false;
            }
            else { _suppress = true; EditText = row.ValueText; _suppress = false; }
            Raise(nameof(AnyModified));
        }
        catch (SpyglassProtocolException ex) { EditError = ex.Message; }
    }

    async Task ResetEntryAsync()
    {
        if (SelectedEntry is not { } row) return;
        try
        {
            await _session.ResetResourcesAsync(new ResourcesResetParams(row.ProviderId, row.RawKey));
            await ReloadEntryAsync(row);
        }
        catch (Exception ex) { EditError = ex.Message; }
    }

    async Task ResetAllAsync()
    {
        try
        {
            await _session.ResetResourcesAsync(new ResourcesResetParams(All: true));
            await LoadEntriesAsync();
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    async Task ReloadEntryAsync(EntryRowViewModel row)
    {
        var r = await _session.GetResourceEntriesAsync(row.ProviderId, query: row.RawKey);
        if (r.Entries.FirstOrDefault(e => e.Key == row.RawKey) is { } fresh) row.Update(fresh);
        if (ReferenceEquals(row, SelectedEntry)) LoadEditor();
        Raise(nameof(AnyModified));
    }

    // ---------- lookup ----------

    string _tab = "entries";
    string _lookupKey = "";
    string _lookupVariant = "active";
    string _lookupSummary = "";
    bool _lookupBusy;
    bool _showMisses = true;
    bool _lookupFromElement = true;
    List<LookupStepViewModel> _steps = [];

    /// <summary>"entries" or "lookup".</summary>
    public string TabKey { get => _tab; set { if (Set(ref _tab, value)) { Raise(nameof(ShowEntries)); Raise(nameof(ShowLookup)); } } }
    public bool ShowEntries => TabKey == "entries";
    public bool ShowLookup => TabKey == "lookup";
    public string LookupKey { get => _lookupKey; set => Set(ref _lookupKey, value); }
    public string LookupVariant { get => _lookupVariant; set => Set(ref _lookupVariant, value); }
    public string LookupSummary { get => _lookupSummary; private set { if (Set(ref _lookupSummary, value)) Raise(nameof(HasLookup)); } }
    public bool HasLookup => !string.IsNullOrEmpty(_lookupSummary);
    public bool LookupBusy { get => _lookupBusy; private set => Set(ref _lookupBusy, value); }
    public bool ShowMisses { get => _showMisses; set { if (Set(ref _showMisses, value)) FilterSteps(); } }
    public bool LookupFromElement { get => _lookupFromElement; set => Set(ref _lookupFromElement, value); }
    public string LookupStart => _elements.SelectedNode is { } n ? $"Start at {n.Crumb}" : "Start at the application (no element selected)";
    public bool CanStartAtElement => _elements.SelectedNode is not null;

    /// <summary>Called when the page opens: refresh what the lookup can start from.</summary>
    public void NotifySelectionChanged()
    {
        Raise(nameof(LookupStart)); Raise(nameof(CanStartAtElement));
    }

    async Task LookupAsync()
    {
        var key = LookupKey.Trim();
        if (key.Length == 0) return;
        LookupBusy = true;
        try
        {
            int? from = LookupFromElement ? _elements.SelectedNode?.Id : null;
            var variant = LookupVariant == "active" ? null : LookupVariant;
            var r = await _session.LookupResourceAsync(key, from, variant);
            _steps = r.Chain.Select(s => new LookupStepViewModel(s)).ToList();
            var hits = r.Chain.Count(c => c.Hit);
            var winner = r.Chain.FirstOrDefault(c => c.Winner);
            LookupSummary = r.Found
                ? $"{key} → {r.Value?.Text} ({TypeName(r.Value)}) in the {r.Variant} theme" + (winner is not null ? $", defined in {winner.Path}" : "")
                  + (hits > 1 ? $"; {hits - 1} more definition{(hits == 2 ? "" : "s")} hidden." : ".")
                : $"{key} is not defined for the {r.Variant} theme from here.";
            if (r.Note is { } note) LookupSummary += "  " + note;
            if (r.Truncated) LookupSummary += "  (chain truncated)";
            FilterSteps();
        }
        catch (Exception ex) { LookupSummary = ex.Message; _steps = []; FilterSteps(); }
        finally { LookupBusy = false; }
    }

    void FilterSteps()
    {
        Steps.Clear();
        foreach (var s in _steps) if (ShowMisses || s.Hit) Steps.Add(s);
        Raise(nameof(HasSteps));
    }

    public bool HasSteps => Steps.Count > 0;

    internal static string TypeName(ValueDto? v) => v?.Type is { } t ? t[(t.LastIndexOf('.') + 1)..] : v?.Kind.ToString() ?? "";
}

public sealed class ProviderNodeViewModel(ResourceProviderDto dto, ProviderNodeViewModel? parent) : ObservableObject
{
    public int Id { get; } = dto.Id;
    public ResourceProviderDto Dto { get; } = dto;
    public ProviderNodeViewModel? Parent { get; } = parent;
    public ObservableCollection<ProviderNodeViewModel> Children { get; } = new();

    /// <summary>Root nodes say what they are ("Application", "Element"); the rest show their own name.</summary>
    public string Label { get; } = dto.Kind switch
    {
        "application" => "Application · " + dto.Name,
        "element" => dto.Name,
        _ => dto.Name,
    };
    public string KindText { get; } = dto.Kind switch
    {
        "element" => "element",
        "themeDictionary" => "theme",
        "dictionary" => "merged",
        "styles" => "styles",
        "style" => "style",
        _ => "",
    };
    public string CountText { get; } = dto.KeyCount > 0 ? dto.KeyCount.ToString() : "";
    public string Tooltip { get; } = dto.Source is { } s ? $"{s.Uri}:{s.Line}" : dto.Name;
    /// <summary>Second line of the entries header: where it is declared, or what kind of provider it is.</summary>
    public string SubText { get; } = dto.Source is { } s ? $"{s.Uri[(s.Uri.LastIndexOfAny(['/', '\\']) + 1)..]}:{s.Line}" : dto.Kind switch
    {
        "themeDictionary" => $"theme dictionary · {dto.Variant}",
        "dictionary" => "merged dictionary",
        "styles" => "styles",
        "style" => "style",
        "element" => "element resources",
        _ => "application resources",
    };
    public override string ToString() => Label;

    public static ProviderNodeViewModel Build(ResourceProviderDto dto, ProviderNodeViewModel? parent)
    {
        var node = new ProviderNodeViewModel(dto, parent);
        foreach (var c in dto.Children ?? []) node.Children.Add(Build(c, node));
        return node;
    }

    public IEnumerable<ProviderNodeViewModel> Descendants()
    {
        yield return this;
        foreach (var c in Children) foreach (var d in c.Descendants()) yield return d;
    }
}

public sealed class EntryRowViewModel : ObservableObject
{
    ResourceEntryDto _dto;

    public EntryRowViewModel(ResourceEntryDto dto, int providerId)
    {
        _dto = dto;
        ProviderId = providerId;
    }

    bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public RelayCommand? SelectCommand { get; set; }
    public int ProviderId { get; }
    public string Key => _dto.Key;
    /// <summary>The key as the agent expects it for resources/set and reset (only string keys are editable).</summary>
    public string RawKey => _dto.Key;
    public ValueKind Kind => _dto.Value.Kind;
    public string TypeText => ResourcesViewModel.TypeName(_dto.Value);
    public string ValueText => _dto.Reference is { } r ? $"{r} = {_dto.Value.Text}" : _dto.Value.Text;
    public string? VariantText => _dto.Variant;
    public bool Editable => _dto.Editable;
    public bool IsModified => _dto.IsModifiedBySpyglass;
    public string SourceText => _dto.Source is { } s ? $"{FileName(s.Uri)}:{s.Line}" : "";
    public string? SourceTip => _dto.Source is { } s ? $"{s.Uri}:{s.Line}:{s.Column}" : null;

    /// <summary>The colour data of a Color or solid-colour brush value (null for anything else). Data is the app's, not UI chrome.</summary>
    public Color? EditableColor => ColorOf(_dto.Value);
    public IBrush? Swatch => EditableColor is { } c ? new SolidColorBrush(c) : null;
    public bool HasSwatch => EditableColor is not null;

    public void Update(ResourceEntryDto dto)
    {
        _dto = dto;
        Raise(nameof(ValueText)); Raise(nameof(IsModified)); Raise(nameof(Swatch)); Raise(nameof(HasSwatch)); Raise(nameof(EditableColor)); Raise(nameof(Editable));
    }

    public static Color? ColorOf(ValueDto v)
    {
        try
        {
            if (v.Kind == ValueKind.Color && v.Data is not null) { var c = SpyglassJson.Read<ColorData>(v.Data); return Color.FromArgb(c.A, c.R, c.G, c.B); }
            if (v.Kind == ValueKind.Brush && v.Data is not null && SpyglassJson.Read<BrushData>(v.Data).Color is { } b) return Color.FromArgb(b.A, b.R, b.G, b.B);
        }
        catch { /* malformed data: no swatch */ }
        return null;
    }

    static string FileName(string uri)
    {
        var i = uri.LastIndexOfAny(['/', '\\']);
        return i >= 0 ? uri[(i + 1)..] : uri;
    }
}

public sealed class LookupStepViewModel(ResourceStepDto dto)
{
    public bool Hit { get; } = dto.Hit;
    public string Path { get; } = dto.Path + (dto.Provider.Kind == "element" ? $" [e{dto.Provider.Id}]" : "");
    public string StatusText { get; } = dto.Winner ? "used" : dto.Shadowed ? "hidden" : "miss";
    public TKCode.Arc.BadgeTone Tone { get; } = dto.Winner ? TKCode.Arc.BadgeTone.Success : dto.Shadowed ? TKCode.Arc.BadgeTone.Warning : TKCode.Arc.BadgeTone.Neutral;
    public string ValueText { get; } = dto.Hit ? $"{dto.Value?.Text}  ({ResourcesViewModel.TypeName(dto.Value)})" : "";
    public string SourceText { get; } = dto.Source is { } s ? $"{s.Uri[(s.Uri.LastIndexOfAny(['/', '\\']) + 1)..]}:{s.Line}" : "";
    public IBrush? Swatch { get; } = dto.Value is { } v && EntryRowViewModel.ColorOf(v) is { } c ? new SolidColorBrush(c) : null;
    public bool HasSwatch => Swatch is not null;
    public double Opacity { get; } = dto.Hit ? 1 : 0.55;
}
