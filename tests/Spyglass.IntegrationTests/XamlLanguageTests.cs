using Spyglass.Protocol.Messages;
using Spyglass.XamlLanguage;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>
/// Avalonia XAML IntelliSense against real projects (design/xaml-language/README.md): the sample app (Avalonia + its own
/// view models and code-behind) and the Spyglass GUI, which uses the TKCode.Arc component library through its own xmlns URI.
/// References come from MSBuild like in the IDE; `|` marks the cursor.
/// </summary>
public sealed class XamlLanguageTests : IClassFixture<XamlLanguageTests.Service>
{
    public sealed class Service : IDisposable
    {
        public XamlLanguageService Xaml { get; } = new() { LoadWait = TimeSpan.FromMinutes(3) };
        public void Dispose() => Xaml.Dispose();
    }

    readonly XamlLanguageService _xaml;
    public XamlLanguageTests(Service s) => _xaml = s.Xaml;

    static readonly string Repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    static readonly string Sample = Path.Combine(Repo, "samples", "Spyglass.Sample", "Probe.axaml");
    static readonly string Gui = Path.Combine(Repo, "src", "Spyglass.App", "Probe.axaml");

    const string Head = """
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:local="clr-namespace:Spyglass.Sample"
                x:Class="Spyglass.Sample.MainWindow"
                x:DataType="local:MainViewModel">
        """;

    async Task<(XamlCompletionResult Result, string Text, int Offset)> Complete(string path, string withCursor)
    {
        var offset = withCursor.IndexOf('|');
        var text = withCursor.Remove(offset, 1);
        var r = await _xaml.CompleteAsync(new XamlCompletionParams(path, text, offset), TestContext.Current.CancellationToken);
        Assert.False(r.Incomplete, r.Message);
        Assert.Null(r.Message);
        return (r, text, offset);
    }

    static XamlCompletionItem Item(XamlCompletionResult r, string label) =>
        r.Items.FirstOrDefault(i => i.Label == label) ?? throw new Xunit.Sdk.XunitException(
            $"no '{label}' among {r.Items.Count}: {string.Join(", ", r.Items.Take(40).Select(i => i.Label))}");

    [Fact]
    public async Task Elements_come_from_the_avalonia_xmlns_and_the_projects_own_prefix()
    {
        var (r, text, offset) = await Complete(Sample, Head + "\n  <StackPanel>\n    <Bu|\n  </StackPanel>\n</Window>");
        Assert.Equal(offset - 2, r.From);
        Assert.Equal("element", Item(r, "Button").Kind);
        Item(r, "TextBlock");
        Item(r, "ItemsControl");
        Item(r, "x:String");
        Assert.DoesNotContain(r.Items, i => i.Label is "RangeBase" or "Orientation");   // abstract class, enum
        Item(r, "StackPanel.");                                                           // parent's property elements
        Assert.Equal("Bu", text[r.From..offset]);
    }

    [Fact]
    public async Task Attributes_in_the_real_sample_window()
    {
        var file = Path.Combine(Repo, "samples", "Spyglass.Sample", "MainWindow.axaml");
        var text = File.ReadAllText(file);
        var at = text.IndexOf("<StackPanel x:Name=\"Root\"", StringComparison.Ordinal) + 12;
        var (r, _, _) = await Complete(file, text.Insert(at, "|"));
        Item(r, "Orientation");
        Assert.DoesNotContain(r.Items, i => i.Label is "x:Name" or "Margin" or "Spacing");             // already set
        Assert.DoesNotContain(r.Items, i => i.Label is "Bounds" or "DesiredSize" or "IsEffectivelyEnabled");   // read-only
    }

    [Fact]
    public async Task Property_elements_after_the_owner()
    {
        var (r, _, _) = await Complete(Sample, Head + "\n  <Grid>\n    <Grid.|\n  </Grid>\n</Window>");
        Item(r, "Grid.RowDefinitions");
        Item(r, "Grid.Children");
        Assert.All(r.Items, i => Assert.StartsWith("Grid.", i.Label));
        Assert.DoesNotContain(r.Items, i => i.Label is "Grid.Bounds" or "Grid.DesiredSize");   // read-only, not a collection
    }

