using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Arc;

namespace Spyglass.App.Theme;

/// <summary>
/// Arc's ResizablePanels draws its divider as a 1px line in an internal handle control (ResizablePanelsHandle).
/// Between floating glass cards that line looks like a stray dark stripe (the gap between cards is already the
/// visual separator), so the handle is made invisible while staying hit-testable, and fades in on hover as the
/// resize affordance.
/// </summary>
public static class GlassDividers
{
    /// <summary>
    /// The Sidebar draws a 1px line on its right edge (Border#PART_Edge in its pane host). On a floating, rounded glass
    /// card it sticks out as a straight dark line ignoring the corners. Set as local values because values set inside
    /// the template outrank style setters.
    /// </summary>
    public static void HideEdge(Sidebar sidebar)
    {
        void Apply()
        {
            foreach (var edge in sidebar.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "PART_Edge"))
                edge.BorderThickness = default;
        }
        sidebar.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(Apply, DispatcherPriority.Loaded);
        sidebar.LayoutUpdated += (_, _) => Apply();
    }

    /// <summary>
    /// ThemeSwitcher's inner button is an opaque (Secondary) Arc button: on the glass title bar it reads as a black
    /// square. Switch it to the Ghost variant; Arc's Reveal transition on the switcher is unaffected.
    /// </summary>
    public static void Ghost(ThemeSwitcher switcher)
    {
        void Apply()
        {
            foreach (var button in switcher.GetVisualDescendants().OfType<TKCode.Arc.Button>().Concat(switcher.GetVisualDescendants().OfType<TKCode.Arc.Button>()))
                button.Variant = ButtonVariant.Ghost;
            if (switcher.GetVisualChildren().FirstOrDefault() is TKCode.Arc.Button b) b.Variant = ButtonVariant.Ghost;
        }
        switcher.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(Apply, DispatcherPriority.Loaded);
        switcher.TemplateApplied += (_, _) => Apply();
    }

    public static void Soften(ResizablePanels panels)
    {
        void Apply()
        {
            foreach (var handle in panels.GetVisualDescendants().OfType<Control>().Where(c => c.GetType().Name == "ResizablePanelsHandle"))
            {
                if (handle.Tag is "soft") continue;
                handle.Tag = "soft";
                handle.Opacity = 0;
                handle.PointerEntered += (_, _) => handle.Opacity = 1;
                handle.PointerExited += (_, _) => handle.Opacity = 0;
            }
        }

        // The handles are created with the template and the panes; apply after layout and whenever panes change.
        panels.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(Apply, DispatcherPriority.Loaded);
        panels.LayoutUpdated += (_, _) => Apply();
    }
}
