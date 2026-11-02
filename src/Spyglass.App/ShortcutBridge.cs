using Avalonia.Input;
using Spyglass.App.Core.Settings;
using TKCode.Arc;

namespace Spyglass.App;

/// <summary>Converts between the Avalonia/Arc key types and the pure <see cref="Gesture"/> of App.Core.</summary>
public static class ShortcutBridge
{
    /// <summary>The shortcuts in effect right now (re-read on every key press, so Settings changes apply live).</summary>
    public static ShortcutMap Current => ShortcutMap.From(SettingsService.Shared.Current);

    public static Gesture? FromEvent(KeyEventArgs e) => ShortcutFormatter.FromKeyEvent(e) is { } g ? FromKeyGesture(g) : null;

    public static Gesture? FromKeyGesture(KeyGesture g)
    {
        var id = IdOf(g.Key);
        if (id is null) return null;
        var m = g.KeyModifiers;
        var text = (m.HasFlag(KeyModifiers.Control) ? "Ctrl+" : "") + (m.HasFlag(KeyModifiers.Alt) ? "Alt+" : "")
                 + (m.HasFlag(KeyModifiers.Shift) ? "Shift+" : "") + (m.HasFlag(KeyModifiers.Meta) ? "Cmd+" : "") + id;
        return Gesture.TryParse(text, OperatingSystem.IsMacOS(), out var r) ? r : null;
    }

    static string? IdOf(Key k) => k switch
    {
        >= Key.A and <= Key.Z => k.ToString(),
        >= Key.D0 and <= Key.D9 => ((int)k - (int)Key.D0).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((int)k - (int)Key.NumPad0).ToString(),
        >= Key.F1 and <= Key.F24 => k.ToString(),
        Key.Space => "Space", Key.Enter => "Enter", Key.Escape => "Escape", Key.Back => "Backspace", Key.Delete => "Delete", Key.Tab => "Tab",
        Key.Up => "Up", Key.Down => "Down", Key.Left => "Left", Key.Right => "Right",
        Key.Home => "Home", Key.End => "End", Key.PageUp => "PageUp", Key.PageDown => "PageDown",
        Key.OemComma => ",", Key.OemPeriod => ".", Key.OemQuestion => "/", Key.OemSemicolon => ";", Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]", Key.OemPipe => "\\", Key.OemMinus => "-", Key.OemPlus => "=", Key.OemTilde => "`",
        _ => null,
    };

    public static KeyGesture? ToKeyGesture(Gesture? g) =>
        g is { } x && ShortcutFormatter.TryParse(x.ToString(), ShortcutFormatter.CurrentPlatform, out var k) ? k : null;
}
