using Spyglass.App.Core;
using Spyglass.App.Core.Settings;
using Spyglass.App.Localization;
using Spyglass.Protocol.Messages;

namespace Spyglass.App.ViewModels;

/// <summary>
/// "Open in editor" for XAML locations. When an IDE extension whose workspace contains the file is connected, the file is
/// revealed there (editor/reveal, design/hot-reload/README.md §9); otherwise Settings › Editor starts an editor.
/// Returns an error message for the UI, or "".
/// </summary>
public static class OpenInEditor
{
    /// <summary>Set by the main view model once the hub exists.</summary>
    public static IdeEndpoint? Ide { get; set; }

    public static string Open(SourceInfo? source)
    {
        if (source is null) return Loc.T("Editor.NoLocation");
        if (Ide is { } ide && EditorLauncher.FilePath(source) is { } file && ide.HasEditorFor(file))
        {
            _ = Task.Run(async () =>
            {
                if (!await ide.RevealAsync(file, source.Line, Math.Max(1, source.Column)))
                    EditorLauncher.Open(source, SettingsService.Shared.Current);
            });
            return "";
        }
        return EditorLauncher.Open(source, SettingsService.Shared.Current) ?? "";
    }
}
