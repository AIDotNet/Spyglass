using System.Globalization;
using System.Text.RegularExpressions;
using Spyglass.App.Core.Localization;
using Spyglass.App.Core.Settings;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>UI language: resolution, string lookup and fallbacks, the string tables, and the Language setting (pure logic, no UI).</summary>
public partial class LocalizationTests
{
    static IReadOnlyDictionary<string, string> Table(Language l) => Localizer.LoadEmbedded(l);

    // ---- language resolution ----

    [Theory]
    [InlineData("English", Language.English)]
    [InlineData("ChineseSimplified", Language.ChineseSimplified)]
    [InlineData("ChineseTraditional", Language.ChineseTraditional)]
    [InlineData("chinesesimplified", Language.ChineseSimplified)]   // hand-edited case
    [InlineData("zh-CN", Language.ChineseSimplified)]
    [InlineData("zh-Hant", Language.ChineseTraditional)]
    [InlineData("zh_TW", Language.ChineseTraditional)]
    [InlineData("en-GB", Language.English)]
    [InlineData("Klingon", Language.English)]   // unknown values mean English
    public void Explicit_settings_resolve_regardless_of_the_system_culture(string setting, Language expected) =>
        Assert.Equal(expected, Languages.Resolve(setting, CultureInfo.GetCultureInfo("fr-FR")));

