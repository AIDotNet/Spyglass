using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Spyglass.App.ViewModels;
using ArcTreeView = TKCode.Arc.TreeView;

namespace Spyglass.App.Views;

public partial class ElementsView : UserControl
{
    ElementsViewModel? _vm;

    public ElementsView()
    {
        InitializeComponent();
        Spyglass.App.Theme.GlassDividers.Soften(Split);
        Tree.ChildrenSelector = o => ((ElementNodeViewModel)o).Children;
        // DefaultSize alone renders ~70/30 (spike S2 finding), so set the split explicitly.
        AttachedToVisualTree += (_, _) => Split.Sizes = [42, 58];
        Tree.RowHeight = Spyglass.App.Theme.SpyglassTheme.TreeRowHeight;
        // Property editors commit on Enter and revert on Escape (W2 property grid).
        PropertyList.AddHandler(KeyDownEvent, OnPropertyKeyDown, RoutingStrategies.Tunnel);
        Crumbs.ItemClick += (_, e) => { if (e.Item is ElementNodeViewModel node) _vm?.Select(node); };
        Search.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Enter) _vm?.NextMatchCommand.Execute(null); }, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) { _vm.RevealRequested -= Reveal; _vm.ScreenshotCaptured -= SaveScreenshot; }
        _vm = DataContext as ElementsViewModel;
        if (_vm is not null) { _vm.RevealRequested += Reveal; _vm.ScreenshotCaptured += SaveScreenshot; }
    }

    static readonly FilePickerFileType PngType = new("PNG image") { Patterns = ["*.png"], MimeTypes = ["image/png"] };

    async void SaveScreenshot(ElementScreenshot shot)
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        try
        {
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Spyglass.App.Localization.Loc.T("Elements.ScreenshotTitle"),
                SuggestedFileName = shot.FileName, DefaultExtension = "png", FileTypeChoices = [PngType],
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await stream.WriteAsync(shot.Png);
        }
        catch (Exception ex) { _vm.Details.ReportError(Spyglass.App.Localization.Loc.F("Elements.ScreenshotSaveFailed", ex.Message)); }
    }

    void Reveal(ElementNodeViewModel node)
    {
        var chain = new List<ElementNodeViewModel>();
        for (var p = node.Parent; p is not null; p = p.Parent) chain.Insert(0, p);
        foreach (var p in chain) Tree.Expand(p);
        Tree.ScrollIntoView(node);
    }

    static void OnPropertyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not Control { DataContext: PropertyRowViewModel row }) return;
        if (e.Key == Key.Enter) { row.CommitCommand.Execute(null); e.Handled = true; }
        if (e.Key == Key.Escape) { row.EditText = row.EditText; e.Handled = true; }
    }
}
