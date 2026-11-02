using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace S6.HotReload;

/// R8: real desktop window (not headless). Reload timing incl. next rendered frame, and instances inside a Popup (own PopupRoot).
static class R8Desktop
{
    static readonly Uri CardUri = new("avares://S6.HotReload/CardView.axaml");
    static readonly RuntimeXamlLoaderConfiguration Cfg = new() { LocalAssembly = typeof(R8Desktop).Assembly, UseCompiledBindingsByDefault = true };

    public static void Run(string[] args) =>
        AppBuilder.Configure<App>().UsePlatformDetect().AfterSetup(_ => Dispatcher.UIThread.Post(Start)).StartWithClassicDesktopLifetime(args);

    static string Xaml(int i) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../CardView.axaml"))
        .Replace("Content=\"Save\"", $"Content=\"Save #{i}\"");

    static async void Start()
    {
        var popup = new Popup { Child = new Border { Padding = new Thickness(8), Child = new CardView { DataContext = new CardViewModel { Title = "in popup" } } }, Placement = PlacementMode.Bottom };
        var panel = new StackPanel();
        for (int i = 0; i < 20; i++) panel.Children.Add(new CardView { DataContext = new CardViewModel { Title = $"card {i}" } });
        panel.Children.Add(popup);
        var w = new Window { Width = 500, Height = 700, Content = new ScrollViewer { Content = panel } };
        w.Show();
        await Task.Delay(800);
        popup.IsOpen = true;
        await Task.Delay(500);

        // Enumerate instances across top levels: window + popup root (PopupRoot is a separate TopLevel on desktop).
        List<CardView> Find()
        {
            var roots = new List<Visual> { w };
            foreach (var p in w.GetVisualDescendants().OfType<Popup>().Where(p => p.IsOpen && p.Child != null))
                if (TopLevel.GetTopLevel(p.Child!) is { } tl && tl != w) roots.Add(tl);
            return roots.SelectMany(r => r.GetVisualDescendants().OfType<CardView>()).Distinct().ToList();
        }
        var popupRootType = TopLevel.GetTopLevel(popup.Child!)?.GetType().Name;
        var inst = Find();
        var times = new List<double>(); var frames = new List<double>();
        for (int i = 1; i <= 15; i++)
        {
            var sw = Stopwatch.StartNew();
            var x = Xaml(i);
            foreach (var c in inst) { c.Styles.Clear(); c.Resources.Clear(); AvaloniaRuntimeXamlLoader.Load(new RuntimeXamlLoaderDocument(CardUri, c, x), Cfg); }
            times.Add(sw.Elapsed.TotalMilliseconds);
            var tcs = new TaskCompletionSource();
            w.RequestAnimationFrame(_ => tcs.TrySetResult());
            await tcs.Task;
            frames.Add(sw.Elapsed.TotalMilliseconds);
            await Task.Delay(100);
        }
        var popupCard = TopLevel.GetTopLevel(popup.Child!)!.GetVisualDescendants().OfType<CardView>().First();
        Console.WriteLine($"R8 popup TopLevel={popupRootType}; instances found={inst.Count} (20 in window + 1 in popup expected)");
        Console.WriteLine($"R8 popup card Save content='{popupCard.FindControl<Button>("SaveButton")?.Content}'");
        Console.WriteLine($"R8 reload {inst.Count} instances: first={times[0]:F1} ms, warm median={times.Skip(1).OrderBy(t => t).ElementAt(7):F1} ms, max={times.Skip(1).Max():F1} ms");
        Console.WriteLine($"R8 reload -> next frame: first={frames[0]:F1} ms, warm median={frames.Skip(1).OrderBy(t => t).ElementAt(7):F1} ms");
        Console.WriteLine($"R8 per-instance warm ≈ {times.Skip(1).OrderBy(t => t).ElementAt(7) / inst.Count:F2} ms");
        await Task.Delay(300);
        ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Shutdown();
    }
}
