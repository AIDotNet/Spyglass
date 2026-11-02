namespace Spyglass.VisualStudio.Core.Preview;

/// <summary>Small decisions of the preview that do not depend on the IDE (design/preview/README.md).</summary>
public static class PreviewPolicy
{
    /// <summary>
    /// Whether opening a text view should open the preview window by itself (<c>previewAutoOpen</c>): only for an .axaml
    /// (a .xaml may be WPF), only when the preview is enabled and not already open.
    /// </summary>
    public static bool ShouldAutoOpen(string path, bool autoOpenSetting, bool previewEnabled, bool windowOpen) =>
        autoOpenSetting && previewEnabled && !windowOpen && path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase);

    static readonly HashSet<string> NotRenderable = new(StringComparer.Ordinal)
        { "Application", "ResourceDictionary", "Styles", "Style", "ControlTheme", "DataTemplate", "ControlTemplate" };

    /// <summary>
    /// Whether a file can be shown by itself in the preview: not an Application (App.axaml), a resource dictionary or a
    /// styles file (the previewer would show an empty window); such files only refresh the preview of the active view.
    /// An unreadable root is allowed through so the previewer can report the XAML error.
    /// </summary>
    public static bool IsPreviewable(string xaml) =>
        PreviewXaml.RootLocalName(xaml) is not { } root || !NotRenderable.Contains(root);

    /// <summary>
    /// Whether an edit or save of <paramref name="path"/> should refresh the preview of the active file: any .axaml/.xaml
    /// inside the project's directory (App.axaml, styles, resource dictionaries, other views), except build output.
    /// </summary>
    public static bool IsProjectXaml(string? projectDirectory, string path)
    {
        if (string.IsNullOrEmpty(projectDirectory) || !XamlBufferSync.IsXaml(path)) return false;
        var dir = projectDirectory.TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
        var full = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (!full.StartsWith(dir.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return false;
        var rel = full[dir.Length..];
        foreach (var segment in rel.Split(Path.DirectorySeparatorChar)[..^1])
            if (segment.Equals("bin", StringComparison.OrdinalIgnoreCase) || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}

/// <summary>Where the floating preview window goes next to the editor (pure geometry; device-independent pixels).</summary>
public readonly record struct PreviewPlacement(double Left, double Top, double Width, double Height)
{
    /// <summary>The right third of the IDE window (at least 320 wide), inset a little from its top and bottom.</summary>
    public static PreviewPlacement Beside(double ideLeft, double ideTop, double ideWidth, double ideHeight)
    {
        var width = Math.Max(320, ideWidth / 3);
        var height = Math.Max(240, ideHeight - 80);
        return new(ideLeft + ideWidth - 8 - width, ideTop + 40, width, height);
    }

    /// <summary>
    /// Whether the window still follows the IDE: it does until the user moves or resizes it away from where it was put
    /// (more than 2 DIPs off), after which it stays where they left it.
    /// </summary>
    public static bool StillDocked(PreviewPlacement put, double left, double top, double width, double height) =>
        Math.Abs(put.Left - left) <= 2 && Math.Abs(put.Top - top) <= 2 && Math.Abs(put.Width - width) <= 2 && Math.Abs(put.Height - height) <= 2;
}
