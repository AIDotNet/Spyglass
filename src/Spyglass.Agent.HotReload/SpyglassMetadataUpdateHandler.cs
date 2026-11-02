using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Spyglass.Agent.Internal;
using Spyglass.Protocol.Messages;

[assembly: MetadataUpdateHandler(typeof(Spyglass.Agent.HotReload.SpyglassMetadataUpdateHandler))]

namespace Spyglass.Agent.HotReload;

/// <summary>
/// Runs after every .NET Hot Reload delta (dotnet watch, Visual Studio, C# Dev Kit; design/hot-reload/README.md §5, §6.1).
/// 1. XAML stamps in the delta (the generator's SpyglassXamlStamp_* types, recreated on update) carry the saved .axaml
///    text: apply it like any other XAML (works without the Spyglass hub, and on devices the hub cannot see).
/// 2. C# changes: re-evaluate bindings of elements whose DataContext type changed, invalidate layout everywhere.
/// The runtime never refreshes UI on its own.
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Debug-only hot reload.")]
[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Debug-only hot reload.")]
[UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Debug-only hot reload.")]
internal static class SpyglassMetadataUpdateHandler
{
    const string StampPrefix = "SpyglassXamlStamp_";

    /// <summary>Raised on the UI thread after the C# part of a delta was handled.</summary>
    public static event Action<HotReloadCodeUpdate>? CodeUpdated;

    // Signatures required by the runtime (resolved by name).
    static void ClearCache(Type[]? types) { }

    static void UpdateApplication(Type[]? types) =>
        Dispatcher.UIThread.Post(() => _ = HandleAsync(types));

    public static async Task HandleAsync(Type[]? types)
    {
        var sw = Stopwatch.StartNew();
        var updated = types ?? [];
        var reloadedViews = new HashSet<string>();

        // 1. XAML first, so a save that changed both .axaml and .axaml.cs ends with the new XAML on the new code.
        foreach (var stamp in updated.Where(IsStamp))
        {
            if (Read(stamp) is not var (uri, cls, text)) continue;
            try
            {
                var r = await XamlReloader.Shared.ApplyAsync(uri, text, HotReloadSource.Stamp, 0);
                if (r.Ok && cls is not null) reloadedViews.Add(cls);
            }
            catch (Exception ex) { Debug.WriteLine($"[Spyglass] hot reload stamp {uri}: {ex.Message}"); }
        }

        // 2. C#.
        var code = updated.Where(t => !IsStamp(t)).ToList();
        if (types is not null && code.Count == 0) return;   // a XAML-only save
        var warnings = new List<string>();
        foreach (var t in code)
            if (NeedsRestartForNewInstances(t) && !reloadedViews.Contains(t.FullName ?? ""))
                warnings.Add($"{t.Name}: new instances may fail until the app restarts (its InitializeComponent was recompiled; set SpyglassHotReloadInitializeComponent=true to avoid this).");

        var refreshed = RefreshBindings(types is null ? null : code.ToHashSet());
        foreach (var (tl, _, _, _) in new TreeWalker(new NodeRegistry()).Roots())
        {
            tl.InvalidateMeasure();
            tl.InvalidateVisual();
        }
        CodeUpdated?.Invoke(new HotReloadCodeUpdate(
            types is null ? ["(all)"] : code.Select(t => t.FullName ?? t.Name).ToList(),
            refreshed, Math.Round(sw.Elapsed.TotalMilliseconds, 1), warnings));
    }

    static bool IsStamp(Type t) => t.Namespace == "Spyglass.Generated" && t.Name.StartsWith(StampPrefix, StringComparison.Ordinal);

    static (string Uri, string? Class, string Text)? Read(Type stamp)
    {
        try
        {
            var uri = stamp.GetField("Uri")?.GetValue(null) as string;
            var cls = stamp.GetField("Class")?.GetValue(null) as string;
            var text = stamp.GetMethod("Text", Type.EmptyTypes)?.Invoke(null, null) as string;
            return uri is null || text is null ? null : (uri, cls, text);
        }
        catch { return null; }
    }

    /// <summary>
    /// A compiled x:Class view whose InitializeComponent came from Avalonia's generator: if the delta recompiled it, it now
    /// calls the throwing AvaloniaXamlLoader.Load stub (spike S7, R1). Spyglass's own InitializeComponent (§5.1) is safe.
    /// </summary>
    static bool NeedsRestartForNewInstances(Type t)
    {
        if (t.GetField("!XamlIlPopulateOverride", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public) is null) return false;
        var init = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "InitializeComponent");
        var tool = init?.GetCustomAttribute<System.CodeDom.Compiler.GeneratedCodeAttribute>()?.Tool;
        return tool?.StartsWith("Avalonia.Generators", StringComparison.Ordinal) == true;
    }

    /// <summary>
    /// Re-evaluates bindings whose source type changed by setting the DataContext back on the topmost element that sets it
    /// (null = any type changed). View state is captured and restored around it.
    /// </summary>
    static int RefreshBindings(HashSet<Type>? changed)
    {
        var count = 0;
        foreach (var (tl, _, _, _) in new TreeWalker(new NodeRegistry()).Roots())
        {
            var stack = new Stack<ILogical>();
            stack.Push(tl);
            while (stack.Count > 0)
            {
                var l = stack.Pop();
                if (l is StyledElement e && e.IsSet(StyledElement.DataContextProperty) && e.DataContext is { } dc && Changed(dc.GetType(), changed))
                {
                    var state = StateKeeper.Capture(e);
                    e.DataContext = null;
                    e.DataContext = dc;
                    StateKeeper.Restore(e, state);
                    count++;
                    continue;   // descendants inherit it, or set their own below a refreshed parent: refreshed too
                }
                foreach (var c in l.LogicalChildren) stack.Push(c);
            }
        }
        return count;
    }

    static bool Changed(Type t, HashSet<Type>? changed)
    {
        if (changed is null) return true;
        for (var c = t; c is not null && c != typeof(object); c = c.BaseType)
            if (changed.Contains(c)) return true;
        return false;
    }
}
