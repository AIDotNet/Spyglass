using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Spyglass.App.Core.Network;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Spyglass.App.ViewModels;
using TKCode.Arc;

namespace Spyglass.App.Views;

public partial class NetworkView : UserControl
{
    NetworkViewModel? _vm;

    public NetworkView()
    {
        InitializeComponent();
        // Row selection (left and right click, so the context menu acts on the clicked row).
        RowList.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is Control { DataContext: NetworkRowViewModel row } && _vm is not null) _vm.Selected = row;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        RowMenu.ItemClicked += async (_, e) => { if (e.Entry.Tag is string what) await OnRowMenu(what); };
        // Right click on the table opens the menu for the row under the pointer; entries depend on selection and settings.
        RowList.AddHandler(PointerPressedEvent, (_, e) => { if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) BuildRowMenu(); },
            RoutingStrategies.Tunnel, handledEventsToo: true);
        HeaderRow.AddHandler(PointerPressedEvent, (_, e) => { if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) BuildHeaderMenu(e); },
            RoutingStrategies.Tunnel, handledEventsToo: true);
        HeaderMenu.ItemClicked += (_, e) => OnHeaderMenu(e.Entry.Tag as string);
        SettingsMenu.ItemClicked += (_, e) => OnSettingsMenu(e.Entry.Tag as string);
        ExportMenu.ItemClicked += async (_, e) => await ExportHar(e.Entry.Tag as string == "sensitive");
        Overview.RangeChanged += (a, b) => _vm?.SetRange(a, b);
        // Drag-and-drop: a .har file dropped on the table is imported (Chrome).
        DragDrop.SetAllowDrop(TablePanel, true);
        TablePanel.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None);
        TablePanel.AddHandler(DragDrop.DropEvent, async (_, e) => await OnDrop(e));
        // Chrome shortcuts: Ctrl/Cmd+E record, Esc closes details, ↑/↓ move the selection.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.CopyRequested -= OnCopy;
        _vm = DataContext as NetworkViewModel;
        if (_vm is not null)
        {
            _vm.CopyRequested += OnCopy;
            _ = _vm.ActivateAsync();   // opening the panel starts recording (design/network §3)
        }
    }

    async void OnCopy(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    void OnTypeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TypeToggleViewModel t })
        {
            var mods = (e as PointerEventArgs)?.KeyModifiers ?? KeyModifiers.None;
            t.Click(mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta) || _lastModifiers.HasFlag(KeyModifiers.Control) || _lastModifiers.HasFlag(KeyModifiers.Meta));
        }
    }

    KeyModifiers _lastModifiers;

    protected override void OnKeyUp(KeyEventArgs e) { _lastModifiers = e.KeyModifiers; base.OnKeyUp(e); }

    void OnCloseDetails(object? sender, RoutedEventArgs e) { if (_vm is not null) _vm.Selected = null; }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        _lastModifiers = e.KeyModifiers;
        if (_vm is null) return;
        var cmd = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (cmd && e.Key == Key.E) { _vm.ToggleRecordCommand.Execute(null); e.Handled = true; }
        else if (cmd && e.Key == Key.F) { OpenSearch(); e.Handled = true; }
        else if (e.Key == Key.Escape && _vm.SearchOpen) { _vm.SearchOpen = false; Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter && _vm.SearchOpen && SearchBox.IsKeyboardFocusWithin) { _ = SearchOrNext(); e.Handled = true; }
        else if (e.Key == Key.Escape && _vm.Selected is not null) { _vm.Selected = null; e.Handled = true; }
        else if (e.Key is Key.Down or Key.Up && _vm.Rows.Count > 0 && e.Source is not TextBox)
        {
            var i = _vm.Selected is null ? -1 : _vm.Rows.IndexOf(_vm.Selected);
            _vm.Selected = _vm.Rows[Math.Clamp(i + (e.Key == Key.Down ? 1 : -1), 0, _vm.Rows.Count - 1)];
            e.Handled = true;
        }
    }

    // ---- search (Ctrl/Cmd+F) ----

    (string Text, bool Case, bool Regex)? _searched;

    void OpenSearch()
    {
        if (_vm is null) return;
        _vm.SearchOpen = true;
        Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Input);
    }

    async Task SearchOrNext()
    {
        if (_vm is null) return;
        var key = (_vm.SearchText, _vm.SearchMatchCase, _vm.SearchRegex);
        if (_searched != key) { _searched = key; await _vm.SearchAsync(); }
        else _vm.NextHit();
    }

    void OnSearchToggle(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (_vm.SearchOpen) _vm.SearchOpen = false; else OpenSearch();
    }

    void OnSearchNext(object? sender, RoutedEventArgs e) => _ = SearchOrNext();
    void OnSearchPrev(object? sender, RoutedEventArgs e) => _vm?.NextHit(-1);
    void OnSearchClose(object? sender, RoutedEventArgs e) { if (_vm is not null) _vm.SearchOpen = false; }

    // ---- header: sort (click) and columns (right-click) ----

    void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is not null && sender is Control { Tag: ColumnViewModel col } && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            _vm.ToggleSort(col);
    }

    static MenuEntry Entry(string header, string tag, bool? check = null, bool radio = false, bool separator = false) => new()
    {
        Header = header, Tag = tag, HasSeparatorBefore = separator,
        ToggleType = check is null ? MenuEntryToggleType.None : radio ? MenuEntryToggleType.Radio : MenuEntryToggleType.CheckBox,
        IsChecked = check ?? false,
    };

    void BuildHeaderMenu(PointerPressedEventArgs e)
    {
        if (_vm is null) return;
        HeaderMenu.Items.Clear();
        foreach (var c in NetworkViewModel.OptionalColumns)
            HeaderMenu.Items.Add(Entry(NetworkViewModel.TitleOf(c), "col:" + c, _vm.IsVisible(c)));

        var response = new MenuEntry { Header = "Response Headers", HasSeparatorBefore = true };
        foreach (var h in _vm.CustomHeaders) response.Items.Add(Entry($"Remove column “{h}”", "rm:" + h));
        response.Items.Add(Entry("Add custom header…", "addheader", separator: _vm.CustomHeaders.Count > 0));
        HeaderMenu.Items.Add(response);

        var waterfall = new MenuEntry { Header = "Waterfall" };
        foreach (var (w, title) in new[] { (WaterfallSort.StartTime, "Start Time"), (WaterfallSort.ResponseTime, "Response Time"), (WaterfallSort.EndTime, "End Time"),
                     (WaterfallSort.TotalDuration, "Total Duration"), (WaterfallSort.Latency, "Latency") })
            waterfall.Items.Add(Entry(title, "wf:" + w, _vm.Sort.Column == NetworkColumn.Waterfall && _vm.Sort.Waterfall == w, radio: true));
        HeaderMenu.Items.Add(waterfall);
        HeaderMenu.Items.Add(Entry("Sort chronologically (default)", "sort-reset", separator: true));
        HeaderMenu.Items.Add(Entry("Reset columns", "reset"));
    }

    void OnHeaderMenu(string? tag)
    {
        if (_vm is null || tag is null) return;
        if (tag.StartsWith("col:")) _vm.ToggleColumn(Enum.Parse<NetworkColumn>(tag[4..]));
        else if (tag.StartsWith("wf:")) _vm.SetWaterfallSort(Enum.Parse<WaterfallSort>(tag[3..]));
        else if (tag.StartsWith("rm:")) _vm.RemoveCustomHeader(tag[3..]);
        else if (tag == "addheader") { _vm.NewHeaderName = ""; _vm.IsAddingHeader = true; }
        else if (tag == "sort-reset") _vm.SetSort(NetworkSortSpec.Chronological);
        else if (tag == "reset") _vm.ResetColumns();
    }

    void OnAddHeaderOk(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.AddCustomHeader(_vm.NewHeaderName);
        _vm.IsAddingHeader = false;
    }

    void OnAddHeaderCancel(object? sender, RoutedEventArgs e) { if (_vm is not null) _vm.IsAddingHeader = false; }

    // ---- row context menu: Copy, Copy all, Save all as HAR (parity §7) ----

    void BuildRowMenu()
    {
        if (_vm is null) return;
        RowMenu.Items.Clear();
        var copy = new MenuEntry { Header = "Copy" };
        var sep = false;
        if (_vm.Selected is not null)
        {
            copy.Items.Add(Entry("Copy URL", "row:url"));
            copy.Items.Add(Entry("Copy as cURL", "row:curl"));
            copy.Items.Add(Entry("Copy as PowerShell", "row:powershell"));
            copy.Items.Add(Entry("Copy as fetch", "row:fetch"));
            copy.Items.Add(Entry("Copy as fetch (Node.js)", "row:fetch-node"));
            copy.Items.Add(Entry("Copy as C# (HttpClient)", "row:csharp"));
            copy.Items.Add(Entry("Copy response", "row:response", separator: true));
            copy.Items.Add(Entry("Copy stack trace", "row:stack"));
            sep = true;
        }
        void All(string scope, string label)
        {
            var first = true;
            foreach (var (kind, text) in new[] { ("urls", "URLs"), ("curl", "as cURL"), ("powershell", "as PowerShell"), ("fetch", "as fetch"),
                         ("fetch-node", "as fetch (Node.js)"), ("csharp", "as C#") })
            { copy.Items.Add(Entry($"Copy {label} {text}", $"{scope}:{kind}", separator: first && sep)); first = false; sep = false; }
            copy.Items.Add(Entry($"Copy {label} as HAR (sanitized)", $"{scope}:har"));
            if (_vm.AllowSensitiveHar) copy.Items.Add(Entry($"Copy {label} as HAR (with sensitive data)", $"{scope}:har-sensitive"));
            sep = true;
        }
        All("all", "all");
        if (_vm.IsFiltered) All("listed", "all listed");
        RowMenu.Items.Add(copy);
        RowMenu.Items.Add(Entry("Save all as HAR (sanitized)…", "save:har", separator: true));
        if (_vm.AllowSensitiveHar) RowMenu.Items.Add(Entry("Save all as HAR (with sensitive data)…", "save:har-sensitive"));
    }

    async Task OnRowMenu(string tag)
    {
        if (_vm is null) return;
        var (scope, what) = tag.Split(':', 2) is [var a, var b] ? (a, b) : ("", tag);
        switch (scope)
        {
            case "row": _vm.Copy(what); break;
            case "all": await _vm.CopyAllAsync(what, listedOnly: false); break;
            case "listed": await _vm.CopyAllAsync(what, listedOnly: true); break;
            case "save": await ExportHar(what == "har-sensitive"); break;
        }
    }

    // ---- HAR files ----

    static readonly FilePickerFileType HarType = new("HTTP Archive") { Patterns = ["*.har"], MimeTypes = ["application/json"] };

    async Task ExportHar(bool sensitive)
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        try
        {
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = sensitive ? "Export HAR (with sensitive data)" : "Export HAR (sanitized)",
                SuggestedFileName = _vm.HarFileName(), DefaultExtension = "har", FileTypeChoices = [HarType],
            });
            if (file is null) return;
            var json = await _vm.BuildHarAsync(sensitive);
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await stream.WriteAsync(new System.Text.UTF8Encoding(false).GetBytes(json));
            _vm.Notice = null;
        }
        catch (Exception ex) { _vm.Notice = "Export failed: " + ex.Message; }
    }

    void OnExportHar(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (!_vm.AllowSensitiveHar) { _ = ExportHar(false); return; }
        ExportMenu.Items.Clear();
        ExportMenu.Items.Add(Entry("Export HAR (sanitized)…", "sanitized"));
        ExportMenu.Items.Add(Entry("Export HAR (with sensitive data)…", "sensitive"));
        ExportMenu.Open();
    }

    async void OnImportHar(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import HAR file", AllowMultiple = true, FileTypeFilter = [HarType, FilePickerFileTypes.All] });
        foreach (var f in files) await ImportFile(f);
    }

    async Task ImportFile(IStorageFile file)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            _vm?.ImportHar(await reader.ReadToEndAsync());
        }
        catch (Exception ex) { if (_vm is not null) _vm.Notice = "Import failed: " + ex.Message; }
    }

    static bool HasFiles(DragEventArgs e) => e.DataTransfer?.Contains(DataFormat.File) == true;

    async Task OnDrop(DragEventArgs e)
    {
        if (e.DataTransfer?.TryGetFiles() is not { } items) return;
        foreach (var f in items.OfType<IStorageFile>()) await ImportFile(f);
        e.Handled = true;
    }

    // ---- gear menu ----

    void OnSettings(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        SettingsMenu.Items.Clear();
        SettingsMenu.Items.Add(Entry("Show overview", "overview", _vm.ShowOverview));
        SettingsMenu.Items.Add(Entry("Allow to generate HAR with sensitive data", "sensitive", _vm.AllowSensitiveHar));
        SettingsMenu.Open();
    }

    void OnSettingsMenu(string? tag)
    {
        if (_vm is null) return;
        if (tag == "overview") _vm.ShowOverview = !_vm.ShowOverview;
        else if (tag == "sensitive") _vm.AllowSensitiveHar = !_vm.AllowSensitiveHar;
    }
}
