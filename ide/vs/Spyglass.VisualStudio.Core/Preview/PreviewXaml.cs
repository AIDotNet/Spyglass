namespace Spyglass.VisualStudio.Core.Preview;

/// <summary>The theme variant the previewed XAML is rendered with (RequestedThemeVariant).</summary>
public enum PreviewTheme { Default, Light, Dark }

/// <summary>
/// A change made to the root start tag before the XAML is sent: an attribute inserted at (Line, Column), 1-based.
/// Host error positions on that line that lie after the insertion are shifted back so they match the editor's text.
/// </summary>
public readonly record struct PreviewXamlEdit(int Line, int Column, int Length)
{
    public static readonly PreviewXamlEdit None = default;

    public bool IsEmpty => Length == 0;

    /// <summary>Maps a position in the edited XAML back to the editor's text.</summary>
    public (int? Line, int? Column) MapBack(int? line, int? column)
    {
        if (IsEmpty || line != Line || column is not { } c) return (line, column);
        return (line, c >= Column + Length ? c - Length : c < Column ? c : Column);
    }
}

public static class PreviewXaml
{
    const string Attribute = "RequestedThemeVariant";
    static readonly System.Text.RegularExpressions.Regex BackgroundAttribute = new(@"(?<![\w.])Background\s*=", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Adds <c>RequestedThemeVariant="Light|Dark"</c> to the root element (the previewer has no theme message), together
    /// with <c>Background="{DynamicResource SystemRegionBrush}"</c> when the root has no Background of its own: the previewer
    /// gives a Window no themed background, so the variant alone would not be visible. Both go in one insertion, so error
    /// columns map back with a single offset. A root that already sets the variant is left alone, and so is
    /// <see cref="PreviewTheme.Default"/>. Only a <c>Window</c> root
    /// takes the property (the previewer rejects it on a UserControl, which would turn the preview into an error), so any
    /// other root is sent unchanged.
    /// </summary>
    public static (string Xaml, PreviewXamlEdit Edit) ApplyTheme(string xaml, PreviewTheme theme)
    {
        if (theme == PreviewTheme.Default) return (xaml, PreviewXamlEdit.None);
        var nameEnd = FindRootNameEnd(xaml);
        if (nameEnd < 0) return (xaml, PreviewXamlEdit.None);

        var nameStart = xaml.LastIndexOf('<', nameEnd - 1) + 1;
        var name = xaml.AsSpan(nameStart, nameEnd - nameStart);
        var colon = name.LastIndexOf(':');
        if (!(colon >= 0 ? name[(colon + 1)..] : name).SequenceEqual("Window")) return (xaml, PreviewXamlEdit.None);

        var tagEnd = xaml.IndexOf('>', nameEnd);
        if (tagEnd < 0 || xaml.AsSpan(nameEnd, tagEnd - nameEnd).Contains(Attribute, StringComparison.Ordinal))
            return (xaml, PreviewXamlEdit.None);

        var hasBackground = BackgroundAttribute.IsMatch(xaml.AsSpan(nameEnd, tagEnd - nameEnd).ToString());
        var insert = $" {Attribute}=\"{theme}\"" + (hasBackground ? "" : " Background=\"{DynamicResource SystemRegionBrush}\"");
        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < nameEnd; i++)
            if (xaml[i] == '\n') { line++; lineStart = i + 1; }
        return (xaml.Insert(nameEnd, insert), new PreviewXamlEdit(line, nameEnd - lineStart + 1, insert.Length));
    }

    /// <summary>The root element's local name (without prefix), or null when the text has no element.</summary>
    public static string? RootLocalName(string xaml)
    {
        var end = FindRootNameEnd(xaml);
        if (end < 0) return null;
        var start = xaml.LastIndexOf('<', end - 1) + 1;
        var name = xaml.AsSpan(start, end - start);
        var colon = name.LastIndexOf(':');
        return (colon >= 0 ? name[(colon + 1)..] : name).ToString();
    }

    /// <summary>Index just after the root element's name, skipping the XML declaration, comments and DOCTYPE; -1 if none.</summary>
    static int FindRootNameEnd(string s)
    {
        var i = 0;
        while (i < s.Length)
        {
            var lt = s.IndexOf('<', i);
            if (lt < 0 || lt + 1 >= s.Length) return -1;
            if (s.AsSpan(lt).StartsWith("<?")) { var e = s.IndexOf("?>", lt, StringComparison.Ordinal); if (e < 0) return -1; i = e + 2; continue; }
            if (s.AsSpan(lt).StartsWith("<!--")) { var e = s.IndexOf("-->", lt, StringComparison.Ordinal); if (e < 0) return -1; i = e + 3; continue; }
            if (s.AsSpan(lt).StartsWith("<!")) { var e = s.IndexOf('>', lt); if (e < 0) return -1; i = e + 1; continue; }
            var j = lt + 1;
            while (j < s.Length && !char.IsWhiteSpace(s[j]) && s[j] != '>' && s[j] != '/') j++;
            return j > lt + 1 ? j : -1;
        }
        return -1;
    }
}
