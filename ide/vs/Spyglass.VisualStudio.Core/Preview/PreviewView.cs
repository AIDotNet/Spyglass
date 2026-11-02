using System.Globalization;
using Avalonia.Remote.Protocol.Input;

namespace Spyglass.VisualStudio.Core.Preview;

/// <summary>
/// How the preview picture is scaled in the pane: fit the pane (shrinking, never enlarging past 100%) or a fixed percentage.
/// All sizes below are device-independent pixels (1/96 inch) of the pane, not device pixels.
/// </summary>
public readonly record struct PreviewZoom(bool Fit, double Percent)
{
    public static readonly PreviewZoom FitToPane = new(true, 100);
    public static readonly PreviewZoom Actual = new(false, 100);
    public static readonly double[] Steps = [25, 50, 75, 100, 125, 150, 200, 300, 400];

    /// <summary>The scale factor for a picture of the given logical size in a pane of the given size.</summary>
    public double Scale(double frameWidth, double frameHeight, double paneWidth, double paneHeight)
    {
        if (!Fit) return Percent / 100;
        if (frameWidth <= 0 || frameHeight <= 0 || paneWidth <= 0 || paneHeight <= 0) return 1;
        return Math.Clamp(Math.Min(paneWidth / frameWidth, paneHeight / frameHeight), 0.1, 1);
    }

    /// <summary>The next larger step above <paramref name="currentScale"/> (the current look, also when it came from Fit).</summary>
    public static PreviewZoom In(double currentScale)
    {
        var pct = currentScale * 100;
        foreach (var s in Steps) if (s > pct + 0.5) return new(false, s);
        return new(false, Steps[^1]);
    }

    public static PreviewZoom Out(double currentScale)
    {
        var pct = currentScale * 100;
        for (var i = Steps.Length - 1; i >= 0; i--) if (Steps[i] < pct - 0.5) return new(false, Steps[i]);
        return new(false, Steps[0]);
    }

    public static PreviewZoom Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return FitToPane;
        var t = text.Trim().TrimEnd('%');
        if (t.Equals("fit", StringComparison.OrdinalIgnoreCase)) return FitToPane;
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p is >= 10 and <= 800
            ? new PreviewZoom(false, p) : FitToPane;
    }

    public override string ToString() => Fit ? "fit" : Percent.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    /// <summary>The viewport to tell the previewer (logical size): the pane, undone by the zoom (Fit asks for the whole pane).</summary>
    public (double Width, double Height) Viewport(double paneWidth, double paneHeight)
    {
        var scale = Fit ? 1 : Percent / 100;
        return (Math.Max(1, Math.Floor(paneWidth / scale)), Math.Max(1, Math.Floor(paneHeight / scale)));
    }
}

/// <summary>Pane → previewer translation of pointer, keyboard and modifier input.</summary>
public static class PreviewInput
{
    /// <summary>A position inside the (scaled) picture, in DIPs from its top-left, → the previewer's logical position.</summary>
    public static (double X, double Y) ToLogical(double pictureX, double pictureY, double scale)
    {
        if (scale <= 0) scale = 1;
        return (pictureX / scale, pictureY / scale);
    }

    /// <summary>The DPI to render at for a WPF DPI scale factor (1.0 = 96, 1.5 = 144…).</summary>
    public static double Dpi(double dpiScale) => 96 * Math.Clamp(dpiScale <= 0 ? 1 : dpiScale, 0.5, 4);

    static readonly Dictionary<string, Key> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Next"] = Key.PageDown,
        ["Prior"] = Key.PageUp,
        ["Capital"] = Key.CapsLock,
        ["Return"] = Key.Return,
    };

    /// <summary>Maps a WPF <c>System.Windows.Input.Key</c> name (WPF and Avalonia use the same names) to the protocol's key.</summary>
    public static Key? MapKey(string? wpfKeyName)
    {
        if (string.IsNullOrEmpty(wpfKeyName) || !char.IsLetter(wpfKeyName[0])) return null;   // Enum.TryParse would accept "123"
        if (Aliases.TryGetValue(wpfKeyName, out var alias)) return alias;
        return Enum.TryParse<Key>(wpfKeyName, ignoreCase: false, out var k) && Enum.IsDefined(k) && k != Key.None ? k : null;
    }

    /// <summary>The modifier array the previewer expects, or null when none are held.</summary>
    public static InputModifiers[]? Modifiers(bool control, bool shift, bool alt, bool left = false, bool right = false, bool middle = false)
    {
        var list = new List<InputModifiers>(4);
        if (alt) list.Add(InputModifiers.Alt);
        if (control) list.Add(InputModifiers.Control);
        if (shift) list.Add(InputModifiers.Shift);
        if (left) list.Add(InputModifiers.LeftMouseButton);
        if (right) list.Add(InputModifiers.RightMouseButton);
        if (middle) list.Add(InputModifiers.MiddleMouseButton);
        return list.Count == 0 ? null : list.ToArray();
    }

    /// <summary>WPF wheel delta (120 per notch, up positive) → the previewer's scroll delta (notches, up positive).</summary>
    public static double WheelNotches(int wpfDelta) => wpfDelta / 120.0;
}
