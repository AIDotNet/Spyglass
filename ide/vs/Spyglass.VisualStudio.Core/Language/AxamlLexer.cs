namespace Spyglass.VisualStudio.Core.Language;

/// <summary>What a span of .axaml text is, for syntax highlighting (the VS Code grammar distinguishes the same things).</summary>
public enum AxamlTokenKind
{
    /// <summary>&lt; &lt;/ &gt; /&gt; = : . and the quotes around attribute values.</summary>
    Delimiter,
    /// <summary>local in &lt;local:Card.</summary>
    ElementPrefix,
    /// <summary>x in x:Name, xmlns in xmlns:local, local in local:Ext.Hint.</summary>
    AttributePrefix,
    /// <summary>x in {x:Static}, arc in {arc:Token}.</summary>
    ExtensionPrefix,
    ElementName,
    /// <summary>RowDefinitions in &lt;Grid.RowDefinitions&gt;.</summary>
    PropertyElement,
    AttributeName,
    /// <summary>Grid in Grid.Row="1".</summary>
    AttachedOwner,
    /// <summary>Name in x:Name, Class in x:Class…</summary>
    Directive,
    AttributeValue,
    /// <summary>{ } of a markup extension.</summary>
    MarkupBrace,
    /// <summary>Binding, StaticResource, x:Static…</summary>
    MarkupExtension,
    /// <summary>Mode in {Binding Mode=TwoWay}.</summary>
    MarkupArgument,
    /// <summary>, and = inside a markup extension.</summary>
    MarkupPunctuation,
    /// <summary>{} at the start of a value, \x inside a markup extension.</summary>
    Escape,
    Comment,
    CData,
    Entity,
    ProcessingInstruction,
}

public readonly record struct AxamlToken(int Start, int Length, AxamlTokenKind Kind)
{
    public int End => Start + Length;
}

/// <summary>
/// A tolerant lexer for Avalonia XAML: the whole text in one pass (comments, tags and attribute values span lines), never
/// throws, and copes with what half-typed markup looks like (unclosed tags, quotes, comments and markup extensions).
/// </summary>
public static class AxamlLexer
{
    static readonly HashSet<string> Directives = new(StringComparer.Ordinal)
    {
        "Class", "ClassModifier", "Name", "Key", "DataType", "CompileBindings", "FieldModifier", "Shared", "TypeArguments", "Uid", "FactoryMethod", "Arguments",
    };

    public static List<AxamlToken> Tokenize(string text)
    {
        var tokens = new List<AxamlToken>();
        var i = 0;
        var n = text.Length;
        void Add(int start, int end, AxamlTokenKind kind) { if (end > start) tokens.Add(new AxamlToken(start, end - start, kind)); }

        while (i < n)
        {
            var c = text[i];
            if (c == '&') { i = Entity(text, i, n, Add); continue; }
            if (c != '<') { i++; continue; }

            if (At(text, i, "<!--"))
            {
                var end = IndexOrEnd(text, "-->", i + 4, 3);
                Add(i, end, AxamlTokenKind.Comment);
                i = end;
            }
            else if (At(text, i, "<![CDATA["))
            {
                Add(i, i + 9, AxamlTokenKind.Delimiter);
                var close = text.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                var contentEnd = close < 0 ? n : close;
                Add(i + 9, contentEnd, AxamlTokenKind.CData);
                if (close >= 0) Add(close, close + 3, AxamlTokenKind.Delimiter);
                i = close < 0 ? n : close + 3;
            }
            else if (At(text, i, "<?") || At(text, i, "<!"))
            {
                var end = IndexOrEnd(text, ">", i + 2, 1);
                Add(i, end, AxamlTokenKind.ProcessingInstruction);
                i = end;
            }
            else i = Tag(text, i, n, Add);
        }
        return tokens;
    }

    delegate void Emit(int start, int end, AxamlTokenKind kind);

