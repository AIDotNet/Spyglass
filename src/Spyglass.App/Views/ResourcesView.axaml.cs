using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Spyglass.App.ViewModels;

namespace Spyglass.App.Views;

public partial class ResourcesView : UserControl
{
    public ResourcesView()
    {
        InitializeComponent();
        Spyglass.App.Theme.GlassDividers.Soften(Split);
        Tree.ChildrenSelector = o => ((ProviderNodeViewModel)o).Children;
        // DefaultSize alone renders ~70/30 (spike S2 finding), so set the split explicitly.
        AttachedToVisualTree += (_, _) => Split.Sizes = [34, 66];
        Tree.RowHeight = Spyglass.App.Theme.SpyglassTheme.TreeRowHeight;
        // Enter commits the edited value, Escape reverts the text (like the property grid).
        EditInput.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (DataContext is not ResourcesViewModel vm) return;
            if (e.Key == Key.Enter) { vm.CommitCommand.Execute(null); e.Handled = true; }
            if (e.Key == Key.Escape) { vm.SelectedEntry = vm.SelectedEntry; e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        LookupField.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is ResourcesViewModel vm) { vm.ResolveCommand.Execute(null); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
    }

    ResourcesViewModel? _vm;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.RevealRequested -= Reveal;
        _vm = DataContext as ResourcesViewModel;
        if (_vm is not null) _vm.RevealRequested += Reveal;
    }

    void Reveal(ProviderNodeViewModel node)
    {
        // Roots open one level (so the page does not start as two collapsed lines), and the selected node is shown.
        foreach (var root in _vm!.Roots) Tree.Expand(root);
        for (var p = node.Parent; p is not null; p = p.Parent) Tree.Expand(p);
        Tree.ScrollIntoView(node);
    }

    void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is EntryRowViewModel row) row.SelectCommand?.Execute(null);
    }
}
