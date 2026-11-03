using System.Text.RegularExpressions;

namespace Spyglass.XamlLanguage;

public enum XamlContextKind { None, Text, ElementName, ClosingTag, AttributeName, AttributeValue }

/// <summary>An element of the document (open around the cursor, or the tag the cursor is in).</summary>
/// <param name="NameStart">Offset of the element name (just after '&lt;').</param>
public sealed record XamlElement(string Name, int NameStart, IReadOnlyDictionary<string, string> Attributes, XamlElement? Parent)
{
    public IEnumerable<XamlElement> SelfAndAncestors()
    {
        for (var e = this; e is not null; e = e.Parent) yield return e;
    }
}

/// <summary>Where the cursor is in a .axaml text, as far as completion and hover care.</summary>
public sealed class XamlContext
{
    public XamlContextKind Kind { get; init; }

    /// <summary>The tag the cursor is in (attribute contexts), or the innermost open element (text, element name: the parent).</summary>
    public XamlElement? Element { get; init; }

    /// <summary>What is typed of the current token up to the cursor, and where it starts.</summary>
    public string Partial { get; init; } = "";
    public int PartialStart { get; init; }

    /// <summary>Attribute value context: the attribute's name and its value up to the cursor (starting after the quote).</summary>
    public string? Attribute { get; init; }
    public int ValueStart { get; init; }

    /// <summary>prefix ("" = default) → xmlns value, for the cursor's position.</summary>
    public IReadOnlyDictionary<string, string> Namespaces { get; init; } = new Dictionary<string, string>();

    /// <summary>The root element and where a new xmlns declaration goes (after its name), or -1.</summary>
    public XamlElement? Root { get; init; }
    public int XmlnsInsertOffset { get; init; } = -1;

    public string? Prefix(string uri) => Namespaces.FirstOrDefault(kv => kv.Value == uri).Key;
}

/// <summary>
/// A tolerant scan of the text up to the cursor (half-typed tags, unclosed quotes and missing end tags are normal while
/// typing): element nesting, the tag the cursor is in, and its attributes (the whole tag, also after the cursor, so a root
/// element's xmlns declarations count wherever the cursor is).
/// </summary>
public static class XamlScanner
{
    static readonly Regex AttributeRx = new("""([\w:.\-]+)\s*=\s*(?:"([^"<]*)"?|'([^'<]*)'?)""", RegexOptions.Compiled);

    static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or ':' or '.' or '-';

    public static XamlContext At(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        XamlElement? open = null;   // innermost open element
        XamlElement? root = null;
        var rootInsert = -1;
        var i = 0;
        while (i < offset)
        {
            if (text[i] != '<')
            {
                var next = text.IndexOf('<', i);
                if (next < 0 || next >= offset) break;   // the cursor is in text content
                i = next;
                continue;
            }
            // Comments, CDATA, processing instructions: skip, or stop if the cursor is inside.
            if (Starts(text, i, "<!--") || Starts(text, i, "<![CDATA[") || Starts(text, i, "<?") || Starts(text, i, "<!"))
            {
                var close = Starts(text, i, "<!--") ? "-->" : Starts(text, i, "<![CDATA[") ? "]]>" : Starts(text, i, "<?") ? "?>" : ">";
                var end = text.IndexOf(close, i + 2, StringComparison.Ordinal);
                if (end < 0 || end + close.Length > offset) return new XamlContext { Kind = XamlContextKind.None, Element = open };
                i = end + close.Length;
                continue;
            }

            var closing = i + 1 < text.Length && text[i + 1] == '/';
            var nameStart = i + (closing ? 2 : 1);
            var nameEnd = nameStart;
            while (nameEnd < text.Length && IsNameChar(text[nameEnd])) nameEnd++;
            var tagEnd = TagEnd(text, nameEnd);   // index of '>' (or of the '<' that cuts a broken tag), or text.Length
            var name = text[nameStart..nameEnd];

            if (closing)
            {
                if (offset <= tagEnd)
                {
                    if (offset > nameEnd) return new XamlContext { Kind = XamlContextKind.None, Element = open };
                    return new XamlContext
                    {
                        Kind = XamlContextKind.ClosingTag, Element = open, Partial = text[nameStart..offset], PartialStart = nameStart,
                        Namespaces = Namespaces(open, null), Root = root, XmlnsInsertOffset = rootInsert,
                    };
                }
                // Pop to the matching element (tolerates missing end tags).
                for (var e = open; e is not null; e = e.Parent)
                    if (e.Name == name) { open = e.Parent; break; }
                i = tagEnd + 1;
                continue;
            }

            var tagText = text[nameEnd..Math.Min(tagEnd, text.Length)];
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in AttributeRx.Matches(tagText))
                attributes.TryAdd(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
            var element = new XamlElement(name, nameStart, attributes, open);
            if (root is null) { root = element; rootInsert = nameEnd; }

            if (offset <= tagEnd || tagEnd >= text.Length)
                return InTag(text, offset, element, open, root, rootInsert, nameStart, nameEnd);

            var selfClosing = tagEnd > 0 && text[tagEnd] == '>' && text[tagEnd - 1] == '/';
            if (text[tagEnd] == '>' && !selfClosing) open = element;
            i = text[tagEnd] == '>' ? tagEnd + 1 : tagEnd;   // a broken tag ends at the next '<'
        }
        return new XamlContext
        {
            Kind = XamlContextKind.Text, Element = open, PartialStart = offset,
            Namespaces = Namespaces(open, null), Root = root, XmlnsInsertOffset = rootInsert,
        };
    }

