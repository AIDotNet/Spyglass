using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        if (_vm is not null) _vm.RevealRequested -= Reveal;
        _vm = DataContext as ElementsViewModel;
        if (_vm is not null) _vm.RevealRequested += Reveal;
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
