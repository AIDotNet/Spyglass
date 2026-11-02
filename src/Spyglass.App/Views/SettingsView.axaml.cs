using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Spyglass.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // An Arc SegmentedControl asks to bring its selected segment into view when it loads. That request bubbles to the
        // page's ScrollViewer and scrolls the whole page down to the lowest segmented control (Editor). The segments are
        // already visible, so swallow those requests below the scroll viewer's content presenter.
        Page.AddHandler(RequestBringIntoViewEvent, (_, e) =>
        {
            if (e.TargetObject is Avalonia.Visual v && (v.FindAncestorOfType<TKCode.Arc.SegmentedControl>() is not null || v is TKCode.Arc.SegmentedControl))
                e.Handled = true;
        });
    }
}
