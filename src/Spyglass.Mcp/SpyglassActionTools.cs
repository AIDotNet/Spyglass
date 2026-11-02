using System.ComponentModel;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Spyglass.App.Core;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Mcp;

/// <summary>
/// "Act" tools (design/mcp/01-tools.md #12–#16): property_set, state_force, ui_interact, wait_for, changes_reset.
/// Everything is runtime-only and undoable with changes_reset; nothing is ever written to source files.
/// Server-wide switches (environment of the MCP host process): SPYGLASS_MCP_READONLY=1 refuses every tool that changes
/// the app (property_set, state_force, ui_interact, changes_reset); SPYGLASS_MCP_NOINPUT=1 refuses only ui_interact.
/// </summary>
[McpServerToolType]
public sealed class SpyglassActionTools(SpyglassHub hub)
{
    const string AppParam = "Connected app (id like 'a1', name, or pid). Optional when only one app is connected.";
    const string ElementParam = "Element: a reference like 'e42' from earlier output, a selector ('Button#Save', 'ListBox > ListBoxItem:nth(3)'), 'text=Save', or 'xaml=MainView.axaml:23'.";

    public const string ReadOnlyVariable = "SPYGLASS_MCP_READONLY";
    public const string NoInputVariable = "SPYGLASS_MCP_NOINPUT";

    static bool IsOn(string variable) =>
        Environment.GetEnvironmentVariable(variable)?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    static void RequireWritable(string tool)
    {
        if (IsOn(ReadOnlyVariable))
            throw new McpException($"{tool} is disabled: the Spyglass MCP server runs read-only ({ReadOnlyVariable}=1). " +
                                   $"To allow it, unset {ReadOnlyVariable} in the environment that starts the MCP server and restart it. " +
                                   "Read-only tools (ui_snapshot, element_inspect, wait_for…) still work.");
    }

    static void RequireInput(string tool)
    {
        RequireWritable(tool);
        if (IsOn(NoInputVariable))
            throw new McpException($"{tool} is disabled: UI input is switched off ({NoInputVariable}=1). " +
                                   $"To allow it, unset {NoInputVariable} in the environment that starts the MCP server and restart it. " +
                                   "property_set and state_force still work.");
    }

    static AgentSession NeedInput(AgentSession s)
    {
        if (!s.Has(Capabilities.Input))
            throw new McpException($"{s.App.Name}'s agent does not support input actions; update Spyglass.Agent.");
        return s;
    }

    async Task<int> Target(AgentSession s, string element) => await ElementResolver.ElementAsync(s, element);

    static async Task<long> ErrorCursor(AgentSession s) =>
        s.Has(Capabilities.BindingErrors) ? (await s.GetBindingErrorsAsync()).Select(e => e.Seq).DefaultIfEmpty(0).Max() : 0;

    static async Task<IReadOnlyList<BindingErrorDto>> ErrorsSince(AgentSession s, long cursor)
    {
        if (!s.Has(Capabilities.BindingErrors)) return [];
        await Task.Delay(50);   // binding errors raised by the change are logged on the UI thread right after it
        return await s.GetBindingErrorsAsync(since: cursor);
    }

    static void AppendErrors(StringBuilder sb, IReadOnlyList<BindingErrorDto> errors, string indent = "  ")
    {
        foreach (var e in errors.Take(5))
            sb.Append(indent).Append($"⚠ binding error on {e.TargetType}.{e.Property}: {e.Message}")
              .Append(e.Location is { } l ? $" ({Format.Location(l).Split("   ")[0]})" : "").Append('\n');
    }

    // ---------------------------------------------------------------- property_set

