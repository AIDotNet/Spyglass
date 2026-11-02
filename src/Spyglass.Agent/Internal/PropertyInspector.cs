using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Diagnostics;
using Avalonia.Layout;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Agent.Internal;

/// <summary>props/*, layout/get, styles/* (public Avalonia APIs only; spike S1). UI-thread only.</summary>
internal sealed class PropertyInspector(NodeRegistry registry, ValueCodec codec)
{
    /// <summary>Original values of properties changed through Spyglass, for props/reset.</summary>
    readonly ConditionalWeakTable<AvaloniaObject, Dictionary<AvaloniaProperty, Original>> _modified = new();
    readonly List<WeakReference<AvaloniaObject>> _modifiedObjects = new();

    sealed record Original(bool HadLocalValue, object? Value);

    static readonly string[] CommonPseudoClasses = [":pointerover", ":pressed", ":focus", ":focus-visible", ":focus-within", ":disabled"];

    // ---------- props/get ----------

    public PropsGetResult Get(PropsGetParams p)
    {
        var id = p.Id ?? p.Ref ?? throw new SpyglassProtocolException(ErrorCodes.InvalidParams, "id or ref is required");
        var target = registry.Get<object>(id);
        if (target is not AvaloniaObject ao)
            return new PropsGetResult(ClrProperties(target));

        var type = ao.GetType();
        var props = AvaloniaPropertyRegistry.Instance.GetRegistered(type)
            .Concat(AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(type).Where(ap => ao.IsSet(ap)))
            .Distinct();
        var list = new List<PropertyDto>();
        foreach (var prop in props)
            list.Add(Describe(ao, prop));
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return new PropsGetResult(list);
    }

    public PropertyDto Describe(AvaloniaObject ao, AvaloniaProperty prop)
    {
        var diag = ao.GetDiagnostic(prop);
        var priority = diag.Priority;
        // Read-only direct properties (Bounds, DesiredSize, Parent, IsEffectivelyEnabled…) are computed state:
        // nobody "set" them, so they never count as set (keeps "Only set" and MCP output focused).
        bool isSet = prop.IsDirect
            ? !prop.IsReadOnly && !Equals(diag.Value, DefaultOf(prop, ao))
            : priority is not BindingPriority.Unset and not BindingPriority.Inherited;
        if (prop.Name == "XamlSourceInfo") isSet = false;   // shown separately as the element's source location
        return new PropertyDto
        {
            Name = prop.Name,
            Owner = prop.OwnerType.Name,
            Kind = prop.IsAttached ? PropertyKind.Attached : prop.IsDirect ? PropertyKind.Direct : PropertyKind.Styled,
            Group = prop.OwnerType.Name,
            Value = codec.Encode(diag.Value),
            Priority = prop.IsDirect ? (prop.IsReadOnly ? "Computed" : isSet ? "LocalValue" : "Unset") : priority.ToString(),
            IsReadOnly = prop.IsReadOnly,
            IsSet = isSet,
            HasBinding = !prop.IsDirect && BindingOperations.GetBindingExpressionBase(ao, prop) is not null,
            IsModifiedBySpyglass = _modified.TryGetValue(ao, out var m) && m.ContainsKey(prop),
        };
    }

    static object? DefaultOf(AvaloniaProperty prop, AvaloniaObject ao) =>
        prop.GetMetadata(ao) is { } meta && meta is IDirectPropertyMetadata dm ? dm.UnsetValue : null;

    IReadOnlyList<PropertyDto> ClrProperties(object target)
    {
        // Drilled-into plain objects (e.g. a DataContext): only a summary in the MVP.
        return [new PropertyDto { Name = "(value)", Owner = target.GetType().Name, Kind = PropertyKind.Clr, Value = codec.Encode(target), Priority = "LocalValue", IsReadOnly = true, IsSet = true }];
    }

    // ---------- props/set / reset ----------

    public PropertyDto Set(PropsSetParams p)
    {
        var ao = registry.Get<AvaloniaObject>(p.Id);
        var prop = Find(ao, p.Name, p.Owner);
        if (prop.IsReadOnly) throw new SpyglassProtocolException(ErrorCodes.PropertyReadOnly, $"{p.Owner}.{p.Name} is read-only.");
        var value = codec.Decode(p.Value, prop.PropertyType);

        var originals = _modified.GetOrCreateValue(ao);
        if (!originals.ContainsKey(prop))
        {
            var diag = ao.GetDiagnostic(prop);
            originals[prop] = new Original(diag.Priority == BindingPriority.LocalValue, diag.Value);
            _modifiedObjects.Add(new WeakReference<AvaloniaObject>(ao));
        }
        try
        {
            if (ReferenceEquals(value, AvaloniaProperty.UnsetValue)) ao.ClearValue(prop);
            else ao.SetValue(prop, value);
        }
        catch (Exception ex)
        {
            throw new SpyglassProtocolException(ErrorCodes.SetValueFailed, $"{ex.GetType().Name}: {ex.Message}");
        }
        return Describe(ao, prop);
    }

