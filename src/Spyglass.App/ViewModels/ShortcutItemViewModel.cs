using Avalonia.Input;
using Spyglass.App.Core.Settings;

namespace Spyglass.App.ViewModels;

/// <summary>One editable row of the Settings › Keyboard shortcuts list. The gesture lives in <see cref="AppSettings.Shortcuts"/>; this is a view of it.</summary>
public sealed class ShortcutItemViewModel(ShortcutAction action, SettingsService service) : ObservableObject
{
    ShortcutMap Map => ShortcutMap.From(service.Current);

    public string Id => action.Id;
    public string Area => action.Area;
    public string Title => action.Title;

    /// <summary>Two-way with <c>arc:ShortcutRecorder.Value</c>; null = no shortcut.</summary>
    public KeyGesture? Value
    {
        get => ShortcutBridge.ToKeyGesture(Map.Get(action.Id));
        set
        {
            var g = value is null ? null : ShortcutBridge.FromKeyGesture(value);
            if (value is not null && g is null) return;   // a key Spyglass cannot store
            if (g == Map.Get(action.Id)) return;
            service.Update(s => s.Shortcuts = ShortcutMap.With(s.Shortcuts, action.Id, g));
        }
    }

    /// <summary>What the recorder's reset button restores.</summary>
    public KeyGesture? DefaultValue => ShortcutBridge.ToKeyGesture(Map.Default(action.Id));

    public bool HasConflict => Map.ConflictsOf(action.Id).Count > 0;
    public string ConflictText => HasConflict ? "Also used by: " + string.Join(", ", Map.ConflictsOf(action.Id)) + ". Only the first one runs." : "";

    public void Refresh() => Raise(string.Empty);
}
