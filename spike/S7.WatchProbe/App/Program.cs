using System.Reflection;
using System.Reflection.Metadata;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

[assembly: MetadataUpdateHandler(typeof(S7.App.HotReloadHandler))]

namespace S7.App;

static class Log { public static void W(string s) => Console.WriteLine($"[S7 {DateTime.Now:HH:mm:ss.fff}] {s}"); }

static class HotReloadHandler
{
    static void ClearCache(Type[]? types) => Log.W($"ClearCache: {Names(types)}");
    static void UpdateApplication(Type[]? types)
    {
        Log.W($"UpdateApplication: {Names(types)}");
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var t in types ?? Type.EmptyTypes)
            {
                if (!t.Name.StartsWith("SpyglassXamlStamp_")) continue;
                var orig = t.GetCustomAttribute<System.Runtime.CompilerServices.MetadataUpdateOriginalTypeAttribute>()?.OriginalType;
                var text = (string)t.GetMethod("Text")!.Invoke(null, null)!;
                var cls = (string)t.GetField("Class")!.GetValue(null)!;
                Log.W($"  stamp {t.FullName} (original={orig?.Name ?? "-"}) class={cls} len={text.Length}");
                var type = Type.GetType(cls);
                if (type == null) continue;
                var ov = type.GetField("!XamlIlPopulateOverride", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                ov?.SetValue(null, (Action<object>)(o => AvaloniaRuntimeXamlLoader.Load(new RuntimeXamlLoaderDocument(new Uri("avares://S7.App/CardView.axaml"), o, text),
                    new RuntimeXamlLoaderConfiguration { LocalAssembly = type.Assembly, UseCompiledBindingsByDefault = true })));
                Log.W($"  Override installed for {type.Name}: {ov != null}");
                foreach (var inst in Program.Window!.GetVisualDescendants().Where(v => v.GetType() == type).Cast<StyledElement>().ToList())
                {
                    inst.Styles.Clear();
                    if (inst is Control c) c.Resources.Clear();
                    AvaloniaRuntimeXamlLoader.Load(new RuntimeXamlLoaderDocument(new Uri("avares://S7.App/CardView.axaml"), inst, text),
                        new RuntimeXamlLoaderConfiguration { LocalAssembly = type.Assembly, UseCompiledBindingsByDefault = true });
                    Log.W($"  reloaded live {type.Name}");
                }
            }
        });
    }
    static string Names(Type[]? t) => t == null ? "<null>" : "[" + string.Join(", ", t.Select(x => x.FullName)) + "]";
}

public static class Program
{
    public static Window? Window;
    public static void Main()
    {
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        Window = new Window { Content = new CardView() };
        Window.Show();
        Log.W($"started; MetadataUpdater.IsSupported={MetadataUpdater.IsSupported}; stamps={typeof(Program).Assembly.GetTypes().Count(t => t.Name.StartsWith("SpyglassXamlStamp_"))}");
        string last = "";
        for (;;)
        {
            Dispatcher.UIThread.RunJobs();
            var live = (Window.Content as CardView)?.FindControl<TextBlock>("Title")?.Text;
            string fresh;
            try { var c = new CardView(); fresh = (c.FindControl<TextBlock>("Title")?.Text ?? "<no Title>") + " Sub=" + (c.FindControl<TextBlock>("Sub")?.Text ?? "-") + " SubField=" + (c.GetType().GetProperty("Sub", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(c) is TextBlock ? "set" : "-"); }
            catch (Exception ex) { fresh = $"CTOR THROWS {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"; }
            var state = $"code={Probe.Version()} live.Title={live} new CardView().Title={fresh}";
            if (state != last) { Log.W(state); last = state; }
            Thread.Sleep(200);
        }
    }
}