    public void Reset(PropsResetParams p)
    {
        if (p.All)
        {
            foreach (var w in _modifiedObjects.ToList())
                if (w.TryGetTarget(out var o) && _modified.TryGetValue(o, out var m))
                    foreach (var prop in m.Keys.ToList()) Restore(o, prop, m);
            _modifiedObjects.Clear();
            RestoreForced(null);
            return;
        }
        var ao = registry.Get<AvaloniaObject>(p.Id ?? throw new SpyglassProtocolException(ErrorCodes.InvalidParams, "id is required"));
        if (p.Name is null && ao is StyledElement forcedOwner) RestoreForced(forcedOwner);
        if (!_modified.TryGetValue(ao, out var mods)) return;
        if (p.Name is null) { foreach (var prop in mods.Keys.ToList()) Restore(ao, prop, mods); return; }
        var target = Find(ao, p.Name, p.Owner ?? "");
        if (mods.ContainsKey(target)) Restore(ao, target, mods);
    }

    static void Restore(AvaloniaObject ao, AvaloniaProperty prop, Dictionary<AvaloniaProperty, Original> mods)
    {
        var o = mods[prop];
        if (o.HadLocalValue || prop.IsDirect) ao.SetValue(prop, o.Value);
        else ao.ClearValue(prop);
        mods.Remove(prop);
    }

    static AvaloniaProperty Find(AvaloniaObject ao, string name, string owner)
    {
        var type = ao.GetType();
        var all = AvaloniaPropertyRegistry.Instance.GetRegistered(type).Concat(AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(type));
        return all.FirstOrDefault(p => p.Name == name && (owner.Length == 0 || p.OwnerType.Name == owner))
               ?? all.FirstOrDefault(p => p.Name == name)
               ?? throw new SpyglassProtocolException(ErrorCodes.InvalidParams, $"{type.Name} has no property '{name}'.");
    }

    // ---------- layout/get ----------

    public LayoutResult Layout(int id)
    {
        var v = registry.Get<Visual>(id);
        var l = v as Layoutable;
        var bounds = TreeWalker.BoundsOf(v) ?? new RectData(0, 0, v.Bounds.Width, v.Bounds.Height);
        Thickness? border = v switch { TemplatedControl tc => tc.BorderThickness, Border b => b.BorderThickness, _ => null };
        Thickness? padding = v switch { TemplatedControl tc => tc.Padding, Decorator d => d.Padding, _ => null };

        var active = new List<string>();
        if (l is not null)
        {
            if (!double.IsNaN(l.Width)) active.Add("width");
            if (!double.IsNaN(l.Height)) active.Add("height");
            if (l.MinWidth > 0 && Near(l.Bounds.Width, l.MinWidth)) active.Add("minWidth");
            if (!double.IsInfinity(l.MaxWidth) && Near(l.Bounds.Width, l.MaxWidth)) active.Add("maxWidth");
            if (l.MinHeight > 0 && Near(l.Bounds.Height, l.MinHeight)) active.Add("minHeight");
            if (!double.IsInfinity(l.MaxHeight) && Near(l.Bounds.Height, l.MaxHeight)) active.Add("maxHeight");
        }

        return new LayoutResult
        {
            Bounds = bounds,
            DesiredSize = l is null ? new SizeData(0, 0) : new SizeData(l.DesiredSize.Width, l.DesiredSize.Height),
            Margin = l is null ? new ThicknessData(0, 0, 0, 0) : T(l.Margin),
            BorderThickness = border is { } bt ? T(bt) : null,
            Padding = padding is { } pd ? T(pd) : null,
            Width = l is null || double.IsNaN(l.Width) ? null : l.Width,
            Height = l is null || double.IsNaN(l.Height) ? null : l.Height,
            MinWidth = l?.MinWidth,
            MaxWidth = l is null || double.IsInfinity(l.MaxWidth) ? null : l.MaxWidth,
            MinHeight = l?.MinHeight,
            MaxHeight = l is null || double.IsInfinity(l.MaxHeight) ? null : l.MaxHeight,
            HorizontalAlignment = (l?.HorizontalAlignment ?? HorizontalAlignment.Stretch).ToString(),
            VerticalAlignment = (l?.VerticalAlignment ?? VerticalAlignment.Stretch).ToString(),
            ActiveConstraints = active,
        };
    }