    [McpServerTool(Name = "property_set", Title = "Set a property at runtime", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Changes a property of a live element, to try a fix without restarting the app (e.g. Margin, Background, Width, IsVisible, Text, Classes via state_force). " +
                 "The value is parsed like XAML text: 'Red', '#ff3366', '12,6' (Thickness), 'True', '120', 'Auto', 'Center'; '{x:Null}' sets null and '{x:Unset}' clears the local value. " +
                 "Runtime only: the app's source is never modified and the change is lost on restart. Returns the old and new value and where the element is defined. " +
                 "Undo with changes_reset; make the fix permanent by editing the XAML/C# yourself.")]
    public async Task<string> PropertySet(
        [Description(ElementParam)] string element,
        [Description("Property name, e.g. 'Margin', 'IsVisible', or 'Grid.Row' for attached properties.")] string property,
        [Description("New value as XAML text.")] string value,
        [Description(AppParam)] string? app = null)
    {
        RequireWritable("property_set");
        var s = ElementResolver.App(hub, app);
        var id = await Target(s, element);
        var (owner, name) = SplitProperty(property);

        IReadOnlyList<PropertyDto> all;
        try { all = await s.GetPropertiesAsync(id); }
        catch (SpyglassProtocolException ex) { throw ElementResolver.Explain(ex, element); }
        var old = all.FirstOrDefault(p => p.Name == name && (owner is null || p.Owner == owner))
                  ?? all.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (owner is null || p.Owner == owner));
        if (old is null)
        {
            var similar = all.Where(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(p.Name, StringComparison.OrdinalIgnoreCase)).Take(6).Select(p => p.Name).ToList();
            throw new McpException($"Element e{id} has no property '{property}'." +
                                   (similar.Count > 0 ? $" Similar: {string.Join(", ", similar)}." : " Call element_properties to list its properties."));
        }
        if (old.IsReadOnly) throw new McpException($"{old.Owner}.{old.Name} is read-only (it is computed by Avalonia); it cannot be set. Set the property it derives from instead.");

        var cursor = await ErrorCursor(s);
        PropertyDto now;
        try { now = await s.SetPropertyAsync(id, old.Name, old.Owner, ParseValue(value)); }
        catch (SpyglassProtocolException ex) when (ex.Code is ErrorCodes.ConversionFailed or ErrorCodes.SetValueFailed)
        {
            throw new McpException($"{ex.Message} {old.Owner}.{old.Name} currently is {old.Value.Kind.ToString().ToLowerInvariant()} \"{old.Value.Text}\" — use the same format.");
        }
        catch (SpyglassProtocolException ex) { throw ElementResolver.Explain(ex, element); }

