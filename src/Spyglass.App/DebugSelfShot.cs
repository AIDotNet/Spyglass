#if DEBUG
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Spyglass.App.ViewModels;

namespace Spyglass.App;

/// <summary>
/// Development aid: with SPYGLASS_SELFSHOT=/path/prefix set, waits for an app to connect, optionally selects
/// SPYGLASS_SELFSHOT_SELECT (a search query), renders Spyglass's own window to PNGs and exits.
/// Renders only this window's content (RenderTargetBitmap), never the screen.
/// </summary>
internal static class DebugSelfShot
{
    public static void Schedule(Window window, MainViewModel vm)
    {
        var prefix = Environment.GetEnvironmentVariable("SPYGLASS_SELFSHOT");
        if (string.IsNullOrEmpty(prefix)) return;
        var select = Environment.GetEnvironmentVariable("SPYGLASS_SELFSHOT_SELECT");
        if (Environment.GetEnvironmentVariable("SPYGLASS_SELFSHOT_THEME") == "light")
            TKCode.Arc.ArcTheme.Apply(t => Theme.SpyglassTheme.Glass(t.Appearance(TKCode.Arc.ArcAppearance.Light)));
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 100 && vm.Current is null; i++) await Task.Delay(100);
            await Task.Delay(1500);
            await Dispatcher.UIThread.InvokeAsync(() => Save(window, prefix + "-1-connected.png"));
            if (Environment.GetEnvironmentVariable("SPYGLASS_SELFSHOT_DUMPLINES") is not null)
            {
                await Task.Delay(500);
                await Dispatcher.UIThread.InvokeAsync(() => DumpThinElements(window));
            }
            if (vm.Current is { } s && !string.IsNullOrEmpty(select))
            {
                await Dispatcher.UIThread.InvokeAsync(() => s.Elements.SearchText = select);
                await Task.Delay(1500);
                await Dispatcher.UIThread.InvokeAsync(() => Save(window, prefix + "-2-selected.png"));
                await Dispatcher.UIThread.InvokeAsync(() => s.Elements.DetailsTab = 1);
                await Task.Delay(1000);
                await Dispatcher.UIThread.InvokeAsync(() => Save(window, prefix + "-3-layout.png"));
                await Dispatcher.UIThread.InvokeAsync(() => s.Elements.DetailsTab = 2);
                await Task.Delay(1200);
                await Dispatcher.UIThread.InvokeAsync(() => Save(window, prefix + "-4-styles.png"));
                await Dispatcher.UIThread.InvokeAsync(() => vm.Page = "bindings");
                await Task.Delay(1200);
                await Dispatcher.UIThread.InvokeAsync(() => Save(window, prefix + "-5-bindings.png"));
                await ShootResourcesAsync(window, vm, s, prefix);
                await Dispatcher.UIThread.InvokeAsync(() => vm.Page = "network");   // starts recording
                await Task.Delay(7000);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var net = s.Network;
                    net.Selected = net.Rows.FirstOrDefault(r => r.Request.Status == 401) ?? net.Rows.FirstOrDefault();
                });
                await Task.Delay(1500);
                await Dispatcher.UIThread.InvokeAsync(() => Save(window, prefix + "-6-network.png"));
                // v0.3: overview + range filter, sort by status (descending), optional columns, search highlight.
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var net = s.Network;
                    net.Selected = null;
                    net.ShowOverview = true;
                    net.ToggleColumn(Spyglass.App.Core.Network.NetworkColumn.Domain);
                    net.AddCustomHeader("Content-Type");
                    net.ToggleSort(net.Columns.First(c => c.Column == Spyglass.App.Core.Network.NetworkColumn.Status));
                    net.ToggleSort(net.Columns.First(c => c.Column == Spyglass.App.Core.Network.NetworkColumn.Status));
                    net.SetRange(net.Overview.TotalMs * 0.2, net.Overview.TotalMs * 0.7);
                    net.SearchOpen = true;
                    net.SearchText = "a";
                    _ = net.SearchAsync();
                });
                await Task.Delay(1500);
                await Dispatcher.UIThread.InvokeAsync(() => s.Network.Selected = null);
                await Task.Delay(600);
                await Dispatcher.UIThread.InvokeAsync(() => Save(window, prefix + "-6-network-v03.png"));
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var net = s.Network;
                    net.SearchOpen = false;
                    net.SetRange(null, null);
                    net.ToggleColumn(Spyglass.App.Core.Network.NetworkColumn.Domain);
                    net.RemoveCustomHeader("Content-Type");
                    net.ShowOverview = false;
                    net.SetSort(Spyglass.App.Core.Network.NetworkSortSpec.Chronological);
                });
                await Dispatcher.UIThread.InvokeAsync(() => vm.Page = "logs");
                await Task.Delay(1200);
                await Dispatcher.UIThread.InvokeAsync(() => Save(window, prefix + "-6-logs.png"));
                await ShootEventsAsync(window, vm, s, select, prefix + "-6-events.png");
                await ShootPerformanceAsync(window, vm, s, prefix + "-7-performance.png");
                await Dispatcher.UIThread.InvokeAsync(() => vm.Page = "settings");
                await Task.Delay(1000);
                await Dispatcher.UIThread.InvokeAsync(() => Save(window, prefix + "-8-settings.png"));
                if (Environment.GetEnvironmentVariable("SPYGLASS_SELFSHOT_SETTINGS") is { Length: > 0 } changes)
                {
                    // Same path as the settings page: SettingsService.Update → SettingsApplier → ArcTheme.Apply. e.g. "Accent=Rose,Appearance=Light"
                    await Dispatcher.UIThread.InvokeAsync(() => Core.Settings.SettingsService.Shared.Update(st =>
                    {
                        foreach (var kv in changes.Split(',').Select(x => x.Split('=')))
                        {
                            if (kv[0] == "Accent") st.Accent = kv[1];
                            if (kv[0] == "Appearance") st.Appearance = Enum.Parse<Core.Settings.AppearanceMode>(kv[1]);
                            if (kv[0] == "Motion") st.Motion = Enum.Parse<Core.Settings.MotionMode>(kv[1]);
                        }
                    }));
                    await Task.Delay(1500);
                    await Dispatcher.UIThread.InvokeAsync(() => Save(window, prefix + "-8-settings-changed.png"));
                    await Dispatcher.UIThread.InvokeAsync(() => vm.Page = "elements");
                    await Task.Delay(1000);
                    await Dispatcher.UIThread.InvokeAsync(() => Save(window, prefix + "-8-elements-changed.png"));
                }
                await Dispatcher.UIThread.InvokeAsync(() => vm.Page = "connections");
                await Task.Delay(800);
                await Dispatcher.UIThread.InvokeAsync(() => Save(window, prefix + "-3-connections.png"));
            }
            await Dispatcher.UIThread.InvokeAsync(() => (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown());
        });
    }

    /// <summary>Resources page: the biggest dark theme dictionary (Fluent's), a colour selected for editing, then a lookup.</summary>
    static async Task ShootResourcesAsync(Window window, MainViewModel vm, SessionViewModel s, string prefix)
    {
        await Dispatcher.UIThread.InvokeAsync(() => vm.Page = "resources");
        await Task.Delay(1800);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var r = s.Resources;
            r.SelectedProvider = r.Roots.SelectMany(x => x.Descendants())
                .Where(x => x.Label.Contains("ThemeDictionaries[Dark]")).OrderByDescending(x => x.Dto.KeyCount).FirstOrDefault() ?? r.SelectedProvider;
            r.SearchText = "Accent";
        });
        await Task.Delay(1500);
        await Dispatcher.UIThread.InvokeAsync(() => s.Resources.SelectedEntry = s.Resources.Entries.FirstOrDefault(e => e.HasSwatch) ?? s.Resources.Entries.FirstOrDefault());
        await Task.Delay(800);
        await Dispatcher.UIThread.InvokeAsync(() => Save(window, prefix + "-7-resources.png"));
        await Dispatcher.UIThread.InvokeAsync(() => { s.Resources.LookupKey = "ButtonBackground"; s.Resources.TabKey = "lookup"; s.Resources.ResolveCommand.Execute(null); });
        await Task.Delay(1500);
        await Dispatcher.UIThread.InvokeAsync(() => Save(window, prefix + "-7-resources-lookup.png"));
    }

    /// <summary>Performance page: lets metrics run for a few seconds, with the FPS overlay switched on in the app.</summary>
    static async Task ShootPerformanceAsync(Window window, MainViewModel vm, SessionViewModel s, string path)
    {
        await Dispatcher.UIThread.InvokeAsync(() => vm.Page = "performance");
        await Task.Delay(1500);
        await Dispatcher.UIThread.InvokeAsync(() => { foreach (var o in s.Performance.Overlays.Take(1)) o.IsOn = true; });
        // SPYGLASS_SELFSHOT_QUIET=1: wait until the 10 s rolling window has emptied, to see how a quiet app is shown.
        await Task.Delay(Environment.GetEnvironmentVariable("SPYGLASS_SELFSHOT_QUIET") == "1" ? 16000 : 6000);
        await Dispatcher.UIThread.InvokeAsync(() => Save(window, path));
        await Dispatcher.UIThread.InvokeAsync(() => vm.Page = "connections");   // leaving the page stops profiling
    }

    /// <summary>
    /// Events page with real records: also traces RequestBringIntoView and asks the agent to bring the selected
    /// element into view (raises that routed event in the app without synthesising input).
    /// </summary>
    static async Task ShootEventsAsync(Window window, MainViewModel vm, SessionViewModel s, string select, string path)
    {
        await Dispatcher.UIThread.InvokeAsync(() => vm.Page = "events");
        await Task.Delay(1200);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var t in s.Events.Types.Where(t => t.Name == "RequestBringIntoView")) t.IsChecked = true;
        });
        await Task.Delay(600);
        try
        {
            var matches = (await s.Session.SearchAsync(new Protocol.Messages.SearchParams(select))).Matches;
            for (var i = 0; i < 3 && matches.Count > 0; i++) await s.Session.BringIntoViewAsync(matches[0].Id);
        }
        catch (Exception ex) { Console.WriteLine($"[selfshot] events: {ex.Message}"); }
        await Task.Delay(800);
        await Dispatcher.UIThread.InvokeAsync(() => s.Events.Selected = s.Events.Records.FirstOrDefault());
        await Task.Delay(800);
        await Dispatcher.UIThread.InvokeAsync(() => Save(window, path));
    }

    /// <summary>Prints tall elements that draw a border on one side only (stray divider lines), with their ancestry.</summary>
    static void DumpThinElements(Window w)
    {
        if (Environment.GetEnvironmentVariable("SPYGLASS_SELFSHOT_TREE") is { Length: > 0 } typeName)
            foreach (var root in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w).Where(v => v.GetType().Name == typeName).Take(1))
                foreach (var v in new[] { root }.Concat(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root)).OfType<Avalonia.Controls.Control>())
                {
                    var depth = Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(v).TakeWhile(a => !ReferenceEquals(a, root)).Count();
                    var bg = v is Avalonia.Controls.Border b ? $" bg={b.Background} corner={b.CornerRadius}" : v is Avalonia.Controls.Primitives.TemplatedControl tc ? $" bg={tc.Background}" : "";
                    Console.WriteLine($"[tree] {new string(' ', depth * 2)}{v.GetType().Name}{(string.IsNullOrEmpty(v.Name) ? "" : "#" + v.Name)} {v.Bounds.Width:0.#}x{v.Bounds.Height:0.#}{bg}");
                }
        var filter = Environment.GetEnvironmentVariable("SPYGLASS_SELFSHOT_TOKENS");
        if (!string.IsNullOrEmpty(filter) && TKCode.Arc.ArcThemeManager.Current is { } mgr)
            foreach (var d in mgr.Descriptors.Where(d => d.Identity.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                for (var i = 0; i < d.TokenNames.Count; i++)
                    Console.WriteLine($"[token] {d.Identity}.{d.TokenNames[i]} = {(mgr.Resources.TryGetResource(d.Keys[i], null, out var v) ? v : "?")}");
        foreach (var rp in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w).OfType<TKCode.Arc.ResizablePanels>().Take(1))
            foreach (var v in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(rp).OfType<Avalonia.Controls.Control>())
            {
                if (v.Bounds.Width > 14 || v.Bounds.Height < 100) continue;   // narrow, tall parts: handles, dividers
                var pos = v.TranslatePoint(default, w);
                Console.WriteLine($"[rp] {v.GetType().Name}{(string.IsNullOrEmpty(v.Name) ? "" : "#" + v.Name)} {v.Bounds.Width:0.#}x{v.Bounds.Height:0.#} at {pos?.X:0},{pos?.Y:0}  classes={string.Join(".", v.Classes)}");
            }
        foreach (var v in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w).OfType<Avalonia.Controls.Control>())
        {
            if (!v.IsVisible || v.Bounds.Height < 150) continue;
            var t = v switch
            {
                Avalonia.Controls.Border b => b.BorderThickness,
                Avalonia.Controls.Primitives.TemplatedControl tc => tc.BorderThickness,
                _ => default,
            };
            var oneSide = (t.Left > 0 || t.Right > 0) && t.Top == 0 && t.Bottom == 0 && (t.Left == 0 || t.Right == 0);
            if (!oneSide && v.Bounds.Width > 3) continue;
            var chain = new List<string>();
            for (Avalonia.Visual? p = v; p is not null && chain.Count < 7; p = Avalonia.VisualTree.VisualExtensions.GetVisualParent(p))
                chain.Add(p.GetType().Name + (p is Avalonia.Controls.Control { Name: { Length: > 0 } n } ? "#" + n : ""));
            var pos = v.TranslatePoint(default, w);
            Console.WriteLine($"[thin] {v.Bounds.Width:0.#}x{v.Bounds.Height:0.#} at {pos?.X:0},{pos?.Y:0} border={t}  {string.Join(" < ", chain)}");
        }
    }

    static void Save(Window w, string path)
    {
        var size = new PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height);
        using var rtb = new RenderTargetBitmap(size);
        rtb.Render(w);
        rtb.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine($"[selfshot] {path}");
    }
}
#endif
