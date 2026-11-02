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
}
