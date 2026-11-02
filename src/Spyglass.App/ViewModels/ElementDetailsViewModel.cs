using System.Collections.ObjectModel;
using System.Globalization;
using Spyglass.App.Core;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;
using TKCode.Arc;

namespace Spyglass.App.ViewModels;

/// <summary>Details pane: Properties / Layout / Styles tabs (wireframes W2, W2-a, W2-b).</summary>
public sealed class ElementDetailsViewModel(AgentSession session) : ObservableObject
{
    List<PropertyRowViewModel> _all = new();
    string _filter = "";
    bool _onlySet = true;
    int? _id;
    string _header = "";
    string _source = "";
    string _dataContext = "";
    LayoutResult? _layout;
    int _loadVersion;

    public ObservableCollection<PropertyRowViewModel> Properties { get; } = new();
    public ObservableCollection<string> Classes { get; } = new();
    public ObservableCollection<PseudoClassViewModel> PseudoClasses { get; } = new();
    public ObservableCollection<ValueFrameViewModel> Frames { get; } = new();
    public bool HasValueFrames => session.Has(Capabilities.ValueFrames);
    public bool NoValueFrames => !HasValueFrames;

    public string Header { get => _header; private set => Set(ref _header, value); }
    public string Source { get => _source; private set => Set(ref _source, value); }
    public string DataContext { get => _dataContext; private set => Set(ref _dataContext, value); }

    public string Filter { get => _filter; set { if (Set(ref _filter, value)) ApplyFilter(); } }
    public bool OnlySet { get => _onlySet; set { if (Set(ref _onlySet, value)) ApplyFilter(); } }

    // ---- Layout tab ----
    public string MarginText => T(_layout?.Margin);
    public string BorderText => T(_layout?.BorderThickness);
    public string PaddingText => T(_layout?.Padding);
    public string SizeText => _layout is { } l ? $"{N(l.Bounds.W)} × {N(l.Bounds.H)}" : "";
    public string BoundsText => _layout is { } l ? $"X {N(l.Bounds.X)}   Y {N(l.Bounds.Y)}   W {N(l.Bounds.W)}   H {N(l.Bounds.H)}" : "";
    public string DesiredText => _layout is { } l ? $"{N(l.DesiredSize.W)} × {N(l.DesiredSize.H)}" : "";
    public string ConstraintsText => _layout is { } l
        ? $"Width {O(l.Width)} · Min {O(l.MinWidth)} · Max {O(l.MaxWidth)}   |   Height {O(l.Height)} · Min {O(l.MinHeight)} · Max {O(l.MaxHeight)}" +
          (l.ActiveConstraints.Count > 0 ? $"   (active: {string.Join(", ", l.ActiveConstraints)})" : "")
        : "";
    public string AlignmentText => _layout is { } l ? $"{l.HorizontalAlignment} / {l.VerticalAlignment}" : "";

    public async Task LoadAsync(int id)
    {
        var version = ++_loadVersion;
        _id = id;
        try
        {
            var describe = await session.DescribeAsync(id);
            var props = await session.GetPropertiesAsync(id);
            var layout = await session.GetLayoutAsync(id);
            var classes = await session.GetClassesAsync(id);
            var frames = HasValueFrames ? (await session.GetValueFramesAsync(id)).Frames : [];
            if (version != _loadVersion) return;   // a newer selection won

            Header = describe.Selector.Split(" > ").Last();
            Source = describe.Source is { } s ? $"{Path.GetFileName(s.Uri)}:{s.Line}" : "";
            DataContext = describe.DataContextType ?? "";
            _all = props.Select(p => new PropertyRowViewModel(p, this)).ToList();
            ApplyFilter();
            _layout = layout;
            foreach (var n in new[] { nameof(MarginText), nameof(BorderText), nameof(PaddingText), nameof(SizeText), nameof(BoundsText),
                                      nameof(DesiredText), nameof(ConstraintsText), nameof(AlignmentText) }) Raise(n);
            Classes.Clear();
            foreach (var c in classes.Classes) Classes.Add("." + c);
            Frames.Clear();
            // Active frames first (the cascade that applies), then conditional styles whose condition is not met.
            foreach (var f in frames.Where(f => f.Setters.Count > 0).OrderBy(f => f.IsActive ? 0 : 1))
                Frames.Add(new ValueFrameViewModel(f));
            PseudoClasses.Clear();
            foreach (var pc in classes.KnownPseudoClasses)
                PseudoClasses.Add(new PseudoClassViewModel(pc, classes.PseudoClasses.Contains(pc), this));
        }
        catch (Exception ex) { Header = ex.Message; }
    }

    void ApplyFilter()
    {
        Properties.Clear();
        foreach (var p in _all.Where(p => (!_onlySet || p.IsSet || p.IsModified) &&
                                          (_filter.Length == 0 || p.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase))))
            Properties.Add(p);
    }

    internal async Task<PropertyDto?> SetAsync(PropertyRowViewModel row, ValueDto value)
    {
        if (_id is not { } id) return null;
        return await session.SetPropertyAsync(id, row.Name, row.Owner, value);
    }

    internal async Task ResetAsync(PropertyRowViewModel row)
    {
        if (_id is not { } id) return;
        await session.ResetPropertiesAsync(new PropsResetParams(Id: id, Name: row.Name, Owner: row.Owner));
        await LoadAsync(id);
    }

    internal async Task SetPseudoClassAsync(string pc, bool active)
    {
        if (_id is not { } id) return;
        await session.SetPseudoClassAsync(id, pc, active);
        await LoadAsync(id);
    }

    static string N(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
    static string O(double? v) => v is { } x ? N(x) : "–";
    static string T(ThicknessData? t) => t is null ? "–" : t.L == t.T && t.T == t.R && t.R == t.B ? N(t.L) : $"{N(t.L)},{N(t.T)},{N(t.R)},{N(t.B)}";
}

