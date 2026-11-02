using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;
using ToggleState = Avalonia.Automation.Provider.ToggleState;

namespace Spyglass.Agent.Internal;

/// <summary>
/// input/act, wait/until and changes/reset (design/protocol/01-methods.md §13; spike S3 in design/mcp/README.md §8).
/// Every action prefers the control's AutomationPeer pattern (same path as assistive technology), then a direct
/// property change, then synthesized input. The element's state, the binding errors and the warnings raised while the
/// action ran come back in the result so the caller notices breakage immediately. Handlers run on the UI thread.
/// </summary>
internal sealed class ActionHandlers(JsonRpcPeer peer, NodeRegistry registry, TreeWalker walker, ValueCodec codec, PropertyInspector props)
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public void Register()
    {
        peer.Handle<InputActParams, InputActResult>(Methods.InputAct, (p, _) => ActAsync(p));
        peer.Handle<WaitUntilParams, WaitUntilResult>(Methods.WaitUntil, (p, ct) => WaitAsync(p, ct));
        peer.Handle<ChangesResetParams, ChangesResetResult>(Methods.ChangesReset, (p, _) => Ui(() =>
        {
            var pending = props.PendingChanges(p.Id);
            props.Reset(p.Id is null ? new PropsResetParams(All: true) : new PropsResetParams(Id: p.Id));
            return new ChangesResetResult(pending);
        }));
    }

    static Task<T> Ui<T>(Func<T> f) => Dispatcher.UIThread.InvokeAsync(f).GetTask();

    // =================================================================== input/act

    async Task<InputActResult> ActAsync(InputActParams p)
    {
        var bindingCursor = await Ui(() => BindingErrorCollector.Instance?.LatestSeq ?? 0);
        var logCursor = await Ui(() => LogCollector.Instance?.LatestSeq ?? 0);
        var (via, note) = await Ui(() => Perform(p));
        // Let effects that were posted by the action (binding updates, layout, commands) run before reading the state.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        return await Ui(() =>
        {
            var target = registry.Get<object>(p.Id);
            var newBindings = BindingErrorCollector.Instance?.Snapshot(registry, null, bindingCursor) ?? [];
            IReadOnlyList<LogEntryDto> newLogs = LogCollector.Instance is { } lc
                ? lc.Snapshot(registry, logCursor, Avalonia.Logging.LogEventLevel.Warning, null, 5).Entries.Where(e => e.Area != "Binding").ToList()
                : [];
            return new InputActResult(via, walker.Describe(target) with { ChildCount = 0 }, StateOf(target), newBindings.Count, newLogs.Count)
            {
                NewBindingErrors = newBindings.Count == 0 ? null : newBindings.Take(5).ToList(),
                NewLogs = newLogs.Count == 0 ? null : newLogs,
                Note = note,
            };
        });
    }

    (string Via, string? Note) Perform(InputActParams p)
    {
        var o = registry.Get<object>(p.Id);
        if (o is not Control c)
            throw Fail($"{o.GetType().Name} is not a UI element; actions apply to controls.");
        RequireUsable(c, p.Action);
        return p.Action switch
        {
            InputAction.Invoke => Invoke(c),
            InputAction.SetText => SetText(c, p.Text ?? throw Fail("setText needs `text`.")),
            InputAction.TypeText => TypeText(c, p.Text ?? throw Fail("typeText needs `text`."), p.Replace),
            InputAction.Toggle => Toggle(c, p.State),
            InputAction.Select => Select(c, p.Text, p.Index),
            InputAction.Expand => ExpandCollapse(c, expand: true),
            InputAction.Collapse => ExpandCollapse(c, expand: false),
            InputAction.Focus => Focus(c),
            InputAction.Key => PressKey(c, p.Key ?? throw Fail("key needs `key`, e.g. \"Enter\" or \"Ctrl+S\".")),
            InputAction.ScrollIntoView => ScrollIntoView(c, p.Text, p.Index),
            InputAction.Pointer => Pointer(c, p),
            _ => throw Fail($"Unknown action {p.Action}."),
        };
    }

    static SpyglassProtocolException Fail(string message) => new(ErrorCodes.ActionFailed, message);

    /// <summary>Refuses disabled / invisible targets with the culprit named, since a silent no-op would mislead.</summary>
    static void RequireUsable(Control c, InputAction action)
    {
        if (!c.IsEffectivelyVisible)
        {
            var culprit = c.GetSelfAndVisualAncestors().OfType<Visual>().FirstOrDefault(v => !v.IsVisible);
            throw Fail($"{Name(c)} is not visible" + (culprit is null ? "." : ReferenceEquals(culprit, c) ? " (IsVisible=False)." : $" ({Name(culprit)} has IsVisible=False)."));
        }
        if (action != InputAction.ScrollIntoView && !c.IsEffectivelyEnabled)
        {
            var culprit = c.GetSelfAndVisualAncestors().OfType<InputElement>().FirstOrDefault(v => !v.IsEnabled);
            var command = culprit is null && c is ICommandSource { Command: not null };
            var why = command ? " (its Command's CanExecute returns false)"
                : culprit is null ? ""
                : ReferenceEquals(culprit, c) ? " (IsEnabled=False)"
                : $" ({Name(culprit)} has IsEnabled=False)";
            throw Fail($"{Name(c)} is disabled{why}. Real users cannot operate it either; if you only want to test the enabled look, use property_set IsEnabled=True.");
        }
    }

    static string Name(object o) => o.GetType().Name + (o is StyledElement { Name: { Length: > 0 } n } ? "#" + n : "");

    static T? Provider<T>(Control c) where T : class
    {
        try { return ControlAutomationPeer.CreatePeerForElement(c)?.GetProvider<T>(); }
        catch { return null; }
    }

    // ---------------------------------------------------------------- invoke / toggle / expand

    (string, string?) Invoke(Control c)
    {
        if (Provider<IInvokeProvider>(c) is { } inv)
        {
            inv.Invoke();
            return ("peer:IInvokeProvider", null);
        }
        if (c is ToggleButton || Provider<IToggleProvider>(c) is not null) return (Toggle(c, null).Item1, "invoke on a toggle flips it");
        if (SelectionContainerOf(c) is not null) return Select(c, null, null);
        if (Provider<IExpandCollapseProvider>(c) is { } ec)
        {
            if (ec.ExpandCollapseState == ExpandCollapseState.Expanded) ec.Collapse(); else ec.Expand();
            return ("peer:IExpandCollapseProvider", "invoke on an expandable control flips it");
        }
        // Last resort: a real click through the input pipeline at the element's centre.
        return Pointer(c, new InputActParams(0, InputAction.Pointer));
    }

    (string, string?) Toggle(Control c, bool? state)
    {
        var provider = Provider<IToggleProvider>(c);
        if (provider is not null)
        {
            for (var i = 0; i < 3; i++)
            {
                if (state is { } want && (provider.ToggleState == ToggleState.On) == want && provider.ToggleState != ToggleState.Indeterminate) break;
                if (state is null && i == 1) break;
                provider.Toggle();
            }
            return ("peer:IToggleProvider", null);
        }
        if (c is ToggleButton tb)
        {
            tb.IsChecked = state ?? !(tb.IsChecked ?? false);
            return ("property:IsChecked", null);
        }
        throw Fail($"{Name(c)} is not a toggle (CheckBox, ToggleButton, RadioButton, ToggleSwitch). Use `invoke` to click it.");
    }

    (string, string?) ExpandCollapse(Control c, bool expand)
    {
        if (Provider<IExpandCollapseProvider>(c) is { } ec)
        {
            if (expand) ec.Expand(); else ec.Collapse();
            return ("peer:IExpandCollapseProvider", null);
        }
        switch (c)
        {
            case Expander e: e.IsExpanded = expand; return ("property:IsExpanded", null);
            case ComboBox cb: cb.IsDropDownOpen = expand; return ("property:IsDropDownOpen", null);
            case TreeViewItem t: t.IsExpanded = expand; return ("property:IsExpanded", null);
            case MenuItem m: m.IsSubMenuOpen = expand; return ("property:IsSubMenuOpen", null);
        }
        throw Fail($"{Name(c)} cannot be expanded or collapsed (Expander, ComboBox, TreeViewItem, MenuItem can).");
    }

    // ---------------------------------------------------------------- text

    (string, string?) SetText(Control c, string text)
    {
        if (c is TextBox { IsReadOnly: true })
            throw Fail($"{Name(c)} is read-only.");
        if (Provider<IValueProvider>(c) is { } vp && !vp.IsReadOnly)
        {
            vp.SetValue(text);
            return ("peer:IValueProvider", null);
        }
        if (c is TextBox tb) { tb.Text = text; return ("property:Text", null); }
        if (c is AutoCompleteBox ac) { ac.Text = text; return ("property:Text", null); }
        if (c is NumericUpDown nud && decimal.TryParse(text, NumberStyles.Any, Inv, out var d)) { nud.Value = d; return ("property:Value", null); }
        throw Fail($"{Name(c)} does not accept text (TextBox, AutoCompleteBox, NumericUpDown do).");
    }

    (string, string?) TypeText(Control c, string text, bool replace)
    {
        var target = c as InputElement ?? throw Fail($"{Name(c)} cannot take keyboard input.");
        if (!target.Focusable && c is not TextBox) throw Fail($"{Name(c)} is not focusable; typeText needs a text input control (TextBox).");
        target.Focus();
        if (c is TextBox box)
        {
            if (replace) box.SelectAll();
            else box.CaretIndex = box.Text?.Length ?? 0;   // append, like typing after clicking at the end of the text
        }
        // Same event a text input method / keyboard layout raises: TextBox inserts at the caret and updates bindings.
        foreach (var ch in text.EnumerateRunes())
            target.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Source = target, Text = ch.ToString() });
        return ("synth:TextInput", null);
    }

    // ---------------------------------------------------------------- focus / key

    (string, string?) Focus(Control c)
    {
        if (!c.Focusable)
            throw Fail($"{Name(c)} is not focusable (Focusable=False). Focus a control inside it, e.g. a TextBox or Button.");
        if (!c.Focus(NavigationMethod.Unspecified))
            throw Fail($"{Name(c)} refused focus (it may not be attached to an active window).");
        return ("input:Focus", null);
    }

    static readonly Dictionary<string, string> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esc"] = "Escape", ["return"] = "Enter", ["del"] = "Delete", ["ctl"] = "Ctrl", ["control"] = "Ctrl", ["cmd"] = "Meta",
        ["win"] = "Meta", ["super"] = "Meta", ["option"] = "Alt", ["pgup"] = "PageUp", ["pgdn"] = "PageDown", ["ins"] = "Insert",
        ["spacebar"] = "Space", ["arrowup"] = "Up", ["arrowdown"] = "Down", ["arrowleft"] = "Left", ["arrowright"] = "Right",
    };

    static KeyGesture ParseGesture(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => KeyAliases.TryGetValue(s, out var a) ? a : s).ToArray();
        if (parts.Length == 0) throw Fail($"'{text}' is not a key. Examples: Enter, Escape, Tab, Ctrl+S, Shift+Tab, F5, Alt+Down.");
        try { return KeyGesture.Parse(string.Join("+", parts)); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            throw Fail($"'{text}' is not a key Avalonia understands. Examples: Enter, Escape, Tab, Ctrl+S, Shift+Tab, F5, Alt+Down.");
        }
    }

    (string, string?) PressKey(Control c, string key)
    {
        var gesture = ParseGesture(key);
        // Keys go to the focused element like real typing. Focus the target first when it can take focus.
        InputElement target = c;
        if (c is TopLevel tl) target = tl.FocusManager?.GetFocusedElement() as InputElement ?? c;
        else if (c.Focusable && c.IsEffectivelyEnabled) c.Focus();
        if (c is not TopLevel) target = c;

        KeyEventArgs Make(RoutedEvent ev) => new() { RoutedEvent = ev, Key = gesture.Key, KeyModifiers = gesture.KeyModifiers, Source = target };
        var down = Make(InputElement.KeyDownEvent);
        target.RaiseEvent(down);
        var handledBy = down.Handled ? "handled" : "not handled";
        // Hot keys (KeyBinding) are resolved by the platform input pipeline in a real app; resolve them here.
        var usedBinding = false;
        if (!down.Handled)
            for (var v = target as Visual; v is not null && !usedBinding; v = v.GetVisualParent())
                if (v is InputElement ie)
                    foreach (var kb in ie.KeyBindings.Where(k => k.Gesture is { } g && g.Matches(down)))
                    {
                        kb.TryHandle(down);
                        usedBinding = true;
                        break;
                    }
        target.RaiseEvent(Make(InputElement.KeyUpEvent));
        return ("synth:KeyDown/KeyUp", $"{gesture} sent to {Name(target)} ({(usedBinding ? "matched a KeyBinding" : handledBy)})");
    }

    // ---------------------------------------------------------------- selection

    static SelectingItemsControl? SelectionContainerOf(Control c) =>
        c as SelectingItemsControl ?? (c.GetVisualAncestors().OfType<SelectingItemsControl>().FirstOrDefault() is { } sic
                                        && sic.IndexFromContainer(c) >= 0 ? sic : null);

    (string, string?) Select(Control c, string? text, int? index)
    {
        if (c is SelectingItemsControl sic)
        {
            var i = index ?? FindItem(sic, text ?? throw Fail($"select on {Name(c)} needs `text` (item text) or `index`."));
            if (i < 0 || i >= sic.ItemCount) throw Fail($"{Name(c)} has {sic.ItemCount} items; index {i} is out of range.");
            if (sic.ContainerFromIndex(i) is Control container && Provider<ISelectionItemProvider>(container) is { } sip)
            {
                sip.Select();
                if (sic.SelectedIndex == i) return ("peer:ISelectionItemProvider", null);
            }
            sic.SelectedIndex = i;
            return ("property:SelectedIndex", null);
        }
        // An item itself (ListBoxItem, ComboBoxItem, TabItem, TreeViewItem…).
        if (Provider<ISelectionItemProvider>(c) is { } item)
        {
            item.Select();
            return ("peer:ISelectionItemProvider", null);
        }
        if (SelectionContainerOf(c) is { } owner)
        {
            owner.SelectedIndex = owner.IndexFromContainer(c);
            return ("property:SelectedIndex", null);
        }
        if (c.GetVisualAncestors().OfType<TreeView>().FirstOrDefault() is { } tree)
        {
            tree.SelectedItem = c.DataContext ?? c;
            return ("property:SelectedItem", null);
        }
        throw Fail($"{Name(c)} is not selectable. Select an item of a ListBox/ComboBox/TabControl by passing the list as element with `text` or `index`.");
    }

    int FindItem(SelectingItemsControl sic, string text)
    {
        var texts = new List<string>();
        var i = 0;
        foreach (var item in sic.Items)
        {
            var container = sic.ContainerFromIndex(i);
            texts.Add(ItemText(item, container));
            i++;
        }
        var exact = texts.FindIndex(t => string.Equals(t.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact >= 0) return exact;
        var partial = texts.FindIndex(t => t.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (partial >= 0) return partial;
        throw Fail($"No item of {Name(sic)} matches '{text}'. Items: " +
                   string.Join(", ", texts.Take(12).Select((t, k) => $"[{k}] \"{t}\"")) + (texts.Count > 12 ? $" … (+{texts.Count - 12})" : "") + ".");
    }

    static string ItemText(object? item, Control? container) =>
        item switch
        {
            null => "",
            string s => s,
            ContentControl { Content: string s } => s,
            ContentControl { Content: TextBlock { Text: { } t } } => t,
            HeaderedContentControl { Header: string h } => h,
            _ => (container is null ? null : TreeWalker.TextOf(container)) ?? item.ToString() ?? "",
        };

    (string, string?) ScrollIntoView(Control c, string? text, int? index)
    {
        if (c is ItemsControl ic && (text is not null || index is not null))
        {
            var i = index ?? (ic is SelectingItemsControl s ? FindItem(s, text!) : throw Fail("scrollIntoView with `text` works on ListBox/ComboBox; pass `index` instead."));
            ic.ScrollIntoView(i);
            return ("control:ScrollIntoView", $"item {i}");
        }
        c.BringIntoView();
        return ("control:BringIntoView", null);
    }

    // ---------------------------------------------------------------- pointer (synthesized, last resort)

    (string, string?) Pointer(Control c, InputActParams p)
    {
        var tl = TopLevel.GetTopLevel(c) ?? throw Fail($"{Name(c)} is not attached to a window.");
        var local = new Point(p.X ?? c.Bounds.Width / 2, p.Y ?? c.Bounds.Height / 2);
        var rootPoint = c.TranslatePoint(local, tl) ?? throw Fail($"{Name(c)} has no position in its window.");
        var hit = tl.GetVisualsAt(rootPoint, v => v.IsVisible).FirstOrDefault();
        // No rendered frame yet (headless, or the window was just shown): the platform hit test is empty; deliver to the element itself.
        var unverified = hit is null;
        if (!unverified && !hit!.GetSelfAndVisualAncestors().Contains(c))
        {
            var what = Name(hit is Control hc ? OwnerOf(hc) : hit!);
            throw Fail($"The point ({Fmt(rootPoint.X)}, {Fmt(rootPoint.Y)}) of {Name(c)} is covered by {what}, so a click would not reach it. " +
                       "Scroll it into view (scrollIntoView), or close what covers it.");
        }
        var source = unverified ? c : hit!.GetSelfAndVisualAncestors().OfType<Interactive>().First();
        var right = string.Equals(p.Button, "right", StringComparison.OrdinalIgnoreCase);
        var middle = string.Equals(p.Button, "middle", StringComparison.OrdinalIgnoreCase);
        var button = right ? MouseButton.Right : middle ? MouseButton.Middle : MouseButton.Left;
        var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var (mod, downKind, upKind) = button switch
        {
            MouseButton.Right => (RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed, PointerUpdateKind.RightButtonReleased),
            MouseButton.Middle => (RawInputModifiers.MiddleMouseButton, PointerUpdateKind.MiddleButtonPressed, PointerUpdateKind.MiddleButtonReleased),
            _ => (RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed, PointerUpdateKind.LeftButtonReleased),
        };
        var ts = (ulong)Environment.TickCount64;
        for (var n = 1; n <= Math.Clamp(p.ClickCount, 1, 3); n++)
        {
            source.RaiseEvent(new PointerPressedEventArgs(source, pointer, tl, rootPoint, ts, new PointerPointProperties(mod, downKind), KeyModifiers.None, n));
            source.RaiseEvent(new PointerReleasedEventArgs(source, pointer, tl, rootPoint, ts, new PointerPointProperties(RawInputModifiers.None, upKind), KeyModifiers.None, button));
        }
        return ("synth:pointer", $"{button} click ×{Math.Clamp(p.ClickCount, 1, 3)} at ({Fmt(rootPoint.X)}, {Fmt(rootPoint.Y)}) {(unverified ? "delivered to the element directly (no rendered frame yet, so Avalonia cannot hit-test; controls that re-check the position on release may ignore it)" : "hit " + Name(hit is Control hc2 ? OwnerOf(hc2) : hit!))}");
    }

    static string Fmt(double d) => Math.Round(d, 1).ToString("0.#", Inv);

    /// <summary>The element the developer wrote: climbs out of control templates.</summary>
    static StyledElement OwnerOf(Control c)
    {
        StyledElement cur = c;
        while (cur.TemplatedParent is StyledElement owner) cur = owner;
        return cur;
    }

    // ---------------------------------------------------------------- state description

    static string? StateOf(object o)
    {
        switch (o)
        {
            case ToggleButton tb:
                return tb.IsChecked switch { true => "checked", false => "unchecked", _ => "indeterminate" };
            case MenuItem m when m.HasSubMenu:
                return m.IsSubMenuOpen ? "submenu open" : "submenu closed";
            case SelectingItemsControl sic:
                var sel = sic.SelectedItem;
                var open = sic is ComboBox { IsDropDownOpen: true } ? ", dropdown open" : "";
                return sel is null ? "nothing selected" + open
                    : $"selected \"{ItemText(sel, sic.ContainerFromIndex(sic.SelectedIndex))}\" (index {sic.SelectedIndex} of {sic.ItemCount}){open}";
            case TextBox t:
                return $"text \"{(t.Text?.Length > 60 ? t.Text[..59] + "…" : t.Text)}\"" ;
            case Expander e:
                return e.IsExpanded ? "expanded" : "collapsed";
            case TreeViewItem tv:
                return tv.IsExpanded ? "expanded" : "collapsed";
            case InputElement { IsFocused: true }:
                return "focused";
        }
        return null;
    }

    // =================================================================== wait/until

    async Task<WaitUntilResult> WaitAsync(WaitUntilParams p, CancellationToken ct)
    {
        var timeout = Math.Clamp(p.TimeoutMs, 0, 15000);
        if (p.Condition != WaitCondition.Idle && p.Id is null && p.Selector is null)
            throw new SpyglassProtocolException(ErrorCodes.InvalidParams, "wait/until needs `id` or `selector` (except for condition idle).");
        if (p.Condition == WaitCondition.PropertyEquals && (p.Property is null || p.Value is null))
            throw new SpyglassProtocolException(ErrorCodes.InvalidParams, "propertyEquals needs `property` and `value`.");
        if (p.Selector is not null) _ = new SelectorQuery(p.Selector);   // fail fast on syntax errors

        var sw = Stopwatch.StartNew();
        var idleSince = (Stopwatch?)null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (met, result) = await Ui(() => Check(p));
            if (p.Condition == WaitCondition.Idle)
            {
                // Idle = everything queued for layout / render / data flow has run, and it stays that way for 100 ms.
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (idleSince is null) idleSince = Stopwatch.StartNew();
                else if (idleSince.ElapsedMilliseconds >= 100) met = true;
                result = result with { Met = met };
            }
            if (met) return result with { Met = true, ElapsedMs = (int)sw.ElapsedMilliseconds };
            if (sw.ElapsedMilliseconds >= timeout) return result with { Met = false, ElapsedMs = (int)sw.ElapsedMilliseconds };
            await Task.Delay(Math.Min(30, Math.Max(1, timeout - (int)sw.ElapsedMilliseconds)), ct);
        }
    }

    (bool Met, WaitUntilResult Result) Check(WaitUntilParams p)
    {
        if (p.Condition == WaitCondition.Idle) return (false, new WaitUntilResult(false, 0, 0) { Actual = "waiting for layout/render queues to drain" });

        var matches = Resolve(p);
        var el = matches.FirstOrDefault();
        NodeDto? node = el is null ? null : walker.Describe(el) with { ChildCount = 0 };
        string? actual = el is null
            ? p.Id is not null ? "the element is no longer in the tree" : "no element matches the selector"
            : matches.Count > 1 ? $"{matches.Count} elements match (using the first)" : null;

        bool met;
        switch (p.Condition)
        {
            case WaitCondition.Exists: met = el is not null; break;
            case WaitCondition.Gone: met = el is null; break;
            case WaitCondition.Visible: met = el is Visual { IsEffectivelyVisible: true }; actual ??= node is { IsVisible: false } ? HiddenWhy(el!) : null; break;
            case WaitCondition.Hidden: met = el is null || el is Visual { IsEffectivelyVisible: false }; break;
            case WaitCondition.Enabled: met = el is InputElement { IsEffectivelyEnabled: true }; actual ??= node is { IsEnabled: false } ? "still disabled" : null; break;
            case WaitCondition.PropertyEquals:
                var cur = el is null ? null : ReadProperty(el, p.Property!);
                met = cur is not null && ValueEquals(cur, p.Value!);
                actual = el is null ? actual : cur is null ? $"{el.GetType().Name} has no property '{p.Property}'" : cur;
                break;
            default: met = false; break;
        }
        return (met, new WaitUntilResult(met, 0, matches.Count) { Node = node, Actual = actual });
    }

    static string HiddenWhy(object el) =>
        el is Visual v && v.GetSelfAndVisualAncestors().FirstOrDefault(a => !a.IsVisible) is { } culprit
            ? ReferenceEquals(culprit, v) ? "IsVisible=False" : $"{Name(culprit)} has IsVisible=False"
            : "not visible";

    List<object> Resolve(WaitUntilParams p)
    {
        if (p.Id is { } id)
        {
            if (!registry.TryGet<object>(id, out var o)) return [];
            // A node that left the visual tree no longer exists for the purposes of exists/gone.
            return o is Visual v && TopLevel.GetTopLevel(v) is null ? [] : [o];
        }
        var roots = walker.Roots().Select(r => (object)r.TopLevel);
        return new SelectorQuery(p.Selector!).Run(walker, roots, TreeKind.Visual, false).ToList();
    }

    /// <summary>The current value of an Avalonia or CLR property as the text a user would compare against.</summary>
    string? ReadProperty(object o, string name)
    {
        if (o is AvaloniaObject ao)
        {
            var type = ao.GetType();
            var prop = AvaloniaPropertyRegistry.Instance.GetRegistered(type).Concat(AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(type))
                .FirstOrDefault(x => x.Name == name);
            if (prop is not null) return codec.Encode(ao.GetValue(prop)).Text;
        }
        var clr = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (clr is not null && clr.GetIndexParameters().Length == 0) return codec.Encode(clr.GetValue(o)).Text;
        var field = o.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
        return field is null ? null : codec.Encode(field.GetValue(o)).Text;
    }

    static bool ValueEquals(string actual, string expected)
    {
        expected = expected.Trim();
        if (expected is "{x:Null}" or "null") expected = "(null)";
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return true;
        return double.TryParse(actual, NumberStyles.Float, Inv, out var a) && double.TryParse(expected, NumberStyles.Float, Inv, out var b) && Math.Abs(a - b) < 1e-9;
    }
}