    [Fact]
    public async Task Attributes_are_properties_events_directives_and_attached_owners()
    {
        var (r, _, _) = await Complete(Sample, Head + "\n  <Button Content=\"Go\" |/>\n</Window>");
        var width = Item(r, "Width");
        Assert.Equal(("property", "double", "Width=\"$1\"", true), (width.Kind, width.Detail, width.InsertText, width.Snippet));
        Assert.Equal("P:Avalonia.Layout.Layoutable.Width", width.DocId);
        Assert.Equal("event", Item(r, "Click").Kind);
        Assert.Equal("directive", Item(r, "x:Name").Kind);
        Assert.Equal("owner", Item(r, "Grid.").Kind);
        Assert.DoesNotContain(r.Items, i => i.Label == "Content");   // already set
        Assert.DoesNotContain(r.Items, i => i.Label == "x:Class");   // root only
    }

    [Fact]
    public async Task Attached_properties_after_the_owner()
    {
        var (r, text, offset) = await Complete(Sample, Head + "\n  <Button Grid.R|/>\n</Window>");
        var row = Item(r, "Grid.Row");
        Assert.Equal(("attachedProperty", "Grid.Row=\"$1\""), (row.Kind, row.InsertText));
        Assert.Equal(offset - "Grid.R".Length, r.From);
        Item(r, "Grid.ColumnSpan");
        Assert.Equal("Grid.R", text[r.From..offset]);
    }

    [Fact]
    public async Task Values_for_enums_bools_and_brushes()
    {
        var (r, _, _) = await Complete(Sample, Head + "\n  <StackPanel Orientation=\"|\" />\n</Window>");
        Assert.Equal(["Horizontal", "Vertical"], r.Items.Select(i => i.Label).Order());
        (r, _, _) = await Complete(Sample, Head + "\n  <Button IsEnabled=\"|\" />\n</Window>");
        Assert.Equal(["False", "True"], r.Items.Select(i => i.Label).Order());
        (r, _, _) = await Complete(Sample, Head + "\n  <Border Background=\"|\" />\n</Window>");
        Item(r, "CornflowerBlue");
        (r, _, _) = await Complete(Sample, Head + "\n  <Button Grid.Row=\"1\" HorizontalAlignment=\"|\" />\n</Window>");
        Item(r, "Stretch");
    }

    [Fact]
    public async Task Markup_extensions_and_binding_paths_from_x_DataType()
    {
        var (r, _, _) = await Complete(Sample, Head + "\n  <TextBlock Text=\"{|\" />\n</Window>");
        Assert.Equal("markupExtension", Item(r, "Binding").Kind);
        Item(r, "StaticResource");
        Item(r, "DynamicResource");
        Item(r, "x:Static");

        (r, _, _) = await Complete(Sample, Head + "\n  <TextBlock Text=\"{Binding |}\" />\n</Window>");
        Item(r, "Title");
        Item(r, "User");
        Item(r, "Mode=");

        (r, _, var offset) = await Complete(Sample, Head + "\n  <TextBlock Text=\"{Binding User.Fu|}\" />\n</Window>");
        Item(r, "FullName");
        Assert.Equal(offset - 2, r.From);

        (r, _, _) = await Complete(Sample, Head + "\n  <TextBlock Text=\"{Binding Title, Mode=|}\" />\n</Window>");
        Item(r, "TwoWay");
        Item(r, "OneWay");
    }

    [Fact]
    public async Task Event_handlers_come_from_the_code_behind()
    {
        var (r, _, _) = await Complete(Sample, Head + "\n  <Button Click=\"|\" />\n</Window>");
        Assert.Equal("method", Item(r, "OnNewWindow").Kind);
    }

    [Fact]
    public async Task Setter_properties_and_values_follow_the_style_selector()
    {
        var (r, _, _) = await Complete(Sample, Head + "\n  <Window.Styles><Style Selector=\"StackPanel > TextBlock.h1\"><Setter Property=\"|\" /></Style></Window.Styles>\n</Window>");
        Item(r, "FontSize");
        Item(r, "TextWrapping");
        (r, _, _) = await Complete(Sample, Head + "\n  <Window.Styles><Style Selector=\"TextBlock\"><Setter Property=\"TextWrapping\" Value=\"|\" /></Style></Window.Styles>\n</Window>");
        Item(r, "Wrap");
    }

