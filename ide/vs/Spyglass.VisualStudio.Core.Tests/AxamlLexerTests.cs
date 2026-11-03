using Spyglass.VisualStudio.Core.Language;
using Xunit;

namespace Spyglass.VisualStudio.Core.Tests;

/// <summary>Syntax highlighting tokens for Visual Studio (the same constructs as the VS Code grammar).</summary>
public class AxamlLexerTests
{
    static List<(string Text, AxamlTokenKind Kind)> Lex(string text) =>
        AxamlLexer.Tokenize(text).Select(t => (text.Substring(t.Start, t.Length), t.Kind)).ToList();

    static AxamlTokenKind KindOf(List<(string Text, AxamlTokenKind Kind)> tokens, string text, int nth = 0)
    {
        var hits = tokens.Where(t => t.Text == text).ToList();
        Assert.True(hits.Count > nth, $"no token '{text}' in: {string.Join(" | ", tokens.Select(t => $"{t.Text}:{t.Kind}"))}");
        return hits[nth].Kind;
    }

    [Fact]
    public void Tags_attributes_and_values()
    {
        var t = Lex("""<local:Card xmlns:local="using:App" Title="Hi &amp; bye" />""");
        Assert.Equal(AxamlTokenKind.ElementPrefix, KindOf(t, "local"));
        Assert.Equal(AxamlTokenKind.ElementName, KindOf(t, "Card"));
        Assert.Equal(AxamlTokenKind.AttributePrefix, KindOf(t, "xmlns"));
        Assert.Equal(AxamlTokenKind.AttributeName, KindOf(t, "local", 1));
        Assert.Equal(AxamlTokenKind.AttributeName, KindOf(t, "Title"));
        Assert.Equal(AxamlTokenKind.AttributeValue, KindOf(t, "Hi "));
        Assert.Equal(AxamlTokenKind.Entity, KindOf(t, "&amp;"));
        Assert.Equal(AxamlTokenKind.Delimiter, KindOf(t, "<"));
        Assert.Equal(AxamlTokenKind.Delimiter, KindOf(t, "/>"));
    }

    [Fact]
    public void Property_elements_attached_properties_and_directives()
    {
        var t = Lex("<Grid.RowDefinitions>\n  <Button x:Name=\"Go\" Grid.Row=\"1\" x:Foo=\"1\" />\n</Grid.RowDefinitions>");
        Assert.Equal(AxamlTokenKind.ElementName, KindOf(t, "Grid"));
        Assert.Equal(AxamlTokenKind.PropertyElement, KindOf(t, "RowDefinitions"));
        Assert.Equal(AxamlTokenKind.PropertyElement, KindOf(t, "RowDefinitions", 1));   // closing tag
        Assert.Equal(AxamlTokenKind.Directive, KindOf(t, "Name"));
        Assert.Equal(AxamlTokenKind.AttachedOwner, KindOf(t, "Grid", 1));
        Assert.Equal(AxamlTokenKind.AttributeName, KindOf(t, "Row"));
        Assert.Equal(AxamlTokenKind.AttributeName, KindOf(t, "Foo"));   // not a known x: directive
    }

