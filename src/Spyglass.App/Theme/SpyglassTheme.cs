using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Motion;
using TKCode.Arc.Themes.Corners;

namespace Spyglass.App.Theme;

/// <summary>
/// Spyglass look (decision D8): Apple "Liquid Glass" surfaces, Arc's full motion, Arc's default (large) corner radii.
/// Control sizes are trimmed a little for a dev tool's information density (values verified in spike S2).
/// </summary>
public static class SpyglassTheme
{
    public const double TreeRowHeight = 26;

    public static ArcThemeBuilder Glass(ArcThemeBuilder t) => t
        .Accent(ArcAccentPreset.Blue)
        .Corners(ArcCornerStyle.Default)
        .MotionLevel(ArcMotionLevel.Full)          // Arc animations; Arc still honours the OS "reduce motion" setting
        .Token(nameof(ArcDesignTokenKind.ControlSizeSM), "30")
        .Token(nameof(ArcDesignTokenKind.ControlSizeMD), "34")
        .Token(nameof(ArcDesignTokenKind.ControlSizeLG), "40")
        .Token(nameof(ArcDesignTokenKind.TypeXS), "11")
        .Token(nameof(ArcDesignTokenKind.TypeSM), "12")
        .Token(nameof(ArcDesignTokenKind.TypeBase), "13")
        .Token(nameof(ArcDesignTokenKind.GlyphSizeSM), "14")
        .Token(nameof(ArcDesignTokenKind.GlyphSizeMD), "16")
        // Arc draws a divider line between ResizablePanels panes; between floating glass cards it shows up as a
        // stray dark line, so switch it off (the panes keep their drag handle; the gap between cards is the margin).
        .Token("ResizablePanels", "DividerThickness", "0")
        // Checkbox label sat ~6px below the box: Arc's label line height / copy margin are tuned for a description
        // line under the label. For single-line labels, make the line as tall as the box so they centre.
        .Token("Checkbox", "CopyMargin", "0,6,0,0")
        // Tabs: tighter triggers so seven Network detail tabs fit without scroll arrows.
        .Token("TabItem", "InlinePadding", "10,0,10,0")
        .Token("TabItem", "TriggerMinWidth", "0")
        .Token("Checkbox", "LabelLineHeight", "20");
}
