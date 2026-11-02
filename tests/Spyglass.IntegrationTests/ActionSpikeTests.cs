using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>Spike S3 (design/mcp/README.md §8): which AutomationPeer patterns work in the headless app, per control.</summary>
[Collection("app")]
public class ActionSpikeTests(AppFixture app)
{
    static T? P<T>(Control c) where T : class => ControlAutomationPeer.CreatePeerForElement(c)?.GetProvider<T>();

    [Fact]
    public async Task Peer_patterns_per_control()
    {
        var w = await app.OnUi(() => { var w = new ActWindow(); w.Show(); return w; });
        try
        {
            var report = await app.OnUi(() =>
            {
                var r = new List<string>();
                void Add(string s) => r.Add(s);
                // Button: Invoke
                P<IInvokeProvider>(w.Click)!.Invoke();
                Add($"Button IInvokeProvider.Invoke -> clicks={w.ActClicks}");
                // TextBox: IValueProvider
                P<IValueProvider>(w.Input)!.SetValue("hello");
                Add($"TextBox IValueProvider.SetValue -> Text='{w.Input.Text}'");
                // CheckBox / ToggleButton: IToggleProvider
                P<IToggleProvider>(w.Check)!.Toggle();
                Add($"CheckBox IToggleProvider.Toggle -> IsChecked={w.Check.IsChecked}");
                P<IToggleProvider>(w.Toggle)!.Toggle();
                Add($"ToggleButton IToggleProvider.Toggle -> IsChecked={w.Toggle.IsChecked}");
                // ComboBox: expand + selection
                var ec = P<IExpandCollapseProvider>(w.Combo);
                Add($"ComboBox IExpandCollapseProvider={(ec is null ? "missing" : "present")}");
                ec?.Expand();
                Add($"ComboBox Expand -> IsDropDownOpen={w.Combo.IsDropDownOpen}");
                var item = w.Combo.ContainerFromIndex(2) as Control;
                Add($"ComboBox container(2) realized={(item is not null)}");
                if (item is not null) { P<ISelectionItemProvider>(item)?.Select(); Add($"ComboBox item ISelectionItemProvider.Select -> SelectedIndex={w.Combo.SelectedIndex}"); }
                ec?.Collapse();
                // ListBox
                var li = w.List.ContainerFromIndex(1) as Control;
                Add($"ListBox container(1) realized={(li is not null)}");
                if (li is not null) { P<ISelectionItemProvider>(li)?.Select(); Add($"ListBoxItem ISelectionItemProvider.Select -> SelectedIndex={w.List.SelectedIndex}"); }
                Add($"ListBox ISelectionProvider={(P<ISelectionProvider>(w.List) is null ? "missing" : "present")}");
                // Expander
                var ex = P<IExpandCollapseProvider>(w.Expand);
                ex?.Expand();
                Add($"Expander IExpandCollapseProvider={(ex is null ? "missing" : "present")} -> IsExpanded={w.Expand.IsExpanded}");
                Add($"ScrollViewer IScrollProvider={(P<IScrollProvider>(new ScrollViewer()) is null ? "missing" : "present")}");
                return r;
            });
            Assert.DoesNotContain(report, l => l.Contains("missing") && !l.StartsWith("ScrollViewer"));
            Assert.Contains("SelectedIndex=2", string.Join("\n", report));
            Assert.Contains("IsExpanded=True", string.Join("\n", report));
            Assert.Contains("clicks=1", report[0]);
            Assert.Contains("Text='hello'", report[1]);
            Assert.Contains("IsChecked=True", report[2]);
            Assert.Contains("IsChecked=True", report[3]);
        }
        finally { await app.OnUi(() => { w.Close(); return 0; }); }
    }

    [Fact]
    public async Task Synthesized_pointer_click_reaches_a_button()
    {
        var w = await app.OnUi(() => { var w = new ActWindow(); w.Show(); return w; });
        try
        {
            await app.OnUi(() => { Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); return 0; });
            await Task.Delay(100);   // the render thread commits asynchronously
            // Button re-hit-tests on release: needs a rendered frame
            var log = await app.OnUi(() =>
            {
                var tl = w;
                var p = w.Click.TranslatePoint(new Avalonia.Point(5, 5), tl)!.Value;
                var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, true);
                var down = new Avalonia.Input.PointerPressedEventArgs(w.Click, pointer, tl, p, 1,
                    new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed), Avalonia.Input.KeyModifiers.None, 1);
                w.Click.RaiseEvent(down);
                var up = new Avalonia.Input.PointerReleasedEventArgs(w.Click, pointer, tl, p, 2,
                    new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PointerUpdateKind.LeftButtonReleased), Avalonia.Input.KeyModifiers.None, Avalonia.Input.MouseButton.Left);
                w.Click.RaiseEvent(up);
                return $"down.handled={down.Handled} up.handled={up.Handled} pos={p} clicks={w.ActClicks} bounds={w.Click.Bounds} captured={pointer.Captured} pressed={w.Click.IsPressed}";
            });
            Assert.Contains("clicks=1", log);   // a bare RaiseEvent press+release pair clicks a Button
        }
        finally { await app.OnUi(() => { w.Close(); return 0; }); }
    }
}
