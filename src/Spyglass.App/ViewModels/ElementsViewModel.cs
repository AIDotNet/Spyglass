using System.Collections.ObjectModel;
using Avalonia.Threading;
using Spyglass.App.Core;
using Spyglass.App.Localization;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.App.ViewModels;

/// <summary>Elements page (wireframe W2): tree, search, pick, selection details.</summary>
public sealed class ElementsViewModel : ObservableObject
{
    readonly AgentSession _session;
    readonly Dictionary<int, ElementNodeViewModel> _byId = new();
    ElementNodeViewModel? _selected;
    string _treeKind = "visual";
    bool _showTemplateParts = Spyglass.App.Core.Settings.SettingsService.Shared.Current.ShowTemplateParts;
    string _search = "";
    string _searchStatus = "";
    bool _isPicking;
    bool _isLoading;
    CancellationTokenSource? _searchCts;
    IReadOnlyList<int> _matches = [];
    int _matchIndex;

    public ElementsViewModel(AgentSession session)
    {
        _session = session;
        Details = new ElementDetailsViewModel(session);
        RefreshCommand = new RelayCommand(LoadAsync);
        PickCommand = new RelayCommand(TogglePickAsync);
        NextMatchCommand = new RelayCommand(() => MoveMatch(+1));
        session.PickSelected += p => Dispatcher.UIThread.Post(() => _ = OnPickedAsync(p));
        session.PickStarted += () => Dispatcher.UIThread.Post(() => IsPicking = true);
        session.TreeChanged += p => Dispatcher.UIThread.Post(() => ApplyChanges(p));
        WatchLanguage();
    }