    [Theory]
    [InlineData("zh-CN", Language.ChineseSimplified)]
    [InlineData("zh-Hans-CN", Language.ChineseSimplified)]
    [InlineData("zh-SG", Language.ChineseSimplified)]
    [InlineData("zh-TW", Language.ChineseTraditional)]
    [InlineData("zh-HK", Language.ChineseTraditional)]
    [InlineData("zh-Hant-TW", Language.ChineseTraditional)]
    [InlineData("en-US", Language.English)]
    [InlineData("de-DE", Language.English)]   // no translation: English
    [InlineData("", Language.English)]
    public void System_follows_the_os_ui_culture(string culture, Language expected)
    {
        Assert.Equal(expected, Languages.Resolve("System", CultureInfo.GetCultureInfo(culture)));
        Assert.Equal(expected, Languages.Resolve(null, CultureInfo.GetCultureInfo(culture)));
        Assert.Equal(expected, Languages.Resolve("  ", CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void Normalize_keeps_known_values_and_replaces_unknown_ones_with_english()
    {
        Assert.Equal("System", Languages.Normalize(null));
        Assert.Equal("System", Languages.Normalize("system"));
        Assert.Equal("ChineseTraditional", Languages.Normalize("chinesetraditional"));
        Assert.Equal("ChineseSimplified", Languages.Normalize("zh-Hans"));
        Assert.Equal("English", Languages.Normalize("not a language"));
    }

    // ---- lookup, fallback, formatting ----

    static Localizer Fake(Dictionary<string, string> en, Dictionary<string, string> zh) =>
        new(l => l == Language.English ? en : l == Language.ChineseSimplified ? zh : new Dictionary<string, string>());

    [Fact]
    public void Lookup_falls_back_to_english_then_to_the_key()
    {
        var loc = Fake(new() { ["a"] = "Hello", ["b"] = "Only English" }, new() { ["a"] = "你好" });
        Assert.Equal("Hello", loc.Get("a"));
        loc.Language = Language.ChineseSimplified;
        Assert.Equal("你好", loc.Get("a"));
        Assert.Equal("Only English", loc.Get("b"));   // missing in zh: English
        Assert.Equal("nope.key", loc.Get("nope.key"));   // missing everywhere: the key itself
        loc.Language = Language.ChineseTraditional;   // empty table: everything falls back to English
        Assert.Equal("Hello", loc.Get("a"));
    }

    [Fact]
    public void Format_fills_placeholders_and_survives_broken_templates()
    {
        var loc = Fake(new() { ["n"] = "{0} of {1} files", ["bad"] = "{0} and {1}", ["lit"] = "use {file}" }, new() { ["n"] = "{1} 个文件中的 {0} 个" });
        Assert.Equal("2 of 5 files", loc.Format("n", 2, 5));
        loc.Language = Language.ChineseSimplified;
        Assert.Equal("5 个文件中的 2 个", loc.Format("n", 2, 5));
        Assert.Equal("{0} and {1}", loc.Format("bad", "only one argument"));   // FormatException: the template, not a crash
        Assert.Equal("use {file}", loc.Format("lit"));   // not a numbered placeholder
        Assert.Equal("missing.key", loc.Format("missing.key", 1));
    }

    [Fact]
    public void Changing_the_language_raises_Changed_once_per_change()
    {
        var loc = Fake(new(), new());
        var n = 0;
        loc.Changed += () => n++;
        loc.Language = Language.English;   // already English
        loc.Language = Language.ChineseSimplified;
        loc.Language = Language.ChineseSimplified;
        loc.Language = Language.ChineseTraditional;
        Assert.Equal(2, n);
    }

    // ---- the shipped tables ----

    [Fact]
    public void Every_key_exists_in_all_three_languages()
    {
        var en = Table(Language.English);
        Assert.True(en.Count > 100, "English table did not load");
        foreach (var lang in new[] { Language.ChineseSimplified, Language.ChineseTraditional })
        {
            var t = Table(lang);
            Assert.Empty(en.Keys.Except(t.Keys));   // untranslated
            Assert.Empty(t.Keys.Except(en.Keys));   // stale: no English counterpart
        }
    }

    [Fact]
    public void No_translation_is_empty_and_format_placeholders_match_english()
    {
        var en = Table(Language.English);
        foreach (var lang in new[] { Language.ChineseSimplified, Language.ChineseTraditional })
            foreach (var (key, text) in Table(lang))
            {
                Assert.False(string.IsNullOrWhiteSpace(text), $"{lang} {key} is empty");
                Assert.True(Placeholders(en[key]).SequenceEqual(Placeholders(text)), $"{lang} {key}: placeholders differ from English ({en[key]} / {text})");
            }
    }

    [Fact]
    public void Every_format_template_formats_with_its_own_arguments()
    {
        foreach (var lang in Enum.GetValues<Language>())
            foreach (var (key, text) in Table(lang))
            {
                var n = Placeholders(text).DefaultIfEmpty(-1).Max() + 1;
                if (n == 0) continue;
                var args = Enumerable.Range(0, n).Select(i => (object)(i + 1)).ToArray();
                // {0:HH:mm:ss} needs a date, {0:N0} a number
                if (text.Contains("HH:mm")) args = [DateTime.Now];
                var s = string.Format(Languages.Culture(lang), text, args);
                Assert.DoesNotContain("{0}", s);
            }
    }

    [Fact]
    public void Chinese_tables_actually_contain_chinese()
    {
        foreach (var lang in new[] { Language.ChineseSimplified, Language.ChineseTraditional })
        {
            var t = Table(lang);
            Assert.True(t.Values.Count(v => HanRegex().IsMatch(v)) > t.Count / 2, $"{lang}: most strings should be translated");
        }
        Assert.Equal("设置", Table(Language.ChineseSimplified)["MainWindow.Settings"]);
        Assert.Equal("設定", Table(Language.ChineseTraditional)["MainWindow.Settings"]);
        Assert.Equal("Settings", Table(Language.English)["MainWindow.Settings"]);
    }

    /// <summary>Every key the code asks for (Loc.T/Loc.F, {loc:T Key}, Net.* and Perf.* keys passed around as data) exists in English.</summary>
    [Fact]
    public void Keys_used_in_source_exist()
    {
        var en = Table(Language.English);
        var root = RepoRoot();
        var used = new HashSet<(string Key, string File)>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".axaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in CodeKeyRegex().Matches(text)) used.Add((m.Groups[1].Value, file));
            foreach (Match m in XamlKeyRegex().Matches(text)) used.Add((m.Groups[1].Value, file));
            foreach (Match m in DataKeyRegex().Matches(text)) used.Add((m.Groups[1].Value, file));
        }
        Assert.True(used.Count > 100, "found too few keys: the scan is broken");
        var missing = used.Where(u => !en.ContainsKey(u.Key)).Select(u => $"{u.Key} ({Path.GetFileName(u.File)})").ToList();
        Assert.Empty(missing);
    }

    // ---- the setting ----

    [Fact]
    public void Language_setting_defaults_to_system_round_trips_and_is_normalised()
    {
        var dir = Directory.CreateTempSubdirectory("spyglass-lang-").FullName;
        try
        {
            var store = new AppSettingsStore(Path.Combine(dir, "settings.json"));
            Assert.Equal("System", store.Load().Language);
            Assert.True(store.Save(new AppSettings { Language = "ChineseTraditional" }));
            Assert.Equal("ChineseTraditional", store.Load().Language);
            Assert.Contains("\"Language\": \"ChineseTraditional\"", File.ReadAllText(store.FilePath));   // readable, hand-editable

            File.WriteAllText(store.FilePath, "{ \"Language\": \"Klingon\", \"Port\": 12345 }");
            var s = store.Load();
            Assert.Equal("English", s.Language);   // unknown: English
            Assert.Equal(12345, s.Port);   // the rest of the file survives

            File.WriteAllText(store.FilePath, "{ \"Language\": null }");
            Assert.Equal("System", store.Load().Language);
            File.WriteAllText(store.FilePath, "{ \"Language\": \"zh-TW\" }");
            Assert.Equal("ChineseTraditional", store.Load().Language);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Updating_the_language_notifies_and_reset_restores_system()
    {
        var dir = Directory.CreateTempSubdirectory("spyglass-lang-").FullName;
        try
        {
            var service = new SettingsService(new AppSettingsStore(Path.Combine(dir, "settings.json")));
            string? seen = null;
            service.Changed += s => seen = s.Language;
            service.Update(s => s.Language = "ChineseSimplified");
            Assert.Equal("ChineseSimplified", seen);
            service.Reset();
            Assert.Equal("System", service.Current.Language);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- helpers ----

    static List<int> Placeholders(string text) =>
        NumberedPlaceholderRegex().Matches(text).Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToList();

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Spyglass.slnx"))) return d.FullName;
        throw new InvalidOperationException("Spyglass.slnx not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"\{(\d+)(?::[^}]*)?\}")] private static partial Regex NumberedPlaceholderRegex();
    [GeneratedRegex(@"[一-鿿]")] private static partial Regex HanRegex();
    [GeneratedRegex(@"Loc\.[TF]\(\s*""([A-Za-z0-9_.]+)""")] private static partial Regex CodeKeyRegex();
    [GeneratedRegex(@"\{loc:T\s+([A-Za-z0-9_.]+)\}")] private static partial Regex XamlKeyRegex();
    [GeneratedRegex(@"""((?:Net|Perf)\.[A-Za-z0-9_.]+)""")] private static partial Regex DataKeyRegex();
}
