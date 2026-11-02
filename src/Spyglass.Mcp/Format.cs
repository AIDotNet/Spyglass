using System.Globalization;
using System.Text;
using Spyglass.Protocol.Messages;

namespace Spyglass.Mcp;

/// <summary>Compact text renderings (design/mcp/01-tools.md): readable for models, cheap in tokens.</summary>
internal static class Format
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static IEnumerable<NodeDto> Flatten(NodeDto n) => new[] { n }.Concat(n.Children?.SelectMany(Flatten) ?? []);

    public static string Compound(NodeDto n)
    {
        var sb = new StringBuilder(n.Type);
        if (!string.IsNullOrEmpty(n.Name)) sb.Append('#').Append(n.Name);
        foreach (var c in n.Classes ?? []) sb.Append('.').Append(c);
        return sb.ToString();
    }

    /// <summary><c>Button#Save.primary "Save" [e10] 120×32 @24,180 disabled</c></summary>
    public static string NodeLine(NodeDto n, bool withGeometry = true, string? extra = null)
    {
        var sb = new StringBuilder(Compound(n));
        if (n.Text is { } t) sb.Append(" \"").Append(t.Replace("\n", "⏎")).Append('"');
        if (n.Placeholder is { } ph && string.IsNullOrEmpty(n.Text)) sb.Append(" placeholder=\"").Append(ph).Append('"');
        sb.Append(" [e").Append(n.Id).Append(']');
        if (withGeometry && n.Bounds is { } b)
            sb.Append(' ').Append(Num(b.W)).Append('×').Append(Num(b.H)).Append(" @").Append(Num(b.X)).Append(',').Append(Num(b.Y));
        if (!n.IsVisible) sb.Append(" hidden");
        if (!n.IsEnabled) sb.Append(" disabled");
        if (n.IsFocused) sb.Append(" focused");
        foreach (var pc in n.PseudoClasses ?? [])
            if (pc is ":checked" or ":selected" or ":expanded" or ":indeterminate") sb.Append(' ').Append(pc.TrimStart(':'));
        if (n.IsTemplatePart) sb.Append(" (template)");
        if (extra is not null) sb.Append(' ').Append(extra);
        return sb.ToString();
    }

    public static string Num(double v) => Math.Round(v, 1).ToString("0.#", Inv);

    public static string Thickness(ThicknessData? t) =>
        t is null ? "—" : t.L == t.T && t.T == t.R && t.R == t.B ? Num(t.L) : $"{Num(t.L)},{Num(t.T)},{Num(t.R)},{Num(t.B)}";

    public static string Location(SourceInfo? s)
    {
        if (s is null) return "(not available — Release build or created in code)";
        var file = s.Uri;
        var i = file.LastIndexOfAny(['/', '\\']);
        return $"{(i >= 0 ? file[(i + 1)..] : file)}:{s.Line}:{s.Column}   ({s.Uri})";
    }
}
