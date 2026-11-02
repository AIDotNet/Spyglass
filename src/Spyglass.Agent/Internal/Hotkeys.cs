using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Spyglass.Agent.Internal;

/// <summary>
/// Process-wide hotkeys, registered as a class handler on every TopLevel (current and future windows, popups).
/// Tunnel + handledEventsToo so they work even when a focused control handles keys; matched gestures are
/// marked handled so the app does not also react to them.
/// </summary>
internal static class Hotkeys
{
    static bool _installed;

    public static void Install(SpyglassAgent agent)
    {
        if (_installed) return;
        _installed = true;
        InputElement.KeyDownEvent.AddClassHandler<TopLevel>((tl, e) =>
        {
            if (agent.Options.OpenGesture?.Matches(e) == true)
            {
                e.Handled = true;
                agent.RequestOpen(tl, inspect: false);
            }
            else if (agent.Options.InspectGesture?.Matches(e) == true)
            {
                e.Handled = true;
                agent.RequestOpen(tl, inspect: true);
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }
}