    static int Tag(string text, int i, int n, Emit add)
    {
        var open = text.Length > i + 1 && text[i + 1] == '/' ? 2 : 1;
        add(i, i + open, AxamlTokenKind.Delimiter);
        i += open;
        var nameEnd = NameEnd(text, i, n);
        ElementName(text, i, nameEnd, add);
        i = nameEnd;

        while (i < n)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '>') { add(i, i + 1, AxamlTokenKind.Delimiter); return i + 1; }
            if (c == '/' && i + 1 < n && text[i + 1] == '>') { add(i, i + 2, AxamlTokenKind.Delimiter); return i + 2; }
            if (c == '<') return i;   // a broken tag: the next one starts here
            if (!IsNameChar(c)) { i++; continue; }

            var attrEnd = NameEnd(text, i, n);
            AttributeName(text, i, attrEnd, add);
            i = attrEnd;
            while (i < n && char.IsWhiteSpace(text[i])) i++;
            if (i >= n || text[i] != '=') continue;
            add(i, i + 1, AxamlTokenKind.Delimiter);
            i++;
            while (i < n && char.IsWhiteSpace(text[i])) i++;
            if (i >= n || text[i] is not ('"' or '\'')) continue;
            var quote = text[i];
            add(i, i + 1, AxamlTokenKind.Delimiter);
            var valueStart = i + 1;
            var valueEnd = valueStart;
            while (valueEnd < n && text[valueEnd] != quote && text[valueEnd] != '<') valueEnd++;
            Value(text, valueStart, valueEnd, add);
            if (valueEnd < n && text[valueEnd] == quote) { add(valueEnd, valueEnd + 1, AxamlTokenKind.Delimiter); i = valueEnd + 1; }
            else i = valueEnd;
        }
        return i;
    }

    /// <summary>prefix:Owner.Property → Prefix, ':', ElementName, '.', PropertyElement.</summary>
    static void ElementName(string text, int start, int end, Emit add)
    {
        var colon = text.IndexOf(':', start, end - start);
        var local = start;
        if (colon >= 0)
        {
            add(start, colon, AxamlTokenKind.ElementPrefix);
            add(colon, colon + 1, AxamlTokenKind.Delimiter);
            local = colon + 1;
        }
        var dot = text.IndexOf('.', local, end - local);
        if (dot < 0) { add(local, end, AxamlTokenKind.ElementName); return; }
        add(local, dot, AxamlTokenKind.ElementName);
        add(dot, dot + 1, AxamlTokenKind.Delimiter);
        add(dot + 1, end, AxamlTokenKind.PropertyElement);
    }

    /// <summary>x:Name (directive), Grid.Row / local:Ext.Hint (attached), xmlns:local, Width.</summary>
    static void AttributeName(string text, int start, int end, Emit add)
    {
        var colon = text.IndexOf(':', start, end - start);
        var local = start;
        if (colon >= 0)
        {
            add(start, colon, AxamlTokenKind.AttributePrefix);
            add(colon, colon + 1, AxamlTokenKind.Delimiter);
            local = colon + 1;
            if (text.AsSpan(start, colon - start).SequenceEqual("x") && Directives.Contains(text[local..end]))
            {
                add(local, end, AxamlTokenKind.Directive);
                return;
            }
        }
        var dot = text.LastIndexOf('.', end - 1, end - local);
        if (dot > local)
        {
            add(local, dot, AxamlTokenKind.AttachedOwner);
            add(dot, dot + 1, AxamlTokenKind.Delimiter);
            add(dot + 1, end, AxamlTokenKind.AttributeName);
        }
        else add(local, end, AxamlTokenKind.AttributeName);
    }

    static void Value(string text, int start, int end, Emit add)
    {
        if (end - start >= 2 && text[start] == '{' && text[start + 1] == '}')
        {
            add(start, start + 2, AxamlTokenKind.Escape);
            Plain(text, start + 2, end, add);
        }
        else if (start < end && text[start] == '{') MarkupExtension(text, start, end, add);
        else Plain(text, start, end, add);
    }

    /// <summary>Attribute value text with its entities (&amp;amp; &amp;#x20;).</summary>
    static void Plain(string text, int start, int end, Emit add)
    {
        var run = start;
        for (var i = start; i < end;)
        {
            if (text[i] != '&') { i++; continue; }
            add(run, i, AxamlTokenKind.AttributeValue);
            i = Entity(text, i, end, add);
            run = i;
        }
        add(run, end, AxamlTokenKind.AttributeValue);
    }

    /// <summary>{Name arg, Key=Value, Key={Nested …}, Key='quoted'}; stops at <paramref name="end"/> (the attribute's quote).</summary>
    static int MarkupExtension(string text, int i, int end, Emit add)
    {
        add(i, i + 1, AxamlTokenKind.MarkupBrace);
        i++;
        while (i < end && char.IsWhiteSpace(text[i])) i++;
        var nameEnd = i;
        while (nameEnd < end && (char.IsLetterOrDigit(text[nameEnd]) || text[nameEnd] is '_' or ':' or '.')) nameEnd++;
        var colon = text.IndexOf(':', i, nameEnd - i);
        if (colon >= 0)
        {
            add(i, colon, AxamlTokenKind.ExtensionPrefix);
            add(colon, colon + 1, AxamlTokenKind.Delimiter);
            add(colon + 1, nameEnd, AxamlTokenKind.MarkupExtension);
        }
        else add(i, nameEnd, AxamlTokenKind.MarkupExtension);
        i = nameEnd;

        while (i < end)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            switch (c)
            {
                case '}':
                    add(i, i + 1, AxamlTokenKind.MarkupBrace);
                    return i + 1;
                case '{':
                    i = MarkupExtension(text, i, end, add);
                    continue;
                case ',':
                case '=':
                    add(i, i + 1, AxamlTokenKind.MarkupPunctuation);
                    i++;
                    continue;
                case '\'':
                {
                    var close = text.IndexOf('\'', i + 1, end - i - 1);
                    var stop = close < 0 ? end : close + 1;
                    add(i, stop, AxamlTokenKind.AttributeValue);
                    i = stop;
                    continue;
                }
                case '\\':
                    add(i, Math.Min(i + 2, end), AxamlTokenKind.Escape);
                    i += 2;
                    continue;
                case '&':
                    i = Entity(text, i, end, add);
                    continue;
            }
            // A run: an argument name if '=' follows, else a value (binding path, resource key, type…).
            var runEnd = i;
            while (runEnd < end && text[runEnd] is not ('}' or '{' or ',' or '=' or '\'' or '\\' or '&') && !char.IsWhiteSpace(text[runEnd])) runEnd++;
            var next = runEnd;
            while (next < end && char.IsWhiteSpace(text[next])) next++;
            add(i, runEnd, next < end && text[next] == '=' ? AxamlTokenKind.MarkupArgument : AxamlTokenKind.AttributeValue);
            i = runEnd;
        }
        return i;
    }

    /// <summary>&amp;name; or &amp;#123; → Entity; a bare &amp; is skipped.</summary>
    static int Entity(string text, int i, int end, Emit add)
    {
        var j = i + 1;
        while (j < end && j - i <= 32 && (char.IsLetterOrDigit(text[j]) || text[j] == '#')) j++;
        if (j < end && text[j] == ';' && j > i + 1)
        {
            add(i, j + 1, AxamlTokenKind.Entity);
            return j + 1;
        }
        return i + 1;
    }

    static int NameEnd(string text, int i, int n)
    {
        while (i < n && IsNameChar(text[i])) i++;
        return i;
    }

    static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or ':' or '.' or '-';

    static bool At(string text, int i, string s) => string.CompareOrdinal(text, i, s, 0, s.Length) == 0;

    static int IndexOrEnd(string text, string s, int from, int length)
    {
        var at = text.IndexOf(s, from, StringComparison.Ordinal);
        return at < 0 ? text.Length : at + length;
    }
}