    /// <summary>The cursor is inside the start tag <paramref name="element"/> (name, attribute name, or value).</summary>
    static XamlContext InTag(string text, int offset, XamlElement element, XamlElement? parent, XamlElement root, int rootInsert, int nameStart, int nameEnd)
    {
        var ns = Namespaces(element, null);
        if (offset <= nameEnd)
            return new XamlContext
            {
                Kind = XamlContextKind.ElementName, Element = parent, Partial = text[nameStart..offset], PartialStart = nameStart,
                Namespaces = parent is null ? ns : Namespaces(parent, element), Root = root, XmlnsInsertOffset = rootInsert,
            };

        // Walk the attributes up to the cursor.
        var i = nameEnd;
        while (true)
        {
            while (i < offset && char.IsWhiteSpace(text[i])) i++;
            var attrStart = i;
            while (i < offset && IsNameChar(text[i])) i++;
            if (i >= offset)
                return new XamlContext
                {
                    Kind = XamlContextKind.AttributeName, Element = element, Partial = text[attrStart..offset], PartialStart = attrStart,
                    Namespaces = ns, Root = root, XmlnsInsertOffset = rootInsert,
                };
            var attr = text[attrStart..i];
            while (i < offset && char.IsWhiteSpace(text[i])) i++;
            if (i >= offset) return None(element, ns);
            if (text[i] != '=')
            {
                if (attr.Length == 0) i++;   // '/', stray characters
                continue;
            }
            i++;
            while (i < offset && char.IsWhiteSpace(text[i])) i++;
            if (i >= offset) return None(element, ns);
            var quote = text[i];
            if (quote is not ('"' or '\'')) { i++; continue; }
            var valueStart = i + 1;
            var valueEnd = text.IndexOf(quote, valueStart);
            if (valueEnd < 0 || valueEnd >= offset)
                return new XamlContext
                {
                    Kind = XamlContextKind.AttributeValue, Element = element, Attribute = attr, ValueStart = valueStart,
                    Partial = text[valueStart..offset], PartialStart = valueStart, Namespaces = ns, Root = root, XmlnsInsertOffset = rootInsert,
                };
            i = valueEnd + 1;
        }
    }

    static XamlContext None(XamlElement element, IReadOnlyDictionary<string, string> ns) =>
        new() { Kind = XamlContextKind.None, Element = element, Namespaces = ns };

    /// <summary>The '>' that ends a tag starting before <paramref name="from"/>, ignoring quoted '>'; a '&lt;' ends a broken tag.</summary>
    static int TagEnd(string text, int from)
    {
        char quote = '\0';
        for (var i = from; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '<') return i;   // never valid inside a tag or attribute value: the tag is broken
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '>') return i;
        }
        return text.Length;
    }

    /// <summary>xmlns declarations in scope at <paramref name="element"/> (inner declarations win), plus <paramref name="extra"/>'s own.</summary>
    static Dictionary<string, string> Namespaces(XamlElement? element, XamlElement? extra)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var chain = new List<XamlElement>();
        if (extra is not null) chain.Add(extra);
        if (element is not null) chain.AddRange(element.SelfAndAncestors());
        foreach (var e in chain)
            foreach (var (k, v) in e.Attributes)
            {
                if (k == "xmlns") map.TryAdd("", v);
                else if (k.StartsWith("xmlns:", StringComparison.Ordinal)) map.TryAdd(k[6..], v);
            }
        return map;
    }

    static bool Starts(string text, int i, string s) => string.CompareOrdinal(text, i, s, 0, s.Length) == 0;

    /// <summary>x:Key values in a XAML text (resource keys for StaticResource/DynamicResource).</summary>
    public static IEnumerable<string> Keys(string text) => KeyRx.Matches(text).Select(m => m.Groups[1].Value);

    /// <summary>x:Name / Name values in a XAML text (ElementName, #name).</summary>
    public static IEnumerable<string> Names(string text) => NameRx.Matches(text).Select(m => m.Groups[1].Value);

    static readonly Regex KeyRx = new("""\bx:Key\s*=\s*["']([^"'{}]+)["']""", RegexOptions.Compiled);
    static readonly Regex NameRx = new("""(?<![\w.:])(?:x:)?Name\s*=\s*["']([A-Za-z_]\w*)["']""", RegexOptions.Compiled);
}
