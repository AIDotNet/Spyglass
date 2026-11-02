using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Diagnostics;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace S8;

public static class Program
{
    static void Pump() => Dispatcher.UIThread.RunJobs();
    static string Src(object o) => o is AvaloniaObject ao && XamlSourceInfo.GetXamlSourceInfo(ao) is { } si ? $"{si.SourceUri}:{si.LineNumber}" : "-";
    static string Color(IBrush? b) => (b as ISolidColorBrush)?.Color.ToString() ?? "null";

    public static void Main()
    {
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var app = Application.Current!;
        Console.WriteLine("== Application.Styles at run time");
        foreach (var s in app.Styles) Console.WriteLine($"  {s.GetType().Name} src={Src(s)} {(s is StyleInclude si ? "Source=" + si.Source : "")}");
        Console.WriteLine("== Application.Resources.MergedDictionaries");
        foreach (var d in app.Resources.MergedDictionaries) Console.WriteLine($"  {d.GetType().Name} src={Src(d)} {(d is ResourceInclude ri ? "Source=" + ri.Source : "")} keys=[{string.Join(",", (d as ResourceDictionary)?.Keys ?? [])}]");
        Console.WriteLine($"  own keys=[{string.Join(",", app.Resources.Keys)}]");

        var big = new TextBlock { Classes = { "big" }, Text = "x" };
        var dyn = new Border();
        dyn.Bind(Border.BackgroundProperty, dyn.GetResourceObservable("AccentBrush"));   // = DynamicResource
        var stat = new Border { Background = (IBrush)app.FindResource("AccentBrush")! };   // = StaticResource
        var merged = new Border();
        merged.Bind(Border.BackgroundProperty, merged.GetResourceObservable("MergedBrush"));
        var w = new Window { Content = new StackPanel { Children = { big, dyn, stat, merged } } };
        w.Show(); Pump();
        Console.WriteLine($"== before: big.FontSize={big.FontSize} dyn={Color(dyn.Background)} stat={Color(stat.Background)} merged={Color(merged.Background)}");

        // --- 1. Styles document: replace the object that came from Theme.axaml in its parent collection.
        var themeV2 = File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../Theme.axaml")).Replace("Value=\"30\"", "Value=\"44\"");
        var styles = app.Styles;
        var idx = -1;
        for (var i = 0; i < styles.Count; i++)
            if (styles[i] is StyleInclude inc && inc.Source?.ToString().EndsWith("Theme.axaml") == true || Src(styles[i]).Contains("Theme.axaml")) idx = i;
        Console.WriteLine($"== Theme.axaml found at Styles[{idx}]");
        if (idx >= 0)
        {
            var loaded = (IStyle)AvaloniaRuntimeXamlLoader.Load(new RuntimeXamlLoaderDocument(new Uri("avares://S8.ThemeReload/Theme.axaml"), themeV2) { Document = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../Theme.axaml")) },
                new RuntimeXamlLoaderConfiguration { LocalAssembly = typeof(App).Assembly, CreateSourceInfo = true });
            styles[idx] = loaded; Pump();
            Console.WriteLine($"   after replace: big.FontSize={big.FontSize}  new object src={Src(loaded)} type={loaded.GetType().Name}");
        }

        // --- 2. ResourceDictionary via ResourceInclude: replace in MergedDictionaries.
        var colorsV2 = File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../Colors.axaml")).Replace("Blue", "Orange");
        var md = app.Resources.MergedDictionaries;
        var ridx = -1;
        for (var i = 0; i < md.Count; i++)
            if (md[i] is ResourceInclude ri && ri.Source?.ToString().EndsWith("Colors.axaml") == true || Src(md[i]).Contains("Colors.axaml") || (md[i] as ResourceDictionary)?.ContainsKey("AccentBrush") == true) ridx = i;
        Console.WriteLine($"== Colors.axaml found at MergedDictionaries[{ridx}]");
        if (ridx >= 0)
        {
            var loaded = (IResourceProvider)AvaloniaRuntimeXamlLoader.Load(colorsV2, typeof(App).Assembly, null, new Uri("avares://S8.ThemeReload/Colors.axaml"));
            md[ridx] = loaded; Pump();
            Console.WriteLine($"   after replace: dyn={Color(dyn.Background)} (DynamicResource) stat={Color(stat.Background)} (StaticResource, expected stale)");
        }

        // --- 3. MergeResourceInclude: entries copied into the parent? 
        Console.WriteLine($"== MergedBrush lives in: {(app.Resources.ContainsKey("MergedBrush") ? "Application.Resources itself (merged copy)" : string.Join(",", md.Select((d, i) => (d as ResourceDictionary)?.ContainsKey("MergedBrush") == true ? $"MergedDictionaries[{i}] src={Src(d)}" : null).Where(x => x != null)))}");

        // --- 4. App.axaml: re-populate Application.Current.
        var appV2 = File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../App.axaml")).Replace("Color=\"Red\"", "Color=\"Purple\"");
        if (Environment.GetEnvironmentVariable("S8_REL") == "1") appV2 = appV2.Replace("Source=\"avares://S8.ThemeReload/Merged.axaml\"", "Source=\"/Merged.axaml\"");
        var appBrush = new Border(); appBrush.Bind(Border.BackgroundProperty, appBrush.GetResourceObservable("AppBrush"));
        ((StackPanel)w.Content).Children.Add(appBrush); Pump();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            app.Styles.Clear();
            app.Resources.Clear(); app.Resources.MergedDictionaries.Clear(); app.Resources.ThemeDictionaries.Clear();
            string Read(string f) => File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../" + f));
            var themeLive = Read("Theme.axaml").Replace("Value=\"30\"", "Value=\"55\"");   // pretend Theme.axaml was reloaded before
            AvaloniaRuntimeXamlLoader.LoadGroup(new[]
            {
                new RuntimeXamlLoaderDocument(new Uri("avares://S8.ThemeReload/App.axaml"), app, appV2),
                new RuntimeXamlLoaderDocument(new Uri("avares://S8.ThemeReload/Merged.axaml"), Read("Merged.axaml").Replace("Green", "Yellow")) { Document = Environment.GetEnvironmentVariable("S8_DOC") == "1" ? "/abs/Merged.axaml" : null },
                new RuntimeXamlLoaderDocument(new Uri("avares://S8.ThemeReload/Colors.axaml"), Read("Colors.axaml")),
                new RuntimeXamlLoaderDocument(new Uri("avares://S8.ThemeReload/Theme.axaml"), themeLive),
            }, new RuntimeXamlLoaderConfiguration { LocalAssembly = typeof(App).Assembly, UseCompiledBindingsByDefault = true, CreateSourceInfo = true });
            Pump(); w.CaptureRenderedFrame(); Pump();
            Console.WriteLine($"== App.axaml re-populated in {sw.ElapsedMilliseconds} ms: AppBrush={Color(appBrush.Background)} big.FontSize={big.FontSize} (55 = group text used for the include) dyn={Color(dyn.Background)} Styles.Count={app.Styles.Count} merged={Color(merged.Background)} (Yellow = MergeResourceInclude from group)");
            foreach (var st in app.Styles) Console.WriteLine($"   Styles: {st.GetType().Name} src={Src(st)}");
            var btn = new Button { Content = "b" }; ((StackPanel)w.Content).Children.Add(btn); Pump(); w.CaptureRenderedFrame();
            Console.WriteLine($"   Fluent still applied to a new Button: Template={(btn.Template != null)}");
        }
        catch (Exception ex) { Console.WriteLine($"== App.axaml re-populate FAILED: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }

        // --- 5. StaticResource -> DynamicResource rewrite, as text.
        Console.WriteLine("== done");
    }
}
