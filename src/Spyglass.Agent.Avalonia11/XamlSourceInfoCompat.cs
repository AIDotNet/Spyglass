namespace Avalonia.Markup.Xaml.Diagnostics;

/// <summary>
/// Avalonia 12 records where in XAML each object was created; Avalonia 11 has no public equivalent. This stand-in keeps the
/// agent's code identical on both versions: every lookup says "unknown", so elements simply have no source location.
/// </summary>
internal sealed class XamlSourceInfo
{
    public Uri? SourceUri => null;
    public int LineNumber => 0;
    public int LinePosition => 0;
    public static XamlSourceInfo? GetXamlSourceInfo(object? obj) => null;
    public static XamlSourceInfo? GetXamlSourceInfo(object? obj, object? key) => null;
}
