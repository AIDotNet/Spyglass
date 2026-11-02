using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Agent.Internal;

/// <summary>Converts property values to/from <see cref="ValueDto"/> (design/protocol §5.3).</summary>
internal sealed class ValueCodec(NodeRegistry registry)
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public ValueDto Encode(object? value)
    {
        string? type = value?.GetType().FullName;
        switch (value)
        {
            case null:
                return new ValueDto(ValueKind.Null, "(null)");
            case var _ when ReferenceEquals(value, AvaloniaProperty.UnsetValue):
                return new ValueDto(ValueKind.Unset, "(unset)");
            case bool b:
                return new ValueDto(ValueKind.Bool, b ? "True" : "False", SpyglassJson.ToElement(b), type);
            case string s:
                return new ValueDto(ValueKind.String, s, SpyglassJson.ToElement(s), type);
            case Enum e:
                var et = e.GetType();
                var flags = et.IsDefined(typeof(FlagsAttribute), false);
                return new ValueDto(ValueKind.Enum, e.ToString(),
                    SpyglassJson.ToElement(new EnumData(e.ToString(), Enum.GetNames(et), flags)), type);
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                var d = Convert.ToDouble(value, Inv);
                // JSON has no Infinity/NaN (e.g. MaxWidth = ∞): keep them in the text only.
                return double.IsFinite(d)
                    ? new ValueDto(ValueKind.Number, Convert.ToString(value, Inv)!, SpyglassJson.ToElement(d), type)
                    : new ValueDto(ValueKind.Number, double.IsNaN(d) ? "NaN" : d > 0 ? "Infinity" : "-Infinity", null, type);
            case Color c:
                return new ValueDto(ValueKind.Color, Hex(c), SpyglassJson.ToElement(ToData(c)), type);
            case ISolidColorBrush sb:
                return new ValueDto(ValueKind.Brush, Hex(sb.Color), SpyglassJson.ToElement(new BrushData(ToData(sb.Color))), type);
            case IBrush br:
                return new ValueDto(ValueKind.Brush, br.GetType().Name, SpyglassJson.ToElement(new BrushData(null)), type);
            case Thickness t:
                return new ValueDto(ValueKind.Thickness, Fmt(t.Left, t.Top, t.Right, t.Bottom),
                    SpyglassJson.ToElement(new ThicknessData(t.Left, t.Top, t.Right, t.Bottom)), type);
            case CornerRadius cr:
                return new ValueDto(ValueKind.CornerRadius, Fmt(cr.TopLeft, cr.TopRight, cr.BottomRight, cr.BottomLeft),
                    SpyglassJson.ToElement(new CornerRadiusData(cr.TopLeft, cr.TopRight, cr.BottomRight, cr.BottomLeft)), type);
            case Size sz:
                return new ValueDto(ValueKind.Size, Fmt(sz.Width, sz.Height), Finite(sz.Width, sz.Height) ? SpyglassJson.ToElement(new SizeData(sz.Width, sz.Height)) : null, type);
            case Point p:
                return new ValueDto(ValueKind.Point, Fmt(p.X, p.Y), Finite(p.X, p.Y) ? SpyglassJson.ToElement(new PointData(p.X, p.Y)) : null, type);
            case Rect r:
                return new ValueDto(ValueKind.Rect, Fmt(r.X, r.Y, r.Width, r.Height),
                    Finite(r.X, r.Y, r.Width, r.Height) ? SpyglassJson.ToElement(new RectData(r.X, r.Y, r.Width, r.Height)) : null, type);
            case ICollection col:
                return new ValueDto(ValueKind.Collection, $"{value.GetType().Name} (Count = {col.Count})",
                    SpyglassJson.ToElement(new RefData(registry.IdOf(value), value.GetType().Name, col.Count)), type);
            default:
                return new ValueDto(ValueKind.Object, Summary(value),
                    SpyglassJson.ToElement(new RefData(registry.IdOf(value), value.GetType().Name)), type);
        }
    }

    /// <summary>Decodes a client value for a property of type <paramref name="target"/>.</summary>
    public object? Decode(ValueDto dto, Type target)
    {
        if (dto.Kind == ValueKind.Null) return null;
        if (dto.Kind == ValueKind.Unset) return AvaloniaProperty.UnsetValue;
        var t = Nullable.GetUnderlyingType(target) ?? target;
        var text = dto.Text.Trim();
        try
        {
            if (t == typeof(string)) return dto.Text;
            if (t == typeof(bool)) return bool.Parse(text);
            if (t.IsEnum) return Enum.Parse(t, text, ignoreCase: true);
            if (t == typeof(double)) return Num(dto, text);
            if (t == typeof(float)) return (float)Num(dto, text);
            if (t == typeof(int)) return (int)Num(dto, text);
            if (t == typeof(long)) return (long)Num(dto, text);
            if (t == typeof(Thickness)) return Thickness.Parse(text);
            if (t == typeof(CornerRadius)) return CornerRadius.Parse(text);
            if (t == typeof(Color)) return Color.Parse(text);
            if (typeof(IBrush).IsAssignableFrom(t)) return Brush.Parse(text);
            if (t == typeof(Size)) return Size.Parse(text);
            if (t == typeof(Point)) return Point.Parse(text);
            if (t == typeof(Rect)) return Rect.Parse(text);
            if (t == typeof(object)) return dto.Text;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            throw new SpyglassProtocolException(ErrorCodes.ConversionFailed, $"'{dto.Text}' is not a valid {t.Name}: {ex.Message}");
        }
        throw new SpyglassProtocolException(ErrorCodes.ConversionFailed, $"Editing values of type {t.Name} is not supported yet.");
    }

    static double Num(ValueDto dto, string text) =>
        dto.Data is { ValueKind: System.Text.Json.JsonValueKind.Number } n ? n.GetDouble()
        : text is "∞" or "Infinity" ? double.PositiveInfinity
        : text is "Auto" or "NaN" ? double.NaN
        : double.Parse(text, Inv);

    static ColorData ToData(Color c) => new(c.A, c.R, c.G, c.B);

    /// <summary>Always #aarrggbb: Color.ToString() returns names ("Black") for known colours, which reads inconsistently.</summary>
    static string Hex(Color c) => $"#{c.ToUInt32():x8}";

    static bool Finite(params double[] v) => v.All(double.IsFinite);

    static string Fmt(params double[] v) => string.Join(",", v.Select(x => x.ToString(Inv)));

    static string Summary(object value)
    {
        var s = value.ToString();
        var name = value.GetType().Name;
        return string.IsNullOrEmpty(s) || s == value.GetType().FullName ? "{" + name + "}" : s!.Length > 80 ? s.Substring(0, 79) + "…" : s;
    }
}