    static bool Near(double a, double b) => Math.Abs(a - b) < 0.5;
    static ThicknessData T(Thickness t) => new(t.Left, t.Top, t.Right, t.Bottom);

    // ---------- styles ----------

    public ClassesResult Classes(int id)
    {
        var se = registry.Get<StyledElement>(id);
        var known = new SortedSet<string>(CommonPseudoClasses);
        for (var t = se.GetType(); t is not null; t = t.BaseType)
            foreach (var attr in t.GetCustomAttributes(typeof(PseudoClassesAttribute), false).Cast<PseudoClassesAttribute>())
                foreach (var pc in attr.PseudoClasses) known.Add(pc);
        return new ClassesResult(
            se.Classes.Where(c => !c.StartsWith(':')).ToList(),
            se.Classes.Where(c => c.StartsWith(':')).ToList(),
            known.ToList());
    }

    public void SetClasses(SetClassesParams p)
    {
        var se = registry.Get<StyledElement>(p.Id);
        var wanted = p.Classes.Where(c => !c.StartsWith(':')).ToList();
        foreach (var c in se.Classes.Where(c => !c.StartsWith(':') && !wanted.Contains(c)).ToList()) NoteForced(se, c);
        foreach (var c in wanted.Where(c => !se.Classes.Contains(c))) NoteForced(se, c);
        se.Classes.Replace(wanted);
    }

    public void SetPseudoClass(SetPseudoClassParams p)
    {
        if (!p.PseudoClass.StartsWith(':'))
            throw new SpyglassProtocolException(ErrorCodes.InvalidParams, "pseudo-classes start with ':'");
        var se = registry.Get<StyledElement>(p.Id);
        NoteForced(se, p.PseudoClass);
        ((IPseudoClasses)se.Classes).Set(p.PseudoClass, p.Active);
    }

    // ---------- forced classes / pseudo-classes: remembered so changes/reset can restore them ----------

    readonly ConditionalWeakTable<StyledElement, Dictionary<string, bool>> _forced = new();
    readonly List<WeakReference<StyledElement>> _forcedElements = new();

    /// <summary>Remembers whether <paramref name="cls"/> was present before Spyglass first touched it.</summary>
    void NoteForced(StyledElement se, string cls)
    {
        var d = _forced.GetOrCreateValue(se);
        if (d.ContainsKey(cls)) return;
        d[cls] = se.Classes.Contains(cls);
        if (!_forcedElements.Any(w => w.TryGetTarget(out var t) && ReferenceEquals(t, se))) _forcedElements.Add(new WeakReference<StyledElement>(se));
    }

    void RestoreForced(StyledElement? only)
    {
        foreach (var w in _forcedElements.ToList())
        {
            if (!w.TryGetTarget(out var se)) { _forcedElements.Remove(w); continue; }
            if (only is not null && !ReferenceEquals(only, se)) continue;
            if (_forced.TryGetValue(se, out var d))
            {
                foreach (var (cls, was) in d)
                {
                    if (cls.StartsWith(':')) ((IPseudoClasses)se.Classes).Set(cls, was);
                    else if (was && !se.Classes.Contains(cls)) se.Classes.Add(cls);
                    else if (!was) se.Classes.Remove(cls);
                }
                _forced.Remove(se);
            }
            _forcedElements.Remove(w);
        }
    }

    /// <summary>What <see cref="Reset"/> would undo for one node (null = everything), for changes/reset reporting.</summary>
    public IReadOnlyList<ChangeDto> PendingChanges(int? id)
    {
        var list = new List<ChangeDto>();
        foreach (var w in _modifiedObjects)
        {
            if (!w.TryGetTarget(out var o) || !_modified.TryGetValue(o, out var m)) continue;
            if (id is not null && !(registry.TryGetId(o, out var oid) && oid == id)) continue;
            foreach (var (prop, orig) in m)
                list.Add(new ChangeDto(registry.IdOf(o), o.GetType().Name, "property", prop.Name,
                    $"{prop.Name}: {codec.Encode(o.GetValue(prop)).Text} → back to {(orig.HadLocalValue || prop.IsDirect ? codec.Encode(orig.Value).Text : "(unset)")}"));
        }
        foreach (var w in _forcedElements)
        {
            if (!w.TryGetTarget(out var se) || !_forced.TryGetValue(se, out var d)) continue;
            if (id is not null && !(registry.TryGetId(se, out var sid) && sid == id)) continue;
            foreach (var (cls, was) in d)
                if (se.Classes.Contains(cls) != was)
                    list.Add(new ChangeDto(registry.IdOf(se), se.GetType().Name, cls.StartsWith(':') ? "pseudoClass" : "class", cls,
                        $"{cls}: {(was ? "re-added" : "removed")}"));
        }
        return list;
    }
}
