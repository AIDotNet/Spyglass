using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace S6.HotReload;

/// R3 (!XamlIlPopulateOverride for future instances) and R4 (is Styles/Resources.Clear enough?).
static class R34
{
    static readonly List<(string Id, string Name, bool Ok, string Detail)> Results = new();
    static void Check(string id, string name, Func<string> body)
    {
        try { Results.Add((id, name, true, body())); }
        catch (Exception ex) { Results.Add((id, name, false, $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}")); }
    }
    static void Pump() => Dispatcher.UIThread.RunJobs();
    static readonly Assembly Asm = typeof(R34).Assembly;
    static readonly Uri CardUri = new("avares://S6.HotReload/CardView.axaml");
    static readonly RuntimeXamlLoaderConfiguration Cfg = new() { LocalAssembly = Asm, UseCompiledBindingsByDefault = true };

    static void Reload(StyledElement target, string xaml, Uri uri)
    {
        target.Styles.Clear();
        if (target is Control c) c.Resources.Clear();
        AvaloniaRuntimeXamlLoader.Load(new RuntimeXamlLoaderDocument(uri, target, xaml), Cfg);
    }

    const string Head = """
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:local="clr-namespace:S6.HotReload" x:Class="S6.HotReload.CardView" x:DataType="local:CardViewModel">
        """;
    static string Card(string styles, string resources, string body) =>
        Head + $"<UserControl.Styles>{styles}</UserControl.Styles><UserControl.Resources>{resources}</UserControl.Resources>"
             + $"<StackPanel x:Name=\"Root\">{body}<TextBlock x:Name=\"Title\" Classes=\"title\" Text=\"{{Binding Title}}\" /><Button x:Name=\"SaveButton\" Content=\"Save\" Click=\"OnSave\" /></StackPanel></UserControl>";

    public static void Run()
    {
        var field = typeof(CardView).GetField("!XamlIlPopulateOverride", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;

        // ---------- R3: future instances ----------
        var lw = new ListWindow();
        var items = new ObservableCollection<CardViewModel> { new() { Title = "A" } };
        lw.FindControl<ItemsControl>("List")!.ItemsSource = items;
        lw.Show(); Pump(); lw.CaptureRenderedFrame(); Pump();

        var v2 = Card("", "", "<TextBlock x:Name=\"Subtitle\" Text=\"v2-marker\" />");
        Check("R3a", "!XamlIlPopulateOverride field type", () => field.FieldType.ToString());
        int overrideCalls = 0;
        field.SetValue(null, (Action<object>)(o => { overrideCalls++; AvaloniaRuntimeXamlLoader.Load(new RuntimeXamlLoaderDocument(CardUri, o, v2), Cfg); }));
        Check("R3b", "`new CardView()` after override set uses new XAML", () =>
        {
            var c = new CardView { DataContext = new CardViewModel { Title = "new" } };
            return $"override calls={overrideCalls}; Subtitle={c.FindControl<TextBlock>("Subtitle")?.Text ?? "<none>"}";
        });
        Check("R3c", "DataTemplate-created instance (ItemsControl item added later)", () =>
        {
            items.Add(new CardViewModel { Title = "B" });
            Pump(); lw.CaptureRenderedFrame(); Pump();
            var cards = lw.GetVisualDescendants().OfType<CardView>().ToList();
            return $"{cards.Count} cards; with Subtitle: {cards.Count(c => c.FindControl<TextBlock>("Subtitle") != null)} (old item A not re-populated by override: expected)";
        });
        Check("R3d", "Runtime loader null-root path while our override is set (conflict?)", () =>
        {
            var o = (CardView)AvaloniaRuntimeXamlLoader.Load(new RuntimeXamlLoaderDocument(CardUri, Card("", "", "<TextBlock x:Name=\"Subtitle\" Text=\"dry\" />")), Cfg);
            var after = field.GetValue(null) is Action<object>;
            return $"dry-run Subtitle={o.FindControl<TextBlock>("Subtitle")?.Text}; our override still installed={after}; overrideCalls={overrideCalls}";
        });
        Check("R3e", "Override + construction error surfaces where?", () =>
        {
            field.SetValue(null, (Action<object>)(o => throw new InvalidOperationException("boom")));
            try { _ = new CardView(); return "no exception"; }
            catch (Exception ex) { return $"ctor throws {ex.GetType().Name}: {ex.Message}"; }
            finally { field.SetValue(null, null); }
        });
        Check("R3f", "Clearing override restores compiled XAML", () =>
        {
            var c = new CardView();
            return $"Subtitle present={c.FindControl<TextBlock>("Subtitle") != null}";
        });

        // ---------- R4: is Clear() enough? ----------
        var host = new Window { Width = 400, Height = 300 };
        var card = new CardView { DataContext = new CardViewModel() };
        host.Content = card; host.Show(); Pump(); host.CaptureRenderedFrame(); Pump();
        TextBlock Title() => card.FindControl<TextBlock>("Title")!;
        Button Save() => card.FindControl<Button>("SaveButton")!;
        void Apply(string x) { Reload(card, x, CardUri); Pump(); host.CaptureRenderedFrame(); Pump(); }

        Check("R4a", "Style added then removed -> value reverts", () =>
        {
            Apply(Card("<Style Selector=\"TextBlock.title\"><Setter Property=\"FontSize\" Value=\"40\"/></Style>", "", ""));
            var with = Title().FontSize;
            Apply(Card("", "", ""));
            return $"with style={with}, after removing style={Title().FontSize} (default 14 expected)";
        });
        Check("R4b", "Keyed ControlTheme in Resources, edit Background", () =>
        {
            string Theme(string color) => $"<ControlTheme x:Key=\"MyBtn\" TargetType=\"Button\" BasedOn=\"{{StaticResource {{x:Type Button}}}}\"><Setter Property=\"Background\" Value=\"{color}\"/></ControlTheme>";
            var bodyBtn = "<Button x:Name=\"Themed\" Theme=\"{StaticResource MyBtn}\" Content=\"T\"/>";
            Apply(Card("", Theme("Red"), bodyBtn));
            var b1 = (card.FindControl<Button>("Themed")!.Background as ISolidColorBrush)?.Color;
            Apply(Card("", Theme("Lime"), bodyBtn));
            var b2 = (card.FindControl<Button>("Themed")!.Background as ISolidColorBrush)?.Color;
            return $"Red->{b1}, Lime->{b2}";
        });
        Check("R4c", "Implicit ControlTheme ({x:Type Button}) in Resources, edit", () =>
        {
            string Theme(string color) => $"<ControlTheme x:Key=\"{{x:Type Button}}\" TargetType=\"Button\" BasedOn=\"{{StaticResource {{x:Type Button}}}}\"><Setter Property=\"Foreground\" Value=\"{color}\"/></ControlTheme>";
            Apply(Card("", Theme("Red"), ""));
            var f1 = (Save().Foreground as ISolidColorBrush)?.Color;
            Apply(Card("", Theme("Blue"), ""));
            var f2 = (Save().Foreground as ISolidColorBrush)?.Color;
            Apply(Card("", "", ""));
            var f3 = (Save().Foreground as ISolidColorBrush)?.Color;
            return $"Red->{f1}, Blue->{f2}, removed->{f3}";
        });
        Check("R4d", "Theme/style on the ROOT itself (UserControl.Background via own Styles), then removed", () =>
        {
            Apply(Card("<Style Selector=\"local|CardView\"><Setter Property=\"Background\" Value=\"Orange\"/></Style>", "", ""));
            var b1 = (card.Background as ISolidColorBrush)?.Color;
            Apply(Card("", "", ""));
            return $"with={b1}, removed={card.Background?.ToString() ?? "null"}";
        });
        Check("R4e", "Root attribute removed (Padding=20 then absent) -> local value cleared?", () =>
        {
            Apply(Card("", "", "").Replace("x:DataType=\"local:CardViewModel\">", "x:DataType=\"local:CardViewModel\" Padding=\"20\">"));
            var p1 = card.Padding;
            Apply(Card("", "", ""));
            return $"with={p1}, removed={card.Padding} (stale if still 20)";
        });
        Check("R4f", "ContentControl with inline content reloaded 3x (\"already has a visual parent\"?)", () =>
        {
            var body = "<ContentControl x:Name=\"CC\"><Border Width=\"10\" Height=\"10\"/></ContentControl>";
            for (int i = 0; i < 3; i++) Apply(Card("", "", body));
            return $"ok, CC child={card.FindControl<ContentControl>("CC")!.Content?.GetType().Name}";
        });
        Check("R4g", "Root event handler attribute duplicated on reload? (Loaded=\"...\" not used; check Click count)", () =>
        {
            Apply(Card("", "", ""));
            var before = card.Saves;
            Save().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            return $"one click -> Saves +{card.Saves - before}";
        });
        Check("R4h", "Root-level attached handler (Button.Click on UserControl) after 3 reloads: handler count", () =>
        {
            var x = Card("", "", "").Replace("x:DataType=\"local:CardViewModel\">", "x:DataType=\"local:CardViewModel\" Button.Click=\"OnRootClick\">");
            for (int i = 0; i < 3; i++) Apply(x);
            card.RootFocusCount = 0;
            Save().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            return $"one bubbling Click -> handler ran {card.RootFocusCount}x (1 = ok, 3 = duplicated subscriptions)";
        });


        Check("R4e2", "FIX: RootReset.Undo(oldXaml) before reload clears stale root local value", () =>
        {
            var withPad = Card("", "", "").Replace("x:DataType=\"local:CardViewModel\">", "x:DataType=\"local:CardViewModel\" Padding=\"20\">");
            Apply(withPad);
            var r = RootReset.Undo(card, withPad);
            Apply(Card("", "", ""));
            return $"{r}; Padding now={card.Padding}";
        });
        Check("R4h2", "FIX: RootReset.Undo removes root-level handlers -> 1x after 3 reloads", () =>
        {
            var x = Card("", "", "").Replace("x:DataType=\"local:CardViewModel\">", "x:DataType=\"local:CardViewModel\" Button.Click=\"OnRootClick\">");
            // remove the 3 stale subscriptions from R4h first (each Undo removes one)
            for (int i = 0; i < 3; i++) RootReset.Undo(card, x);
            string r = "";
            for (int i = 0; i < 3; i++) { Apply(x); if (i < 2) r = RootReset.Undo(card, x); }
            card.RootFocusCount = 0;
            Save().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            return $"{r}; one Click -> handler ran {card.RootFocusCount}x";
        });
        Check("R3g", "FIX: override falls back to compiled XAML when runtime populate throws", () =>
        {
            var tramp = typeof(CardView).GetMethod("!XamlIlPopulateTrampoline", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
            Action<object>? ours = null;
            ours = o =>
            {
                try { throw new InvalidOperationException("runtime populate failed"); }
                catch
                {
                    field.SetValue(null, null);                 // let the trampoline use the compiled populate
                    try { tramp.Invoke(null, new[] { o }); }
                    finally { field.SetValue(null, ours); }      // re-install
                }
            };
            field.SetValue(null, ours);
            try
            {
                var c = new CardView();
                return $"ctor ok; compiled content present={c.FindControl<Button>("SaveButton") != null}; override re-installed={ReferenceEquals(field.GetValue(null), ours)}";
            }
            finally { field.SetValue(null, null); }
        });
        foreach (var r in Results)
            Console.WriteLine($"{(r.Ok ? "PASS" : "FAIL"),-4} {r.Id,-5} {r.Name}\n           {r.Detail}");
    }
}
