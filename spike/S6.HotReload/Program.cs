using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace S6.HotReload;

public static class Program
{
    static readonly List<(string Id, string Name, bool Ok, string Detail)> Results = new();
    static void Check(string id, string name, Func<string> body)
    {
        try { Results.Add((id, name, true, body())); }
        catch (Exception ex) { Results.Add((id, name, false, $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}")); }
    }
    static void Pump() => Dispatcher.UIThread.RunJobs();
    static readonly Assembly Asm = typeof(Program).Assembly;

    const string CardV2 = """
        <UserControl xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:local="clr-namespace:S6.HotReload"
                     x:Class="S6.HotReload.CardView"
                     x:DataType="local:CardViewModel">
          <UserControl.Styles>
            <Style Selector="TextBlock.title"><Setter Property="FontSize" Value="32" /></Style>
          </UserControl.Styles>
          <UserControl.Resources><x:String x:Key="Greeting">v2</x:String></UserControl.Resources>
          <StackPanel x:Name="Root" Spacing="8">
            <TextBlock x:Name="Title" Classes="title" Text="{Binding Title}" />
            <TextBlock x:Name="Subtitle" Text="{Binding Subtitle}" />
            <Button x:Name="SaveButton" Content="Save v2" Click="OnSave" />
          </StackPanel>
        </UserControl>
        """;

    public static void Main(string[] args)
    {
        if (args.Contains("--probe"))
        {
            foreach (var t in new[] { typeof(CardView), typeof(HostWindow), typeof(App) })
                foreach (var m in t.GetMembers(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (m.Name.StartsWith("!") || m.Name.Contains("Xaml") || m.Name == "InitializeComponent")
                        Console.WriteLine($"{t.Name}: {m.MemberType} {m.Name} : {(m as FieldInfo)?.FieldType.ToString() ?? (m as MethodInfo)?.ToString()}");
            return;
        }
        if (args.Contains("--r8")) { R8Desktop.Run(args); return; }
        AppBuilder.Configure<App>().UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        if (args.Contains("--r34")) { R34.Run(); return; }
        if (args.Contains("--r7")) { R7Memory.Run(); return; }

        var window = new HostWindow();
        window.Show();
        var card = window.FindControl<CardView>("Card")!;
        card.DataContext = new CardViewModel { Title = "Hello", Subtitle = "World" };
        var input = window.FindControl<TextBox>("Input")!;
        input.Text = "user typed this";
        Pump(); window.CaptureRenderedFrame(); Pump();

        var oldTitle = card.FindControl<TextBlock>("Title")!;
        var stylesBefore = card.Styles.Count;
        var uri = new Uri("avares://S6.HotReload/CardView.axaml");

        // H1: naive re-populate of the live instance (no reset)
        long ms = 0;
        Check("H1", "Load(xaml, asm, rootInstance) re-populates live UserControl", () =>
        {
            var sw = Stopwatch.StartNew();
            AvaloniaRuntimeXamlLoader.Load(CardV2, Asm, card, uri, designMode: false);
            Pump(); window.CaptureRenderedFrame(); Pump();
            ms = sw.ElapsedMilliseconds;
            var sub = card.FindControl<TextBlock>("Subtitle");
            return $"{ms} ms (first, cold); Subtitle present={sub != null}; Save content='{card.FindControl<Button>("SaveButton")?.Content}'";
        });
        Check("H2", "Instance identity, DataContext, sibling state preserved", () =>
            $"same Card instance={ReferenceEquals(window.FindControl<CardView>("Card"), card)}; DataContext kept={card.DataContext is CardViewModel}; TextBox.Text='{input.Text}'");
        Check("H3", "Compiled bindings (x:DataType) under runtime loader", () =>
            $"Title.Text='{card.FindControl<TextBlock>("Title")!.Text}', Subtitle.Text='{card.FindControl<TextBlock>("Subtitle")?.Text}'");
        Check("H4", "Styles: appended or replaced? new style effective?", () =>
            $"Styles.Count before={stylesBefore} after={card.Styles.Count}; Title.FontSize={card.FindControl<TextBlock>("Title")!.FontSize}");
        Check("H5", "Resources updated", () => $"Greeting={card.Resources["Greeting"]}");
        Check("H6", "XAML event handler (Click=OnSave, private) wired", () =>
        {
            var b = card.FindControl<Button>("SaveButton")!;
            b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return $"Saves={card.Saves}";
        });
        Check("H7", "x:Name generated fields after reload", () =>
        {
            var f = typeof(CardView).GetField("Title", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null) return "no generated field 'Title'";
            var v = f.GetValue(card);
            return $"field points to old={ReferenceEquals(v, oldTitle)}, new={ReferenceEquals(v, card.FindControl<TextBlock>("Title"))}";
        });
        Check("H8", "Name scope: FindControl returns new controls (not stale)", () =>
            $"FindControl(Title) is old instance={ReferenceEquals(card.FindControl<TextBlock>("Title"), oldTitle)}; old detached={TopLevel.GetTopLevel(oldTitle) == null}");


        // H7b: refresh generated x:Name fields from the name scope
        Check("H7b", "Refresh x:Name fields via reflection + NameScope", () =>
        {
            int n = 0;
            var ns = Avalonia.Controls.NameScope.GetNameScope(card)!;
            foreach (var f in typeof(CardView).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                if (ns.Find(f.Name) is { } c && f.FieldType.IsInstanceOfType(c)) { f.SetValue(card, c); n++; }
            var t = typeof(CardView).GetField("Title", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
            return $"{n} fields refreshed; Title field is live={ReferenceEquals(t.GetValue(card), card.FindControl<TextBlock>("Title"))}";
        });

        // H14: runtime (not compile-time) failure mid-populate -> roll back by reloading last-good XAML
        Check("H14", "Runtime populate failure (missing StaticResource) then rollback", () =>
        {
            var bad = CardV2.Replace("Text=\"{Binding Subtitle}\"", "Text=\"{StaticResource DoesNotExist}\"");
            card.Styles.Clear(); card.Resources.Clear();
            string err;
            try { AvaloniaRuntimeXamlLoader.Load(bad, Asm, card, uri, false); err = "NO EXCEPTION"; }
            catch (Exception ex) { var xe = ex as System.Xml.XmlException; err = $"{ex.GetType().Name} line={xe?.LineNumber}"; }
            var half = $"content={card.Content?.GetType().Name ?? "null"} Subtitle={card.FindControl<TextBlock>("Subtitle") != null}";
            card.Styles.Clear(); card.Resources.Clear();
            AvaloniaRuntimeXamlLoader.Load(CardV2, Asm, card, uri, false); Pump();
            return $"{err}; after failure: {half}; after rollback Subtitle.Text='{card.FindControl<TextBlock>("Subtitle")?.Text}'";
        });
        // H9: reset-then-reload (clear Styles/Resources first) to avoid style accumulation
        Check("H9", "Reset (Styles.Clear, Resources.Clear) then reload x3: stable counts + warm timing", () =>
        {
            var times = new List<long>();
            for (int i = 0; i < 3; i++)
            {
                var sw = Stopwatch.StartNew();
                card.Styles.Clear(); card.Resources.Clear();
                AvaloniaRuntimeXamlLoader.Load(CardV2, Asm, card, uri, designMode: false);
                Pump();
                times.Add(sw.ElapsedMilliseconds);
            }
            window.CaptureRenderedFrame();
            return $"Styles.Count={card.Styles.Count}; warm ms=[{string.Join(",", times)}]";
        });

        // H10: broken XAML -> error with line info; is instance left intact?
        Check("H10", "Broken XAML: error line info + instance state after failure", () =>
        {
            var broken = CardV2.Replace("<TextBlock x:Name=\"Subtitle\"", "<TextBlok x:Name=\"Subtitle\"");
            try { AvaloniaRuntimeXamlLoader.Load(broken, Asm, card, uri, false); return "NO EXCEPTION"; }
            catch (Exception ex)
            {
                var xe = ex as System.Xml.XmlException ?? ex.InnerException as System.Xml.XmlException;
                return $"{ex.GetType().Name} line={xe?.LineNumber} pos={xe?.LinePosition}; card still has content={card.Content != null}, Subtitle={card.FindControl<TextBlock>("Subtitle") != null}";
            }
        });
        Check("H10b", "Binding to missing property (compiled) -> compile error?", () =>
        {
            var bad = CardV2.Replace("{Binding Subtitle}", "{Binding Nope}");
            card.Styles.Clear(); card.Resources.Clear();
            try { AvaloniaRuntimeXamlLoader.Load(bad, Asm, card, uri, false); return "NO EXCEPTION"; }
            catch (Exception ex) { var xe = ex as System.Xml.XmlException; return $"{ex.GetType().Name} line={xe?.LineNumber}: {ex.Message.Split('\n')[0]}"; }
        });


        Check("H10c", "Config overload: UseCompiledBindingsByDefault=true -> missing property is a compile error", () =>
        {
            var bad = CardV2.Replace("{Binding Subtitle}", "{Binding Nope}");
            card.Styles.Clear(); card.Resources.Clear();
            var cfg = new RuntimeXamlLoaderConfiguration { LocalAssembly = Asm, UseCompiledBindingsByDefault = true };
            try
            {
                AvaloniaRuntimeXamlLoader.Load(new RuntimeXamlLoaderDocument(uri, card, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bad))), cfg);
                return "NO EXCEPTION";
            }
            catch (Exception ex)
            {
                var xe = ex as System.Xml.XmlException ?? (ex as AggregateException)?.InnerExceptions.OfType<System.Xml.XmlException>().FirstOrDefault();
                return $"{ex.GetType().Name} line={xe?.LineNumber}: {(xe?.Message ?? ex.Message).Split('\n')[0]}; Subtitle still present={card.FindControl<TextBlock>("Subtitle") != null}";
            }
        });
        // H11: validate first without touching the live instance: parse into a throwaway (rootInstance=null)
        Check("H11", "Dry-run: Load(xaml) with x:Class but no rootInstance", () =>
        {
            var o = AvaloniaRuntimeXamlLoader.Load(CardV2, Asm, null, uri, false);
            return $"returned {o.GetType().FullName}";
        });
        Check("H11b", "Dry-run via Parse<UserControl> after stripping x:Class", () =>
        {
            var o = AvaloniaRuntimeXamlLoader.Load(CardV2.Replace("x:Class=\"S6.HotReload.CardView\"", ""), Asm, null, uri, false);
            return $"returned {o.GetType().FullName}";
        });

        // H12: Window root reload (Title/size + nested custom control)
        Check("H12", "Reload Window root (Title, nested CardView recreated)", () =>
        {
            var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../HostWindow.axaml")).Replace("Title=\"Before\"", "Title=\"After\"");
            AvaloniaRuntimeXamlLoader.Load(xaml, Asm, window, new Uri("avares://S6.HotReload/HostWindow.axaml"), false);
            Pump();
            var newCard = window.FindControl<CardView>("Card");
            return $"Title={window.Title}; Card recreated={!ReferenceEquals(newCard, card)}; TextBox.Text='{window.FindControl<TextBox>("Input")?.Text}' (state lost expected)";
        });

        // H13: find all live instances of a type across top levels
        Check("H13", "Enumerate live instances of x:Class type", () =>
        {
            var n = window.GetVisualDescendants().OfType<CardView>().Count();
            return $"{n} CardView instance(s) in window";
        });

        foreach (var r in Results)
            Console.WriteLine($"{(r.Ok ? "PASS" : "FAIL"),-4} {r.Id,-5} {r.Name}\n           {r.Detail}");
    }
}