        var node = (await s.DescribeAsync(id)).Node;
        var sb = new StringBuilder();
        sb.Append($"Set {Format.Compound(node)} [e{id}] {old.Owner}.{old.Name}: {old.Value.Text} → {now.Value.Text}");
        sb.Append($"   (was {old.Priority}{(old.HasBinding ? ", bound" : "")}; now {now.Priority}{(now.HasBinding ? ", still bound" : "")})\n");
        if (old.HasBinding)
            sb.Append("  Note: the property was bound; the local value overrides the binding until changes_reset.\n");
        if (!SameValue(now.Value, ParseValue(value)))
            sb.Append($"  Note: the value read back differs from '{value}' (coerced or overridden by Avalonia).\n");
        AppendErrors(sb, await ErrorsSince(s, cursor));
        var src = s.Has(Capabilities.XamlSource) ? await s.GetSourceAsync(id) : null;
        sb.Append("  Runtime change only — nothing was written to source. Undo with changes_reset");
        sb.Append(src is null ? ".\n" : $"; for a permanent fix edit xaml: {Format.Location(src).Split("   ")[0]}.\n");
        return sb.ToString().TrimEnd();
    }

    static (string? Owner, string Name) SplitProperty(string p)
    {
        p = p.Trim();
        var dot = p.LastIndexOf('.');
        return dot > 0 ? (p[..dot], p[(dot + 1)..]) : (null, p);
    }

    static ValueDto ParseValue(string text)
    {
        var t = text.Trim();
        if (t is "{x:Null}" or "{Null}") return new ValueDto(ValueKind.Null, "(null)");
        if (t is "{x:Unset}" or "{Unset}" or "(unset)") return new ValueDto(ValueKind.Unset, "(unset)");
        return new ValueDto(ValueKind.String, text);
    }

    static bool SameValue(ValueDto now, ValueDto asked) =>
        asked.Kind is ValueKind.Null or ValueKind.Unset
        || string.Equals(now.Text.Trim(), asked.Text.Trim(), StringComparison.OrdinalIgnoreCase)
        || double.TryParse(now.Text, System.Globalization.CultureInfo.InvariantCulture, out var a) && double.TryParse(asked.Text, System.Globalization.CultureInfo.InvariantCulture, out var b) && a == b
        || now.Kind is not (ValueKind.String or ValueKind.Number or ValueKind.Bool or ValueKind.Enum);   // brushes/colours/thickness re-format their text

    // ---------------------------------------------------------------- state_force

    [McpServerTool(Name = "state_force", Title = "Force a pseudo-class or style class", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Turns a pseudo-class (':pointerover', ':pressed', ':focus', ':disabled', ':checked', ':error'…) or a style class ('primary', 'danger') on or off for an element, " +
                 "to inspect how styles look in that state without moving the mouse. Pass `pseudoClass` or `class`, not both. " +
                 "The control may reset states it manages itself (e.g. :pointerover when the real mouse moves). Undo with changes_reset.")]
    public async Task<string> StateForce(
        [Description(ElementParam)] string element,
        [Description("Pseudo-class including the colon, e.g. ':pointerover'.")] string? pseudoClass = null,
        [Description("Style class without the dot, e.g. 'primary'.")] string? @class = null,
        [Description("true = add, false = remove. Default true.")] bool active = true,
        [Description(AppParam)] string? app = null)
    {
        RequireWritable("state_force");
        if ((pseudoClass is null) == (@class is null))
            throw new McpException("Pass exactly one of `pseudoClass` (e.g. ':pointerover') or `class` (e.g. 'primary').");
        var s = ElementResolver.App(hub, app);
        var id = await Target(s, element);
        ClassesResult before;
        try { before = await s.GetClassesAsync(id); }
        catch (SpyglassProtocolException ex) { throw ElementResolver.Explain(ex, element); }

        string label;
        if (pseudoClass is not null)
        {
            var pc = pseudoClass.StartsWith(':') ? pseudoClass : ":" + pseudoClass;
            if (active && !before.KnownPseudoClasses.Contains(pc))
                throw new McpException($"'{pc}' is not a pseudo-class of this control. Known: {string.Join(", ", before.KnownPseudoClasses)}.");
            await s.SetPseudoClassAsync(id, pc, active);
            label = pc;
        }
        else
        {
            var cls = @class!.TrimStart('.');
            var list = before.Classes.Where(c => c != cls).ToList();
            if (active) list.Add(cls);
            await s.SetClassesAsync(id, list);
            label = "." + cls;
        }
        var after = await s.GetClassesAsync(id);
        var node = (await s.DescribeAsync(id)).Node;
        var all = after.Classes.Select(c => "." + c).Concat(after.PseudoClasses).ToList();
        return $"{(active ? "Forced" : "Cleared")} {label} on {Format.Compound(node)} [e{id}]. Now: {(all.Count == 0 ? "(no classes)" : string.Join(" ", all))}\n" +
               "  Runtime only; undo with changes_reset. Take a screenshot (highlight=element) to see the state, or element_inspect to read the resulting values.";
    }

    // ---------------------------------------------------------------- ui_interact

    static readonly Dictionary<string, InputAction> Actions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["invoke"] = InputAction.Invoke, ["click"] = InputAction.Invoke, ["setText"] = InputAction.SetText, ["typeText"] = InputAction.TypeText,
        ["toggle"] = InputAction.Toggle, ["select"] = InputAction.Select, ["expand"] = InputAction.Expand, ["collapse"] = InputAction.Collapse,
        ["focus"] = InputAction.Focus, ["key"] = InputAction.Key, ["scrollIntoView"] = InputAction.ScrollIntoView, ["pointer"] = InputAction.Pointer,
    };

    [McpServerTool(Name = "ui_interact", Title = "Operate the app's UI", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Operates a control of the connected app the way a user (or assistive technology) would, to reproduce a bug or verify a fix. " +
                 "Actions: invoke (click a Button/MenuItem), setText (replace a TextBox's text), typeText (type characters like a keyboard; replace=true selects all first), " +
                 "toggle (CheckBox/ToggleButton; state=true/false to set explicitly), select (item of a ListBox/ComboBox/TabControl: element is the list, text or index chooses the item; or an item itself), " +
                 "expand/collapse (Expander, ComboBox dropdown, TreeViewItem, MenuItem), focus, key (e.g. 'Enter', 'Escape', 'Ctrl+S', 'Shift+Tab' sent to the element, or to the focused element when element is a window), " +
                 "scrollIntoView, pointer (click at x,y relative to the element — last resort when no semantic action fits). " +
                 "It uses the control's automation pattern when it has one, otherwise synthesized input; the result says which. " +
                 "The result also reports the element's state afterwards and any binding errors or warnings raised while the action ran. " +
                 "Disabled or hidden elements are refused (like for a real user). Acts on the connected app only, never on other programs.")]
    public async Task<string> UiInteract(
        [Description(ElementParam)] string element,
        [Description("invoke | setText | typeText | toggle | select | expand | collapse | focus | key | scrollIntoView | pointer")] string action,
        [Description("setText/typeText: the text. select: the item text (exact, else contains).")] string? text = null,
        [Description("key: the key or gesture, e.g. 'Enter', 'Escape', 'Ctrl+S', 'Shift+Tab', 'F5'.")] string? key = null,
        [Description("select/scrollIntoView: zero-based item index (alternative to text).")] int? index = null,
        [Description("toggle: true = checked, false = unchecked. Default: flip.")] bool? state = null,
        [Description("pointer: x offset in the element (default: centre).")] double? x = null,
        [Description("pointer: y offset in the element (default: centre).")] double? y = null,
        [Description("pointer: 'left' (default), 'right' or 'middle'.")] string? button = null,
        [Description("pointer: number of clicks (double click = 2).")] int clickCount = 1,
        [Description("typeText: select all existing text first, so the text replaces it.")] bool replace = false,
        [Description(AppParam)] string? app = null)
    {
        RequireInput("ui_interact");
        if (!Actions.TryGetValue(action.Trim(), out var act))
            throw new McpException($"Unknown action '{action}'. Use: invoke, setText, typeText, toggle, select, expand, collapse, focus, key, scrollIntoView, pointer.");
        var s = NeedInput(ElementResolver.App(hub, app));
        var id = await Target(s, element);

        InputActResult r;
        try
        {
            r = await s.ActAsync(new InputActParams(id, act)
            {
                Text = text, Key = key, Index = index, State = state, X = x, Y = y, Button = button, ClickCount = clickCount, Replace = replace,
            });
        }
        catch (SpyglassProtocolException ex) when (ex.Code == ErrorCodes.ActionFailed) { throw new McpException(ex.Message + (ex.Message.Contains(" is disabled") || ex.Message.Contains(" is not visible") ? "" : NextStep(act))); }
        catch (SpyglassProtocolException ex) { throw ElementResolver.Explain(ex, element); }

        var sb = new StringBuilder();
        sb.Append($"✓ {Verb(act, text, key)} {Format.Compound(r.Node)} [e{r.Node.Id}]  via {r.Via}\n");
        sb.Append("  now: ").Append(Format.NodeLine(r.Node, withGeometry: false, extra: r.State is { } st && !Format.NodeLine(r.Node, false).Contains(st) ? $"· {st}" : null)).Append('\n');
        if (r.Note is not null) sb.Append("  ").Append(r.Note).Append('\n');
        sb.Append($"  during the action: {r.BindingErrors} binding error{(r.BindingErrors == 1 ? "" : "s")}, {r.LogWarnings} log warning{(r.LogWarnings == 1 ? "" : "s")}\n");
        if (r.NewBindingErrors is { } errs) AppendErrors(sb, errs);
        foreach (var l in r.NewLogs ?? []) sb.Append($"  ⚠ {l.Level} [{l.Area}] {l.Message}\n");
        if (r.BindingErrors + r.LogWarnings > 0) sb.Append("  Use binding_errors / logs_read for details.\n");
        return sb.ToString().TrimEnd();
    }

    static string Verb(InputAction a, string? text, string? key) => a switch
    {
        InputAction.Invoke => "invoked",
        InputAction.SetText => $"set text of",
        InputAction.TypeText => $"typed into",
        InputAction.Toggle => "toggled",
        InputAction.Select => text is null ? "selected" : $"selected \"{text}\" in",
        InputAction.Expand => "expanded",
        InputAction.Collapse => "collapsed",
        InputAction.Focus => "focused",
        InputAction.Key => $"pressed {key} on",
        InputAction.ScrollIntoView => "scrolled into view",
        _ => "clicked at point on",
    };

    static string NextStep(InputAction a) => a switch
    {
        InputAction.Invoke => " Try action 'pointer' for a plain click, or pick the inner control with ui_find.",
        InputAction.SetText or InputAction.TypeText => " Find the TextBox with ui_find 'TextBox'.",
        InputAction.Select => " For a list, pass the list as element with `text` or `index`.",
        _ => "",
    };

    // ---------------------------------------------------------------- wait_for

    [McpServerTool(Name = "wait_for", Title = "Wait for a UI condition", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Waits (polling inside the app, once per UI tick) until a condition holds, instead of guessing with sleeps — e.g. after ui_interact starts a load. " +
                 "Conditions: exists, gone, visible, hidden, enabled, propertyEquals (property + value), idle (layout/render queues drained and quiet for 100 ms; no element needed). " +
                 "`element` may be a selector for something that does not exist yet. On timeout it returns the element's CURRENT state so you can see what is wrong.")]
    public async Task<string> WaitFor(
        [Description("exists | gone | visible | hidden | enabled | propertyEquals | idle")] string condition,
        [Description(ElementParam + " Not needed for idle.")] string? element = null,
        [Description("propertyEquals: property name, e.g. 'Text', 'IsChecked'. CLR properties of the element also work.")] string? property = null,
        [Description("propertyEquals: expected value as text ('Done', 'True', '3'); '{x:Null}' for null.")] string? value = null,
        [Description("Maximum wait in milliseconds. Default 3000, max 15000.")] int timeoutMs = 3000,
        [Description(AppParam)] string? app = null)
    {
        var cond = condition.Trim().ToLowerInvariant() switch
        {
            "exists" => WaitCondition.Exists, "gone" => WaitCondition.Gone, "visible" => WaitCondition.Visible, "hidden" => WaitCondition.Hidden,
            "enabled" => WaitCondition.Enabled, "propertyequals" => WaitCondition.PropertyEquals, "idle" => WaitCondition.Idle,
            _ => throw new McpException($"Unknown condition '{condition}'. Use: exists, gone, visible, hidden, enabled, propertyEquals, idle."),
        };
        if (cond != WaitCondition.Idle && element is null) throw new McpException($"`element` is required for condition '{condition}'.");
        if (cond == WaitCondition.PropertyEquals && (property is null || value is null)) throw new McpException("propertyEquals needs `property` and `value`.");
        var s = NeedInput(ElementResolver.App(hub, app));

        var p = new WaitUntilParams(cond) { Property = property, Value = value, TimeoutMs = Math.Clamp(timeoutMs, 0, 15000) };
        if (element is not null)
            p = ElementResolver.TryParseRef(element, 'e', out var id) ? p with { Id = id } : p with { Selector = element };
        WaitUntilResult r;
        try { r = await s.WaitUntilAsync(p); }
        catch (SpyglassProtocolException ex) when (ex.Code == ErrorCodes.InvalidParams) { throw new McpException($"Invalid wait: {ex.Message}"); }

        var what = cond == WaitCondition.Idle ? "UI idle"
            : cond == WaitCondition.PropertyEquals ? $"{element} {property} = {value}" : $"{element} {condition.ToLowerInvariant()}";
        var sb = new StringBuilder();
        if (r.Met)
        {
            sb.Append($"✓ {what} after {r.ElapsedMs} ms");
            if (r.Node is { } n) sb.Append($": {Format.NodeLine(n)}");
            return sb.ToString();
        }
        sb.Append($"✗ Timed out after {r.ElapsedMs} ms waiting for: {what}\n  Current state: ");
        if (r.Node is { } cur) sb.Append(Format.NodeLine(cur));
        else if (cond == WaitCondition.Idle) sb.Append("the UI kept changing (still animating or re-laying out)");
        else sb.Append(r.Actual ?? "element not found");
        if (r.Actual is { } actual && r.Node is not null) sb.Append(cond == WaitCondition.PropertyEquals ? $"\n  {property} is currently \"{actual}\"" : $"\n  {actual}");
        if (r.Node is null && element is not null && !ElementResolver.TryParseRef(element, 'e', out _))
            sb.Append(await ElementResolver.SuggestAsync(s, element));
        sb.Append("\n  Next: check the cause with ui_snapshot / logs_read / binding_errors, or wait longer with timeoutMs.");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- changes_reset

    [McpServerTool(Name = "changes_reset", Title = "Undo Spyglass runtime changes", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Undoes the runtime changes made through property_set and state_force (property values, forced pseudo-classes and classes) — for one element, or for the whole app when `element` is omitted. " +
                 "Does not undo what ui_interact did (clicks, typed text); those are real app actions. Lists what was restored.")]
    public async Task<string> ChangesReset(
        [Description(ElementParam + " Omit to reset everything.")] string? element = null,
        [Description(AppParam)] string? app = null)
    {
        RequireWritable("changes_reset");
        var s = NeedInput(ElementResolver.App(hub, app));
        int? id = element is null ? null : await Target(s, element);
        ChangesResetResult r;
        try { r = await s.ResetChangesAsync(id); }
        catch (SpyglassProtocolException ex) { throw ElementResolver.Explain(ex, element ?? "the element"); }
        if (r.Restored.Count == 0)
            return element is null ? "Nothing to reset: Spyglass has not changed anything in this app." : $"Nothing to reset on {element}: Spyglass has not changed it.";
        var sb = new StringBuilder($"Restored {r.Restored.Count} change{(r.Restored.Count == 1 ? "" : "s")}{(element is null ? "" : $" on {element}")}:\n");
        foreach (var c in r.Restored) sb.Append($"  [e{c.Id}] {c.Type} {c.Detail}\n");
        return sb.ToString().TrimEnd();
    }
}
