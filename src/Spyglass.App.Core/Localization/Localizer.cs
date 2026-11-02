using System.Text.Json;

namespace Spyglass.App.Core.Localization;

/// <summary>
/// String lookup for the client UI. Tables are flat key → text JSON files (Strings/*.json, embedded).
/// Fallback chain: selected language → English → the key itself. <c>{0}</c> placeholders use string.Format.
/// </summary>
public sealed class Localizer
{
    readonly Func<Language, IReadOnlyDictionary<string, string>> _load;
    readonly Dictionary<Language, IReadOnlyDictionary<string, string>> _tables = new();
    Language _language = Language.English;

    public Localizer(Func<Language, IReadOnlyDictionary<string, string>>? loader = null) => _load = loader ?? LoadEmbedded;

    /// <summary>The process-wide instance (the GUI's <c>Loc</c> wraps it).</summary>
    public static Localizer Shared { get; } = new();

    public Language Language
    {
        get => _language;
        set { if (_language == value) return; _language = value; Changed?.Invoke(); }
    }

    public event Action? Changed;

    IReadOnlyDictionary<string, string> Table(Language l)
    {
        lock (_tables) return _tables.TryGetValue(l, out var t) ? t : _tables[l] = _load(l);
    }

    public bool TryGet(string key, out string text)
    {
        if (Table(_language).TryGetValue(key, out text!)) return true;
        return Table(Language.English).TryGetValue(key, out text!);
    }

    public string Get(string key) => TryGet(key, out var t) ? t : key;

    public string Format(string key, params object?[] args)
    {
        var template = Get(key);
        try { return string.Format(Languages.Culture(_language), template, args); }
        catch (FormatException) { return template; }
    }

    /// <summary>Reads every key of one language (tests compare the tables).</summary>
    public static IReadOnlyDictionary<string, string> LoadEmbedded(Language language)
    {
        var asm = typeof(Localizer).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith($".Strings.{Languages.FileStem(language)}.json", StringComparison.Ordinal));
        if (name is null) return new Dictionary<string, string>();
        using var stream = asm.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream, new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip }) ?? new();
    }
}