    public ObservableCollection<ElementNodeViewModel> Roots { get; } = new();
    public ObservableCollection<ElementNodeViewModel> Breadcrumb { get; } = new();
    public ElementDetailsViewModel Details { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand PickCommand { get; }
    public RelayCommand NextMatchCommand { get; }

    /// <summary>"Screenshot" of the selected element (button, palette): captures a PNG and hands it to the view, which asks where to save it.</summary>
    public RelayCommand ScreenshotCommand => _screenshot ??= new RelayCommand(CaptureScreenshotAsync);
    RelayCommand? _screenshot;
    public event Action<ElementScreenshot>? ScreenshotCaptured;

    async Task CaptureScreenshotAsync()
    {
        if (_selected is not { } node) { Details.ReportError(Loc.T("Elements.ScreenshotNoSelection")); return; }
        try
        {
            var shot = await _session.ScreenshotAsync(node.Id, 2);   // 2x: crisp on high-DPI screens
            Details.ReportError("");
            var name = new string((node.Crumb + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
            ScreenshotCaptured?.Invoke(new ElementScreenshot(Convert.FromBase64String(shot.Base64), name + ".png"));
        }
        catch (Exception ex) { Details.ReportError(ex.Message); }
    }

    /// <summary>Asks the view to expand to and scroll to a node.</summary>
    public event Action<ElementNodeViewModel>? RevealRequested;

    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    int _detailsTab;
    /// <summary>Selected details tab (0 Properties, 1 Layout, 2 Styles).</summary>
    public int DetailsTab { get => _detailsTab; set => Set(ref _detailsTab, value); }
    public bool IsPicking
    {
        get => _isPicking;
        private set { if (Set(ref _isPicking, value)) { Raise(nameof(PickLabel)); Raise(nameof(PickVariant)); } }
    }

    protected internal override void OnLanguageChanged() { Raise(nameof(PickLabel)); Raise(nameof(SearchStatus)); }

    public string PickLabel => IsPicking ? Loc.T("Elements.Picking") : Loc.T("Elements.PickElement");
    public TKCode.Arc.ButtonVariant PickVariant => IsPicking ? TKCode.Arc.ButtonVariant.Primary : TKCode.Arc.ButtonVariant.Secondary;

    public string TreeKind
    {
        get => _treeKind;
        set { if (Set(ref _treeKind, value)) _ = LoadAsync(); }
    }

    public bool ShowTemplateParts
    {
        get => _showTemplateParts;
        set { if (Set(ref _showTemplateParts, value)) _ = LoadAsync(); }
    }

    public string SearchText
    {
        get => _search;
        set { if (Set(ref _search, value)) _ = SearchAsync(value); }
    }

    bool _noMatches;
    public string SearchStatus { get => _noMatches ? Loc.T("Common.NoMatches") : _searchStatus; private set => Set(ref _searchStatus, value); }

    public ElementNodeViewModel? SelectedNode
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Breadcrumb.Clear();
            for (var n = value; n is not null; n = n.Parent) Breadcrumb.Insert(0, n);
            if (value is not null)
            {
                _ = Details.LoadAsync(value.Id);
                _ = Safe(() => _session.HighlightAsync(value.Id, durationMs: Spyglass.App.Core.Settings.SettingsService.Shared.Current.KeepHighlight ? 0 : 1500));
            }
        }
    }

    Protocol.Messages.TreeKind Kind => _treeKind == "logical" ? Protocol.Messages.TreeKind.Logical : Protocol.Messages.TreeKind.Visual;

    public async Task LoadAsync()
    {
        IsLoading = true;
        var selectedId = _selected?.Id;
        try
        {
            var roots = (await _session.GetRootsAsync()).Roots;
            var built = new List<ElementNodeViewModel>();
            _byId.Clear();
            foreach (var r in roots)
            {
                var tree = await _session.GetTreeAsync(r.Node.Id, Kind);
                var vm = Build(tree, null, r);
                if (vm is not null) built.Add(vm);
            }
            Roots.Clear();
            foreach (var r in built) Roots.Add(r);
            if (selectedId is { } id && _byId.TryGetValue(id, out var again)) Select(again);
            else if (Roots.Count > 0 && _selected is null) SelectedNode = Roots[0];
        }
        catch (Exception ex) { _noMatches = false; SearchStatus = ex.Message; }
        finally { IsLoading = false; }
    }

    ElementNodeViewModel? Build(NodeDto dto, ElementNodeViewModel? parent, RootInfo? root = null)
    {
        var vm = new ElementNodeViewModel(dto, parent, root);
        _byId[dto.Id] = vm;
        foreach (var child in dto.Children ?? [])
        {
            if (!_showTemplateParts && child.IsTemplatePart)
            {
                // Hide template parts but keep the user content they host (e.g. a ContentPresenter's child).
                foreach (var lifted in LiftedChildren(child)) AddChild(vm, lifted);
                continue;
            }
            AddChild(vm, child);
        }
        return vm;
    }

    void AddChild(ElementNodeViewModel parent, NodeDto child)
    {
        if (Build(child, parent) is { } c) parent.Children.Add(c);
    }

    static IEnumerable<NodeDto> LiftedChildren(NodeDto templatePart) =>
        (templatePart.Children ?? []).SelectMany(c => c.IsTemplatePart ? LiftedChildren(c) : [c]);

    // ---------- incremental updates (tree/changed) ----------

    /// <summary>
    /// Applies the agent's add/remove/update ops in place instead of reloading the whole tree. Windows and popups
    /// opening or closing still reload (their roots have to be fetched). A change involving template parts while they
    /// are hidden also reloads, because their user content has to be lifted into the parent.
    /// </summary>
    void ApplyChanges(TreeChangedParams p)
    {
        if (p.Ops.Any(o => o is TreeOpRootAdded or TreeOpRootRemoved)) { _ = LoadAsync(); return; }
        var reload = false;
        foreach (var op in p.Ops)
        {
            switch (op)
            {
                case TreeOpUpdate u when _byId.TryGetValue(u.Id, out var node):
                    node.Update(u.Name, u.Classes, u.IsVisible);   // id-based: applies to whichever tree is shown
                    break;
                case TreeOpRemove r when p.Tree == Kind:
                    if (_byId.TryGetValue(r.Id, out var gone)) RemoveNode(gone);
                    break;
                case TreeOpAdd a when p.Tree == Kind:
                    if (!_byId.TryGetValue(a.ParentId, out var parent)) break;       // parent not shown (collapsed template part etc.)
                    if (a.Node.IsTemplatePart && !_showTemplateParts) { reload = true; break; }
                    var child = Build(a.Node, parent);
                    if (child is not null) parent.Children.Insert(Math.Min(a.Index, parent.Children.Count), child);
                    break;
            }
        }
        if (reload) _ = LoadAsync();
    }

    void RemoveNode(ElementNodeViewModel node)
    {
        var selectedGone = _selected is not null && IsInside(_selected, node);
        foreach (var d in Descendants(node)) _byId.Remove(d.Id);
        node.Parent?.Children.Remove(node);
        if (selectedGone) SelectedNode = node.Parent;   // the selected element was removed: fall back to its parent
    }

    static bool IsInside(ElementNodeViewModel n, ElementNodeViewModel ancestor)
    {
        for (var p = n; p is not null; p = p.Parent) if (ReferenceEquals(p, ancestor)) return true;
        return false;
    }

    static IEnumerable<ElementNodeViewModel> Descendants(ElementNodeViewModel n)
    {
        yield return n;
        foreach (var c in n.Children) foreach (var d in Descendants(c)) yield return d;
    }

    public void Select(ElementNodeViewModel node)
    {
        SelectedNode = node;
        RevealRequested?.Invoke(node);
    }

    public void SelectById(int id)
    {
        if (_byId.TryGetValue(id, out var n)) Select(n);
    }

    async Task TogglePickAsync()
    {
        if (IsPicking)
        {
            IsPicking = false;
            await Safe(() => _session.StopPickAsync());
            return;
        }
        IsPicking = true;
        await Safe(() => _session.StartPickAsync());
    }

    async Task OnPickedAsync(PickSelectedParams p)
    {
        IsPicking = false;
        if (p.Id is not { } id) return;
        if (!_byId.ContainsKey(id)) await LoadAsync();   // picked something the cached tree does not have (new popup…)
        if (_byId.TryGetValue(id, out var node)) Select(node);
        else if (p.Path is { } path && path.LastOrDefault(_byId.ContainsKey) is var nearest && nearest != 0 && _byId.TryGetValue(nearest, out var anc))
            Select(anc);   // the picked element is hidden (template part): select its nearest visible ancestor
    }

    async Task SearchAsync(string text)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        if (string.IsNullOrWhiteSpace(text)) { _matches = []; _noMatches = false; SearchStatus = ""; return; }
        try
        {
            await Task.Delay(250, cts.Token);
            var query = text.Trim();
            SearchResult r;
            try { r = await _session.SearchAsync(new SearchParams(query, Kind, 500, _showTemplateParts), cts.Token); }
            catch (SpyglassProtocolException) { r = await _session.SearchAsync(new SearchParams("text=" + query, Kind, 500, _showTemplateParts), cts.Token); }
            if (r.Matches.Count == 0 && !query.StartsWith("text=", StringComparison.OrdinalIgnoreCase))
                r = await _session.SearchAsync(new SearchParams("text=" + query, Kind, 500, _showTemplateParts), cts.Token);
            if (cts.IsCancellationRequested) return;
            _matches = r.Matches.Select(m => m.Id).Where(_byId.ContainsKey).ToList();
            _matchIndex = 0;
            _noMatches = _matches.Count == 0;
            SearchStatus = _noMatches ? "" : $"1 / {_matches.Count}{(r.Truncated ? "+" : "")}";
            Raise(nameof(SearchStatus));
            if (_matches.Count > 0) SelectById(_matches[0]);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SearchStatus = ex.Message; }
    }

