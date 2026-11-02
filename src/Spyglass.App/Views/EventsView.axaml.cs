using Avalonia.Controls;
using Avalonia.Input;
using Spyglass.App.ViewModels;

namespace Spyglass.App.Views;

public partial class EventsView : UserControl
{
    public EventsView()
    {
        InitializeComponent();
        Spyglass.App.Theme.GlassDividers.Soften(Split);
    }

    void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is EventRowViewModel row) row.SelectCommand.Execute(null);
    }

    /// <summary>Double-click a record: jump to its source element (W3).</summary>
    void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is EventRowViewModel row && DataContext is EventsViewModel vm)
        {
            vm.Selected = row;
            vm.ShowSourceCommand.Execute(null);
        }
    }
}