    [Fact]
    public void Markup_extensions_with_arguments_nesting_and_quotes()
    {
        var t = Lex("""<TextBlock Text="{Binding User.Name, Mode=OneWay, Converter={StaticResource Up}, StringFormat='{}{0:N2}'}" Tag="{x:Static local:S.A}" />""");
        Assert.Equal(AxamlTokenKind.MarkupBrace, KindOf(t, "{"));
        Assert.Equal(AxamlTokenKind.MarkupExtension, KindOf(t, "Binding"));
        Assert.Equal(AxamlTokenKind.AttributeValue, KindOf(t, "User.Name"));
        Assert.Equal(AxamlTokenKind.MarkupPunctuation, KindOf(t, ","));
        Assert.Equal(AxamlTokenKind.MarkupArgument, KindOf(t, "Mode"));
        Assert.Equal(AxamlTokenKind.Delimiter, KindOf(t, "="));               // Text=
        Assert.Equal(AxamlTokenKind.MarkupPunctuation, KindOf(t, "=", 1));    // Mode=
        Assert.Equal(AxamlTokenKind.AttributeValue, KindOf(t, "OneWay"));
        Assert.Equal(AxamlTokenKind.MarkupExtension, KindOf(t, "StaticResource"));
        Assert.Equal(AxamlTokenKind.AttributeValue, KindOf(t, "'{}{0:N2}'"));
        Assert.Equal(AxamlTokenKind.ExtensionPrefix, KindOf(t, "x"));
        Assert.Equal(AxamlTokenKind.MarkupExtension, KindOf(t, "Static"));
        Assert.Equal(AxamlTokenKind.Delimiter, t[^1].Kind);   // "/>": the tag still ends
    }

    [Fact]
    public void Escaped_braces_are_a_literal()
    {
        var t = Lex("""<TextBlock Text="{}{literal}" />""");
        Assert.Equal(AxamlTokenKind.Escape, KindOf(t, "{}"));
        Assert.Equal(AxamlTokenKind.AttributeValue, KindOf(t, "{literal}"));
        Assert.DoesNotContain(t, x => x.Kind == AxamlTokenKind.MarkupExtension);
    }

    [Fact]
    public void Comments_cdata_and_processing_instructions_span_lines()
    {
        var t = Lex("<?xml version=\"1.0\"?>\n<!-- a\n <Grid.Row> {Binding} -->\n<x:String><![CDATA[a < b]]></x:String>");
        Assert.Equal(AxamlTokenKind.ProcessingInstruction, KindOf(t, "<?xml version=\"1.0\"?>"));
        Assert.Equal(AxamlTokenKind.Comment, KindOf(t, "<!-- a\n <Grid.Row> {Binding} -->"));
        Assert.Equal(AxamlTokenKind.CData, KindOf(t, "a < b"));
        Assert.DoesNotContain(t, x => x.Kind == AxamlTokenKind.PropertyElement);
    }

    [Fact]
    public void Half_typed_markup_never_bleeds_or_throws()
    {
        // Unclosed extension: ends at the attribute's quote; the next attribute is still an attribute.
        var t = Lex("""<TextBlock Text="{Binding Na" Foreground="Red" />""");
        Assert.Equal(AxamlTokenKind.AttributeName, KindOf(t, "Foreground"));
        // Unclosed quote: the next tag still lexes.
        t = Lex("<Button Content=\"Go\n<TextBlock Text=\"x\" />");
        Assert.Equal(AxamlTokenKind.ElementName, KindOf(t, "TextBlock"));
        // Unclosed comment and tag at the end of the text.
        Assert.Equal(AxamlTokenKind.Comment, Lex("<!-- open").Single().Kind);
        foreach (var s in new[] { "<", "</", "<A ", "<A B", "<A B=", "<A B=\"", "<A B=\"{", "<A B=\"{X ", "<A B='{X Y=", "&", "&amp", "<![CDATA[", "<?" })
            AxamlLexer.Tokenize(s);
    }

    [Fact]
    public void Tokens_are_ordered_and_never_overlap()
    {
        var text = File.Exists(SampleWindow) ? File.ReadAllText(SampleWindow) : """<Window xmlns="https://github.com/avaloniaui"><TextBlock Text="{Binding A}" /></Window>""";
        var tokens = AxamlLexer.Tokenize(text);
        Assert.NotEmpty(tokens);
        for (var i = 1; i < tokens.Count; i++) Assert.True(tokens[i].Start >= tokens[i - 1].End, $"overlap at {tokens[i].Start}");
        Assert.All(tokens, x => Assert.True(x.Length > 0 && x.End <= text.Length));
    }

    static string SampleWindow => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../samples/Spyglass.Sample/MainWindow.axaml"));
}
