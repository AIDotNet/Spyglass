using System.Globalization;

namespace Spyglass.App.Core.Localization;

/// <summary>A UI language the client ships translations for.</summary>
public enum Language { English, ChineseSimplified, ChineseTraditional }

/// <summary>Maps the persisted <c>Language</c> setting (a hand-editable string) to a <see cref="Language"/>.</summary>
public static class Languages
{
    /// <summary>"System" follows the OS UI culture.</summary>
    public const string System = "System";

    /// <summary>The names the setting can hold (what the Settings page writes).</summary>
    public static readonly IReadOnlyList<string> SettingValues = [System, nameof(Language.English), nameof(Language.ChineseSimplified), nameof(Language.ChineseTraditional)];

    /// <summary>Canonical setting value: empty becomes "System", known names and culture tags (en, zh-CN, zh-Hant, …) are normalised, anything else becomes English.</summary>
    public static string Normalize(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting)) return System;
        var s = setting.Trim();
        foreach (var name in SettingValues)
            if (s.Equals(name, StringComparison.OrdinalIgnoreCase)) return name;
        return FromCultureName(s) is { } l ? l.ToString() : nameof(Language.English);
    }

    /// <summary>The language to show for a setting value; "System" looks at <paramref name="systemCulture"/> (default: the OS UI culture).</summary>
    public static Language Resolve(string? setting, CultureInfo? systemCulture = null)
    {
        var n = Normalize(setting);
        if (n == System) return FromCultureName((systemCulture ?? CultureInfo.CurrentUICulture).Name) ?? Language.English;
        return Enum.Parse<Language>(n);
    }

    /// <summary>zh-Hans / zh-CN / zh-SG → Simplified; zh-Hant / zh-TW / zh-HK / zh-MO → Traditional; en* → English; other cultures → null.</summary>
    public static Language? FromCultureName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim().Replace('_', '-').ToLowerInvariant();
        if (n == "en" || n.StartsWith("en-")) return Language.English;
        if (n != "zh" && !n.StartsWith("zh-")) return null;
        if (n.Contains("hant") || n.EndsWith("-tw") || n.EndsWith("-hk") || n.EndsWith("-mo")) return Language.ChineseTraditional;
        return Language.ChineseSimplified;
    }

    public static CultureInfo Culture(Language l) => CultureInfo.GetCultureInfo(l switch
    {
        Language.ChineseSimplified => "zh-Hans",
        Language.ChineseTraditional => "zh-Hant",
        _ => "en-US",
    });

    /// <summary>Resource file name stem of a language's string table.</summary>
    public static string FileStem(Language l) => l switch { Language.ChineseSimplified => "zh-Hans", Language.ChineseTraditional => "zh-Hant", _ => "en" };
}
