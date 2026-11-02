using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Settings;

namespace Spyglass.VisualStudio;

#pragma warning disable VSEXTPREVIEW_SETTINGS // the settings API is preview in 17.14

/// <summary>Tools › Options › Spyglass (same settings as the VS Code extension).</summary>
internal static class SpyglassSettings
{
    [VisualStudioContribution]
    internal static SettingCategory Category { get; } = new("spyglass", "%Spyglass.Settings.Category%")
    {
        Description = "%Spyglass.Settings.Category.Description%",
    };

    [VisualStudioContribution]
    internal static Setting.Boolean HotReloadEnabled { get; } = new("hotReloadEnabled", "%Spyglass.Settings.HotReloadEnabled%", Category, defaultValue: true);

    [VisualStudioContribution]
    internal static Setting.Integer HotReloadDelay { get; } = new("hotReloadDelay", "%Spyglass.Settings.HotReloadDelay%", Category, defaultValue: 300)
    {
        Minimum = 0,
        Maximum = 10000,
    };

    [VisualStudioContribution]
    internal static Setting.Integer Port { get; } = new("port", "%Spyglass.Settings.Port%", Category, defaultValue: 0)
    {
        Minimum = 0,
        Maximum = 65535,
    };

    [VisualStudioContribution]
    internal static Setting.String Command { get; } = new("command", "%Spyglass.Settings.Command%", Category, defaultValue: "spyglass");

    [VisualStudioContribution]
    internal static Setting.Boolean AutoStart { get; } = new("autoStart", "%Spyglass.Settings.AutoStart%", Category, defaultValue: true);

    [VisualStudioContribution]
    internal static Setting.Boolean PreviewEnabled { get; } = new("previewEnabled", "%Spyglass.Settings.PreviewEnabled%", Category, defaultValue: true);

    [VisualStudioContribution]
    internal static Setting.Integer PreviewDelay { get; } = new("previewDelay", "%Spyglass.Settings.PreviewDelay%", Category, defaultValue: 300)
    {
        Minimum = 0,
        Maximum = 10000,
    };

    [VisualStudioContribution]
    internal static Setting.Boolean PreviewAutoOpen { get; } = new("previewAutoOpen", "%Spyglass.Settings.PreviewAutoOpen%", Category, defaultValue: false);

    // Remembered by the preview window itself (zoom/theme buttons write them): "fit" or a percentage; default|light|dark.
    [VisualStudioContribution]
    internal static Setting.String PreviewZoom { get; } = new("previewZoom", "%Spyglass.Settings.PreviewZoom%", Category, defaultValue: "fit");

    [VisualStudioContribution]
    internal static Setting.String PreviewTheme { get; } = new("previewTheme", "%Spyglass.Settings.PreviewTheme%", Category, defaultValue: "default");
}
