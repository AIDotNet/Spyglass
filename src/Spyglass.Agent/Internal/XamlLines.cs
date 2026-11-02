namespace Spyglass.Agent.Internal;

/// <summary>
/// Normalises XamlSourceInfo line numbers to 1-based (editors, protocol, UI).
/// Avalonia 12.1.4 is inconsistent, found by the integration tests:
/// controls (StyledElement) report 0-based lines (an element on line 14 reports 13), while
/// Style/ControlTheme objects report 1-based lines (a Style on line 6 reports 6; spike S1 X2).
/// </summary>
internal static class XamlLines
{
    public static int ToOneBased(int line, object owner) => owner is Avalonia.StyledElement ? line + 1 : line;
}
