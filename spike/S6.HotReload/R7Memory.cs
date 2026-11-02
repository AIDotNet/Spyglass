using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace S6.HotReload;

/// R7: memory after many reloads (runtime compiler emits a RunAndCollect dynamic assembly per Load).
static class R7Memory
{
    public static void Run()
    {
        var uri = new Uri("avares://S6.HotReload/CardView.axaml");
        var cfg = new RuntimeXamlLoaderConfiguration { LocalAssembly = typeof(R7Memory).Assembly, UseCompiledBindingsByDefault = true };
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../CardView.axaml"));
        var w = new Window { Content = new CardView { DataContext = new CardViewModel() } }; w.Show();
        var card = (CardView)w.Content!;
        long Mem() { for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } return GC.GetTotalMemory(true); }
        int Dyn() => AppDomain.CurrentDomain.GetAssemblies().Count(a => a.IsDynamic);
        void Reload(int i) { card.Styles.Clear(); card.Resources.Clear(); AvaloniaRuntimeXamlLoader.Load(new RuntimeXamlLoaderDocument(uri, card, xaml.Replace("Content=\"Save\"", $"Content=\"S{i}\"")), cfg); Dispatcher.UIThread.RunJobs(); }
        for (int i = 0; i < 20; i++) Reload(i);   // warm up
        long m0 = Mem(); int d0 = Dyn(); var sw = Stopwatch.StartNew();
        var oldTrees = new List<WeakReference>();
        for (int i = 0; i < 500; i++) { oldTrees.Add(new WeakReference(card.Content)); Reload(i); }
        var ms = sw.ElapsedMilliseconds; long m1 = Mem(); int d1 = Dyn();
        Mem();
        if (Environment.GetEnvironmentVariable("R7WAIT") == "1") { Console.WriteLine($"R7 PID {Environment.ProcessId} waiting"); Thread.Sleep(60000); }
        Console.WriteLine($"R7 old content trees still alive after GC: {oldTrees.Count(r => r.IsAlive)} / 500");
        Console.WriteLine($"R7 500 reloads: {ms} ms total ({ms / 500.0:F1} ms each); managed heap {m0 / 1024} KB -> {m1 / 1024} KB (+{(m1 - m0) / 1024} KB, {(m1 - m0) / 500 / 1024.0:F1} KB/reload); live dynamic assemblies {d0} -> {d1}; process WS {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024} MB");
    }
}