    [Fact]
    public async Task Resource_keys_and_element_names()
    {
        var doc = Head + "\n  <Window.Resources><SolidColorBrush x:Key=\"Accent\" Color=\"Red\" /></Window.Resources>\n  <TextBox x:Name=\"Input\" />\n  <Border Background=\"{StaticResource |}\" />\n  <TextBlock Text=\"{Binding Text, ElementName=}\" />\n</Window>";
        var (r, _, _) = await Complete(Sample, doc);
        Assert.Equal("resource", Item(r, "Accent").Kind);
        (r, _, _) = await Complete(Sample, doc.Replace("{StaticResource |}", "{StaticResource Accent}").Replace("ElementName=}", "ElementName=|}"));
        Assert.Equal("name", Item(r, "Input").Kind);
    }

    [Fact]
    public async Task Closing_tag_and_xmlns_values()
    {
        var (r, _, _) = await Complete(Sample, Head + "\n  <StackPanel>\n  </|\n</Window>");
        Assert.Equal("StackPanel>", Item(r, "StackPanel").InsertText);
        (r, _, _) = await Complete(Sample, "<Window xmlns=\"|\" />");
        Item(r, "https://github.com/avaloniaui");
        Item(r, "using:Spyglass.Sample");
    }

    [Fact]
    public async Task Component_library_elements_from_its_xmlns_uri()
    {
        const string head = """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:arc="https://github.com/AIDotNet/TKCode.Arc">
            """;
        var (r, _, _) = await Complete(Gui, head + "\n  <arc:|\n</UserControl>");
        var arc = r.Items.Where(i => i.Label.StartsWith("arc:", StringComparison.Ordinal)).ToList();
        Assert.True(arc.Count > 10, $"only {arc.Count} Arc elements");
        Assert.Contains(arc, i => i.Detail?.StartsWith("TKCode.Arc", StringComparison.Ordinal) == true);

        // Arc's markup extension, as used in the GUI's App.axaml.
        (r, _, _) = await Complete(Gui, head + "\n  <Border CornerRadius=\"{arc:|}\" />\n</UserControl>");
        Item(r, "arc:ArcDesignTokenResource");
    }

    [Fact]
    public async Task Undeclared_component_library_elements_add_their_xmlns()
    {
        const string head = """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            """;
        var (r, text, _) = await Complete(Gui, head + "\n  <StackPanel>\n    <|\n  </StackPanel>\n</UserControl>");
        var lib = r.Items.Where(i => i.AdditionalEdit?.Text.Contains("https://github.com/AIDotNet/TKCode.Arc", StringComparison.Ordinal) == true).ToList();
        Assert.NotEmpty(lib);
        var edit = lib[0].AdditionalEdit!;
        Assert.Equal(text.IndexOf("UserControl", StringComparison.Ordinal) + "UserControl".Length, edit.Offset);
        var prefix = lib[0].Label[..lib[0].Label.IndexOf(':')];
        Assert.Equal($" xmlns:{prefix}=\"https://github.com/AIDotNet/TKCode.Arc\"", edit.Text);
    }

    [Fact]
    public async Task Hover_and_documentation_come_from_the_xml_docs()
    {
        var text = Head + "\n  <StackPanel Orientation=\"Vertical\" />\n</Window>";
        var at = text.IndexOf("Orientation", StringComparison.Ordinal) + 3;
        var h = await _xaml.HoverAsync(new XamlHoverParams(Sample, text, at), TestContext.Current.CancellationToken);
        Assert.NotNull(h.Markdown);
        Assert.Contains("StackPanel.Orientation", h.Markdown);
        Assert.Contains("`Vertical`", h.Markdown);
        Assert.Equal("Orientation", text[h.From..h.To]);

        at = text.IndexOf("StackPanel", StringComparison.Ordinal) + 2;
        h = await _xaml.HoverAsync(new XamlHoverParams(Sample, text, at), TestContext.Current.CancellationToken);
        Assert.Contains("Avalonia.Controls.StackPanel", h.Markdown);

        var d = await _xaml.DocumentationAsync(new XamlDocumentationParams(Sample, "T:Avalonia.Controls.Button"), TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(d.Markdown));
    }

    [Fact]
    public async Task Nothing_in_comments_and_text()
    {
        var (r, _, _) = await Complete(Sample, Head + "\n  <!-- <Bu| -->\n</Window>");
        Assert.Empty(r.Items);
        (r, _, _) = await Complete(Sample, Head + "\n  <TextBlock>Hello |</TextBlock>\n</Window>");
        Assert.Empty(r.Items);
    }
}
