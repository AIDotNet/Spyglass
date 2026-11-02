using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Fonts;
using TKCode.Arc.Motion;
using TKCode.Arc.Themes.Corners;
using ArcTreeView = TKCode.Arc.TreeView;

namespace S2.ArcCompact;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--shot")) { Shots.Run(); return; }
        Build(headless: false).StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder Build(bool headless)
    {
        var b = AppBuilder.Configure<App>();
        b = headless
            ? b.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            : b.UsePlatformDetect();
        return b.WithInterFont().WithArcFonts()
            .UseArc(arc => arc.UseArcControls().UseArcBlocks().UseArcCorners()
                .WithTheme(t => Density.Compact(t.Appearance(ArcAppearance.Light).Accent(ArcAccentPreset.Blue))));
    }
}

public static class Density
{
    public static ArcThemeBuilder Default(ArcThemeBuilder t) => t.ClearTokens().Corners(ArcCornerStyle.Default).MotionLevel(ArcMotionLevel.Full);

    // Proposed Spyglass "compact" density (design/ux/03 §4.2).
    public static ArcThemeBuilder Compact(ArcThemeBuilder t) => t
        .Corners(ArcCornerStyle.Scale(0.35))
        .MotionLevel(ArcMotionLevel.Reduced)
        .Token(nameof(ArcDesignTokenKind.ControlSizeSM), "26")
        .Token(nameof(ArcDesignTokenKind.ControlSizeMD), "30")
        .Token(nameof(ArcDesignTokenKind.ControlSizeLG), "34")
        .Token(nameof(ArcDesignTokenKind.TypeXS), "11")
        .Token(nameof(ArcDesignTokenKind.TypeSM), "12")
        .Token(nameof(ArcDesignTokenKind.TypeBase), "13")
        .Token(nameof(ArcDesignTokenKind.GlyphSizeSM), "14")
        .Token(nameof(ArcDesignTokenKind.GlyphSizeMD), "16");
}

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d)
        {
            var vm = MainViewModel.Create(10_000);
            d.MainWindow = new MainWindow(vm);
        }
        base.OnFrameworkInitializationCompleted();
    }
}

public partial class MainWindow : Window
{
    public MainWindow() : this(MainViewModel.Create(100)) { }
    public MainWindow(MainViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        var tree = this.FindControl<ArcTreeView>("Tree")!;
        tree.ChildrenSelector = o => ((Node)o).Children;
        tree.RowHeight = 24;
    }
}

static class Shots
{
    static string OutDir = "";
    static readonly List<string> Report = new();

    public static void Run()
    {
        OutDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../shots"));
        Directory.CreateDirectory(OutDir);
        Program.Build(headless: true).SetupWithoutStarting();

        var sw = Stopwatch.StartNew();
        var vm = MainViewModel.Create(10_000);
        Log($"model: {vm.TotalNodes:N0} nodes, max depth {vm.MaxDepth} built in {sw.ElapsedMilliseconds} ms");

        var w = new MainWindow(vm);
        var tree = w.FindControl<ArcTreeView>("Tree")!;

        sw.Restart();
        w.Show(); Pump(); w.CaptureRenderedFrame(); Pump();
        Log($"first show + render: {sw.ElapsedMilliseconds} ms");

        // Default density for comparison.
        ArcTheme.Apply(t => Density.Default(t.Appearance(ArcAppearance.Light).Accent(ArcAccentPreset.Blue)));
        tree.RowHeight = 38;
        Expand(tree, vm, levels: 6);
        Shot(w, "01-default-light");

        // Compact light & dark.
        ArcTheme.Apply(t => Density.Compact(t.Appearance(ArcAppearance.Light).Accent(ArcAccentPreset.Blue)));
        tree.RowHeight = 24;
        Shot(w, "02-compact-light");
        ArcTheme.Apply(t => Density.Compact(t.Appearance(ArcAppearance.Dark).Accent(ArcAccentPreset.Blue)));
        Shot(w, "03-compact-dark");

        // Rail expanded.
        var nav = w.FindControl<Sidebar>("Nav")!;
        nav.IsCollapsed = false; Pump();
        Shot(w, "04-compact-dark-sidebar-open");
        nav.IsCollapsed = true; Pump();

        // Perf: expand everything (10k rows), then jump to the last node and select it.
        sw.Restart();
        tree.ExpandAll(); Pump(); w.CaptureRenderedFrame();
        Log($"ExpandAll ({vm.TotalNodes:N0} rows) + render: {sw.ElapsedMilliseconds} ms");
        Log($"realized row visuals after ExpandAll: {CountRows(tree)} (virtualization check)");

        var last = vm.Last;
        sw.Restart();
        tree.ScrollIntoView(last); tree.SelectedItem = last; Pump(); w.CaptureRenderedFrame();
        Log($"ScrollIntoView(last) + select + render: {sw.ElapsedMilliseconds} ms");
        Shot(w, "05-compact-dark-expanded-last");

        // Simulated scroll: 50 jumps across the tree.
        var flat = vm.Flatten().ToList();
        sw.Restart();
        for (var i = 0; i < 50; i++) { tree.ScrollIntoView(flat[i * flat.Count / 50]); Pump(); w.CaptureRenderedFrame(); }
        Log($"50 scroll jumps + render: {sw.ElapsedMilliseconds} ms total ({sw.ElapsedMilliseconds / 50.0:F1} ms/jump)");

        // Live update: add 200 children to a visible node (simulates tree/childAdded deltas).
        var target = vm.Roots[0];
        sw.Restart();
        for (var i = 0; i < 200; i++) target.Children.Add(new Node("TextBlock", $"Added{i}", null, target.Depth + 1));
        Pump(); w.CaptureRenderedFrame();
        Log($"200 incremental child inserts + render: {sw.ElapsedMilliseconds} ms (ObservableCollection; does TreeView react? see shot 06)");
        tree.ScrollIntoView(target.Children[^1]); Pump();
        Shot(w, "06-after-incremental-insert");

        File.WriteAllLines(Path.Combine(OutDir, "timings.txt"), Report);
        Console.WriteLine($"Shots + timings written to {OutDir}");
    }

    static void Expand(ArcTreeView tree, MainViewModel vm, int levels)
    {
        foreach (var n in vm.Flatten().Where(n => n.Depth < levels)) tree.Expand(n);
        Pump();
    }

    static int CountRows(Visual root) => root.GetVisualDescendants().OfType<TextBlock>().Count(t => t.DataContext is Node);

    static void Shot(Window w, string name)
    {
        Pump();
        // Let reduced motion transitions settle.
        for (var i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(15); w.CaptureRenderedFrame(); }
        var frame = w.CaptureRenderedFrame()!;
        frame.Save(Path.Combine(OutDir, name + ".png"));
        Log($"shot {name}.png");
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();
    static void Log(string s) { Report.Add(s); Console.WriteLine(s); }
}