public sealed class PropertyRowViewModel : ObservableObject
{
    readonly ElementDetailsViewModel _owner;
    PropertyDto _dto;
    string _editText;
    string? _error;

    public PropertyRowViewModel(PropertyDto dto, ElementDetailsViewModel owner)
    {
        _dto = dto;
        _owner = owner;
        _editText = dto.Value.Text;
        CommitCommand = new RelayCommand(CommitAsync);
        ResetCommand = new RelayCommand(() => _owner.ResetAsync(this));
    }

    public string Name => _dto.Name;
    public string Owner => _dto.Owner;
    public string Priority => _dto.Priority;
    public bool IsSet => _dto.IsSet;
    public bool IsModified => _dto.IsModifiedBySpyglass;
    public bool HasBinding => _dto.HasBinding;
    public string Tooltip => $"{_dto.Owner}.{_dto.Name} · {_dto.Value.Type ?? _dto.Value.Kind.ToString()}{(HasBinding ? " · bound" : "")}";
    public bool IsBool => _dto.Value.Kind == ValueKind.Bool && !_dto.IsReadOnly;
    public bool IsText => !IsBool;
    /// <summary>Editable text value: gets the compact input. Everything else (read-only, computed, objects) is plain text.</summary>
    public bool IsEditable => !IsBool && !IsReadOnly;
    public bool IsPlain => !IsBool && IsReadOnly;
    public bool IsReadOnly => _dto.IsReadOnly || _dto.Value.Kind is ValueKind.Object or ValueKind.Collection;
    public RelayCommand CommitCommand { get; }
    public RelayCommand ResetCommand { get; }

    public BadgeTone PriorityTone => _dto.Priority switch
    {
        "LocalValue" => BadgeTone.Info,
        "Animation" => BadgeTone.Warning,
        "StyleTrigger" or "Style" => BadgeTone.Success,
        _ => BadgeTone.Neutral,
    };

    public string EditText { get => _editText; set => Set(ref _editText, value); }
    public string? Error { get => _error; private set { Set(ref _error, value); Raise(nameof(HasError)); } }
    public bool HasError => _error is not null;

    public bool BoolValue
    {
        get => _dto.Value.Text == "True";
        set
        {
            if (value == BoolValue) return;
            _ = SetValueAsync(new ValueDto(ValueKind.Bool, value ? "True" : "False"));
        }
    }

    async Task CommitAsync()
    {
        if (IsReadOnly || _editText == _dto.Value.Text) return;
        await SetValueAsync(new ValueDto(_dto.Value.Kind == ValueKind.Unset ? ValueKind.String : _dto.Value.Kind, _editText));
    }

    async Task SetValueAsync(ValueDto value)
    {
        try
        {
            if (await _owner.SetAsync(this, value) is { } updated) Update(updated);
            Error = null;
        }
        catch (SpyglassProtocolException ex)
        {
            Error = ex.Message;
            EditText = _dto.Value.Text;   // roll back the editor
        }
    }

    void Update(PropertyDto dto)
    {
        _dto = dto;
        _editText = dto.Value.Text;
        Raise(string.Empty);
    }
}

public sealed class PseudoClassViewModel(string name, bool active, ElementDetailsViewModel owner) : ObservableObject
{
    bool _active = active;
    public string Name { get; } = name;
    public bool IsActive
    {
        get => _active;
        set { if (Set(ref _active, value)) _ = owner.SetPseudoClassAsync(Name, value); }
    }
}

public sealed class ValueFrameViewModel(ValueFrameDto f)
{
    public string Title { get; } = f.Source.Kind switch
    {
        FrameSourceKind.Style => f.Source.Selector ?? "Style",
        FrameSourceKind.ControlTheme => $"ControlTheme · {f.Source.TargetType}",
        FrameSourceKind.Local => "Local values",
        FrameSourceKind.Template => $"Template · {f.Source.TargetType}",
        _ => f.Type,
    };
    public string Meta { get; } = string.Join("  ·  ", new[]
    {
        f.IsActive ? "active" : "inactive",
        f.Priority,
        f.Source.Location is { } l ? $"{Path.GetFileName(l.Uri)}:{l.Line}" : null,
    }.Where(x => x is not null));
    public double Opacity { get; } = f.IsActive ? 1 : 0.5;
    public IReadOnlyList<SetterViewModel> Setters { get; } = f.Setters.Select(s => new SetterViewModel(s)).ToList();
}

public sealed class SetterViewModel(SetterDto s)
{
    public string Property { get; } = s.Property;
    public string Value { get; } = s.Value.Text;
    public bool IsOverridden { get; } = s.IsOverridden;
    /// <summary>Overridden setters are struck through, like Chrome's Styles pane.</summary>
    public Avalonia.Media.TextDecorationCollection? Decorations { get; } = s.IsOverridden ? Avalonia.Media.TextDecorations.Strikethrough : null;
    public double Opacity { get; } = s.IsOverridden ? 0.55 : 1;
}