    void MoveMatch(int delta)
    {
        if (_matches.Count == 0) return;
        _matchIndex = (_matchIndex + delta + _matches.Count) % _matches.Count;
        _noMatches = false;
        SearchStatus = $"{_matchIndex + 1} / {_matches.Count}";
        SelectById(_matches[_matchIndex]);
    }

    static async Task Safe(Func<Task> f)
    {
        try { await f(); } catch { /* agent gone or element removed; the UI recovers on next load */ }
    }
}

public sealed class ElementNodeViewModel(NodeDto dto, ElementNodeViewModel? parent, RootInfo? root) : ObservableObject
{
    string _name = dto.Name ?? "";
    IReadOnlyList<string>? _classes = dto.Classes;
    bool _visible = dto.IsVisible;
    public int Id { get; } = dto.Id;
    public ElementNodeViewModel? Parent { get; } = parent;
    public ObservableCollection<ElementNodeViewModel> Children { get; } = new();
    public string Type { get; } = root is { Title: { Length: > 0 } t } ? $"{dto.Type} \"{t}\"" : dto.Type;
    public string NameText => string.IsNullOrEmpty(_name) ? "" : "#" + _name;
    public string ClassText => _classes is { Count: > 0 } c ? "." + string.Join(".", c) : "";
    public string Hint { get; } = dto.Text is { } text ? $"\"{text}\"" : "";
    public bool IsVisible => _visible;
    public double Opacity => IsVisible ? 1 : 0.45;
    public bool IsTemplatePart { get; } = dto.IsTemplatePart;
    public string Crumb => Type.Split(' ')[0] + NameText;
    public override string ToString() => Crumb;

    /// <summary>tree/changed update op: name, classes and visibility changed in the app.</summary>
    public void Update(string? name, IReadOnlyList<string>? classes, bool isVisible)
    {
        _name = name ?? "";
        _classes = classes;
        _visible = isVisible;
        Raise(nameof(NameText)); Raise(nameof(ClassText)); Raise(nameof(IsVisible)); Raise(nameof(Opacity)); Raise(nameof(Crumb));
    }
}

/// <summary>A captured element image waiting to be saved.</summary>
public sealed record ElementScreenshot(byte[] Png, string FileName);
