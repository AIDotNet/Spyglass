using Spyglass.App.Core.Settings;
using Spyglass.Protocol.Messages;

namespace Spyglass.App.ViewModels;

/// <summary>"Open in editor" for XAML locations, using Settings › Editor. Returns an error message for the UI, or "".</summary>
public static class OpenInEditor
{
    public static string Open(SourceInfo? source)
    {
        if (source is null) return "No XAML location (Release build, or the element was created in code).";
        return EditorLauncher.Open(source, SettingsService.Shared.Current) ?? "";
    }
}
