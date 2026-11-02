using System.Diagnostics;
using System.Diagnostics.Metrics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Diagnostics;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Logging;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Rendering;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace S1.AgentApis;

public static class Program
{
    static readonly List<(string Id, string Name, bool Ok, string Detail)> Results = new();

    static void Check(string id, string name, Func<string> body)
    {
        try { Results.Add((id, name, true, body())); }
        catch (Exception ex) { Results.Add((id, name, false, $"{ex.GetType().Name}: {ex.Message}")); }
    }

    public static void Main()
    {
        // Opt in to Avalonia's built-in metrics / activities before anything starts.
        AppContext.SetSwitch("Avalonia.Diagnostics.Diagnostic.IsEnabled", true);

        var sink = new CapturingSink();
        Logger.Sink = sink;

        var meterHits = new Dictionary<string, int>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name.StartsWith("Avalonia")) l.EnableMeasurementEvents(inst);
        };
        meterListener.SetMeasurementEventCallback<double>((i, _, _, _) => Bump(meterHits, i.Name));
        meterListener.SetMeasurementEventCallback<long>((i, _, _, _) => Bump(meterHits, i.Name));
        meterListener.SetMeasurementEventCallback<int>((i, _, _, _) => Bump(meterHits, i.Name));
        meterListener.Start();

        var activityHits = new Dictionary<string, int>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name.StartsWith("Avalonia"),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => Bump(activityHits, a.OperationName),
        };
        ActivitySource.AddActivityListener(activityListener);

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var window = new TestWindow { DataContext = new TestViewModel() };
        window.Show();
        Pump();
        window.CaptureRenderedFrame(); // force a full layout + render pass
        Pump();

        var save = window.FindControl<Button>("SaveButton")!;
        var broken = window.FindControl<TextBlock>("Broken")!;
        var combo = window.FindControl<ComboBox>("Combo")!;

        // ---------- Trees ----------
        Check("T1", "Visual tree walk (GetVisualChildren)", () =>
            $"{CountVisual(window)} visuals under Window");
        Check("T2", "Logical tree walk (GetLogicalChildren)", () =>
            $"{CountLogical(window)} logical nodes under Window");
        Check("T3", "Template parts detectable (TemplatedParent)", () =>
        {
            var parts = save.GetVisualDescendants().OfType<StyledElement>().Count(v => v.TemplatedParent == save);
            return $"{parts} template parts of SaveButton";
        });
        Check("T4", "TopLevel.GetTopLevel(visual)", () =>
            TopLevel.GetTopLevel(save) == window ? "returns the Window" : "returned something else");

        // ---------- Picking ----------
        var center = save.TranslatePoint(new Point(save.Bounds.Width / 2, save.Bounds.Height / 2), window)!.Value;
        Check("P1", "GetVisualsAt(point) hit test", () =>
        {
            var hits = window.GetVisualsAt(center).ToList();
            return $"{hits.Count} visuals; top={Describe(hits.FirstOrDefault())}; contains SaveButton={hits.Contains(save) || hits.Any(h => h.FindAncestorOfType<Button>() == save)}";
        });
        Check("P2", "InputHitTest(point)", () =>
        {
            var hit = window.InputHitTest(center) as Visual;
            return $"hit={Describe(hit)}; owning Button={(hit as Button ?? hit?.FindAncestorOfType<Button>())?.Name}";
        });
        Check("P3", "Picking under the pointer via headless MouseMove + IsPointerOver", () =>
        {
            window.MouseMove(center);
            Pump();
            return $"SaveButton.IsPointerOver={save.IsPointerOver}, pseudo :pointerover={((IPseudoClasses)save.Classes).GetType().Name}/{save.Classes.Contains(":pointerover")}";
        });

        // ---------- Popups ----------
        combo.IsDropDownOpen = true;
        Pump();
        Check("U1", "Find popup root from owner (public: Popup.Child → TopLevel)", () =>
        {
            var popup = combo.GetVisualDescendants().OfType<Popup>().FirstOrDefault()
                        ?? throw new InvalidOperationException("no Popup in ComboBox template");
            var root = popup.Child == null ? null : TopLevel.GetTopLevel(popup.Child);
            return $"popup.IsOpen={popup.IsOpen}; root={root?.GetType().Name}; root==window={root == window}; items under root={(root == null ? 0 : CountVisual(root))}";
        });
        Check("U1b", "Enumerate open popups (Popup.OpenedPopups)", () =>
        {
            var popup = combo.GetVisualDescendants().OfType<Popup>().First();
            return $"OpenedPopups.Count={popup.OpenedPopups.Count}";
        });
        Check("U2", "Popup content reachable from window tree?", () =>
        {
            var inWindow = window.GetVisualDescendants().OfType<ComboBoxItem>().Count();
            return $"{inWindow} ComboBoxItem found by walking the Window visual tree (0 => popups must be enumerated separately)";
        });
        Check("U3", "Overlay layer (popups-in-overlay / adorners)", () =>
        {
            var overlay = OverlayLayer.GetOverlayLayer(window);
            var adorner = AdornerLayer.GetAdornerLayer(save);
            return $"OverlayLayer={(overlay != null)}; AdornerLayer={(adorner != null)}";
        });
        combo.IsDropDownOpen = false;
        Pump();

        // ---------- Highlight ----------
        Check("H1", "Add highlight adorner via AdornerLayer", () =>
        {
            var layer = AdornerLayer.GetAdornerLayer(save) ?? throw new InvalidOperationException("no AdornerLayer");
            var box = new Border { BorderBrush = Avalonia.Media.Brushes.DodgerBlue, BorderThickness = new Thickness(1), IsHitTestVisible = false };
            AdornerLayer.SetAdornedElement(box, save);
            layer.Children.Add(box);
            Pump();
            window.CaptureRenderedFrame();
            var ok = box.Bounds.Size == save.Bounds.Size;
            layer.Children.Remove(box);
            return $"adorner bounds={box.Bounds.Size} vs element={save.Bounds.Size} match={ok}";
        });

        // ---------- Properties ----------
        Check("R1", "AvaloniaPropertyRegistry.GetRegistered(type)", () =>
        {
            var props = AvaloniaPropertyRegistry.Instance.GetRegistered(save.GetType());
            var attached = AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(save.GetType());
            return $"{props.Count} registered + {attached.Count} attached for Button";
        });
        Check("R2", "GetDiagnostic(prop) → value + priority", () =>
        {
            var bg = save.GetDiagnostic(TemplatedControl.BackgroundProperty);
            var w = save.GetDiagnostic(Layoutable.WidthProperty);
            var fs = save.GetDiagnostic(TemplatedControl.FontSizeProperty);
            return $"Background={bg.Value} [{bg.Priority}] diag='{bg.Diagnostic}'; Width={w.Value} [{w.Priority}]; FontSize={fs.Value} [{fs.Priority}]";
        });
        Check("R3", "SetValue at runtime (property editing)", () =>
        {
            save.SetValue(Layoutable.WidthProperty, 200d);
            Pump();
            window.CaptureRenderedFrame();
            var r = $"Width now {save.Bounds.Width}";
            save.ClearValue(Layoutable.WidthProperty);
            save.SetValue(Layoutable.WidthProperty, 120d);
            return r;
        });
        Check("R4", "Classes / PseudoClasses toggle", () =>
        {
            ((IPseudoClasses)save.Classes).Set(":pointerover", true);
            Pump();
            var bg = save.GetDiagnostic(TemplatedControl.BackgroundProperty);
            ((IPseudoClasses)save.Classes).Set(":pointerover", false);
            return $"after forcing :pointerover Background={bg.Value}";
        });

        // ---------- Bindings ----------
        Check("B1", "Logger.Sink captures binding errors", () =>
        {
            var b = sink.Entries.Where(e => e.Area == LogArea.Binding).ToList();
            return $"{b.Count} binding log entries; first: {b.FirstOrDefault()}";
        });
        Check("B2", "Binding error carries target element (source object)", () =>
        {
            var targets = sink.Entries.Where(e => e.Area == LogArea.Binding).Select(e => Describe(e.Source as Visual ?? (e.Source as StyledElement) as Visual)).Distinct();
            return "targets: " + string.Join(", ", targets);
        });
        Check("B3", "BindingOperations.GetBindingExpressionBase(obj, prop)", () =>
        {
            var expr = Avalonia.Data.BindingOperations.GetBindingExpressionBase(broken, TextBlock.TextProperty);
            return $"expr={expr?.GetType().Name ?? "null"}";
        });

        // ---------- Source info ----------
        Check("X1", "XamlSourceInfo.GetXamlSourceInfo(control)", () =>
        {
            var si = XamlSourceInfo.GetXamlSourceInfo(save);
            return si?.ToString() ?? "null (not emitted in this build configuration)";
        });

        Check("X2", "XamlSourceInfo of a Style (for 'go to style definition')", () =>
        {
            var style = Avalonia.Application.Current!.Styles.OfType<Avalonia.Styling.Style>().First();
            return $"Style({style.Selector}) → {XamlSourceInfo.GetXamlSourceInfo(style)?.ToString() ?? "null"}";
        });

        // ---------- Routed events ----------
        Check("E1", "RoutedEventRegistry.GetAllRegistered()", () =>
            $"{RoutedEventRegistry.Instance.GetAllRegistered().Count()} routed events registered");
        Check("E2", "Global tracing via RoutedEvent.Raised", () =>
        {
            var seen = new List<string>();
            using var sub = Button.ClickEvent.Raised.Subscribe(new ActionObserver<(object, RoutedEventArgs)>(t =>
                seen.Add($"{(t.Item1 as Control)?.Name}:{t.Item2.RoutedEvent?.Name}")));
            using var sub2 = InputElement.PointerPressedEvent.Raised.Subscribe(new ActionObserver<(object, RoutedEventArgs)>(t =>
                seen.Add($"{(t.Item1 as Control)?.Name ?? t.Item1.GetType().Name}:{t.Item2.RoutedEvent?.Name}")));
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            Pump();
            return string.Join(" | ", seen.Take(8));
        });

        // ---------- Screenshot ----------
        Check("S1", "RenderTargetBitmap of a single element", () =>
        {
            var size = new PixelSize((int)save.Bounds.Width, (int)save.Bounds.Height);
            using var rtb = new RenderTargetBitmap(size);
            rtb.Render(save);
            var path = Path.Combine(AppContext.BaseDirectory, "save-button.png");
            rtb.Save(path);
            return $"{size} → {new FileInfo(path).Length} bytes";
        });

        // ---------- Perf ----------
        Check("M1", "RendererDiagnostics.DebugOverlays toggle", () =>
        {
            window.RendererDiagnostics.DebugOverlays = RendererDebugOverlays.Fps | RendererDebugOverlays.LayoutTimeGraph;
            return $"overlays={window.RendererDiagnostics.DebugOverlays}";
        });
        Check("M2", "Avalonia meters observed (MeterListener)", () =>
        {
            meterListener.RecordObservableInstruments();
            return meterHits.Count == 0 ? "no measurements" : string.Join(", ", meterHits.Select(kv => $"{kv.Key}×{kv.Value}"));
        });
        Check("M3", "Avalonia activities observed (ActivityListener)", () =>
            activityHits.Count == 0 ? "no activities" : string.Join(", ", activityHits.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key}×{kv.Value}")));

        Check("T5", "Snapshot + JSON-serialize a large visual tree (warm-up)", () => TreeSnapshot.Measure(500));
        Check("T6", "Snapshot + JSON-serialize a large visual tree", () => TreeSnapshot.Measure(3000));

        PrivateChecks.Run(Check, save, broken);

        Print();
    }

    static void Bump(Dictionary<string, int> d, string k) { lock (d) d[k] = d.TryGetValue(k, out var v) ? v + 1 : 1; }
    static void Pump() { Dispatcher.UIThread.RunJobs(); }
    static int CountVisual(Visual v) => 1 + v.GetVisualChildren().Sum(CountVisual);
    static int CountLogical(ILogical l) => 1 + l.GetLogicalChildren().Sum(CountLogical);
    public static string Describe(Visual? v) => v == null ? "null" : $"{v.GetType().Name}{(v is Control { Name: { } n } ? "#" + n : "")}";

    static void Print()
    {
        Console.WriteLine($"Avalonia {typeof(AvaloniaObject).Assembly.GetName().Version}  |  .NET {Environment.Version}  |  {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Console.WriteLine();
        foreach (var r in Results)
            Console.WriteLine($"[{(r.Ok ? "PASS" : "FAIL")}] {r.Id,-3} {r.Name}\n        {r.Detail}");
    }
}

sealed class ActionObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void OnNext(T value) => onNext(value);
}

sealed class CapturingSink : ILogSink
{
    public readonly List<(string Area, LogEventLevel Level, object? Source, string Message)> Entries = new();
    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning || area == LogArea.Binding;
    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        Entries.Add((area, level, source, messageTemplate));
    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
        Entries.Add((area, level, source, Format(messageTemplate, propertyValues)));
    static string Format(string t, object?[] args)
    {
        var i = 0;
        return System.Text.RegularExpressions.Regex.Replace(t, @"\{[^}]+\}", m => i < args.Length ? $"{args[i++]}" : m.Value);
    }
}
