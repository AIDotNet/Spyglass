using Spyglass.App.Core.Localization;
using Spyglass.App.Core.Palette;
using Spyglass.App.Core.Settings;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>Command palette catalog (wireframe W6): what is offered, shortcut hints, languages.</summary>
public class PaletteTests
{
    static readonly NamedElement[] Two = [new(7, "Button", "SaveButton"), new(9, "TextBox", "NameBox")];

    static Localizer English() => new() { Language = Language.English };

    static IReadOnlyList<PaletteEntry> Build(bool session, IEnumerable<NamedElement>? els = null, ShortcutMap? map = null, bool mac = false, Localizer? loc = null) =>
        PaletteCatalog.Build(session, els ?? Two, map ?? new ShortcutMap(null, mac), mac, loc ?? English());

    [Fact]
    public void Without_an_app_only_pages_that_need_none_and_settings_are_offered()
    {
        var all = Build(session: false);
        Assert.Equal(["nav:connections", "nav:settings"], all.Where(e => e.Id.StartsWith("nav:")).Select(e => e.Id));
        Assert.DoesNotContain(all, e => e.Id.StartsWith("act:") || e.Id.StartsWith("el:"));
        Assert.Equal(3, all.Count(e => e.Id.StartsWith("set:theme:")));
        Assert.Equal(4, all.Count(e => e.Id.StartsWith("set:lang:")));
    }

    [Fact]
    public void With_an_app_everything_is_offered_and_ids_are_unique()
    {
        var all = Build(session: true);
        Assert.Equal(9, all.Count(e => e.Id.StartsWith("nav:")));
        Assert.Equal(6, all.Count(e => e.Id.StartsWith("act:")));
        Assert.Equal(["el:7", "el:9"], all.Where(e => e.Id.StartsWith("el:")).Select(e => e.Id));
        Assert.Equal("Button #SaveButton", all.Single(e => e.Id == "el:7").Label);
        Assert.Equal(all.Count, all.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void Element_list_is_capped()
    {
        var many = Enumerable.Range(1, PaletteCatalog.MaxElements + 50).Select(i => new NamedElement(i, "Border", "B" + i));
        Assert.Equal(PaletteCatalog.MaxElements, Build(true, many).Count(e => e.Id.StartsWith("el:")));
    }

    [Fact]
    public void Shortcut_hints_follow_the_users_bindings_and_the_platform()
    {
        Assert.Equal("Ctrl 3", Build(true).Single(e => e.Id == "nav:network").Shortcut);
        Assert.Equal("⌘ 3", Build(true, mac: true).Single(e => e.Id == "nav:network").Shortcut);

        var custom = new ShortcutMap(new Dictionary<string, string> { [ShortcutRegistry.NavId("network")] = "Ctrl+Shift+N", [ShortcutRegistry.NavId("logs")] = "None" }, mac: false);
        var all = Build(true, map: custom);
        Assert.Equal("Ctrl Shift N", all.Single(e => e.Id == "nav:network").Shortcut);
        Assert.Null(all.Single(e => e.Id == "nav:logs").Shortcut);              // unbound: no hint
        Assert.Equal("Ctrl E", all.Single(e => e.Id == PaletteCatalog.RecordNetwork).Shortcut);
    }

    [Fact]
    public void Default_shortcuts_do_not_conflict_with_each_other()
    {
        var map = new ShortcutMap(null, mac: false);
        foreach (var a in ShortcutRegistry.Actions) Assert.True(map.ConflictsOf(a.Id).Count == 0, $"{a.Id} conflicts with {string.Join(", ", map.ConflictsOf(a.Id))}");
        Assert.Equal("Ctrl I", map.Get(ShortcutRegistry.PaletteOpen)!.Value.ToDisplay(false));
    }

    [Theory]
    [InlineData(Language.English)]
    [InlineData(Language.ChineseSimplified)]
    [InlineData(Language.ChineseTraditional)]
    public void Every_label_and_group_is_translated_in_every_language(Language language)
    {
        var loc = new Localizer { Language = language };
        foreach (var e in Build(true, loc: loc).Where(e => !e.Id.StartsWith("el:")))
        {
            Assert.DoesNotContain("Palette.", e.Label);   // a missing key would show the key itself
            Assert.DoesNotContain("Palette.", e.Group);
        }
        foreach (var key in new[] { "Palette.Placeholder", "Palette.Label", "Palette.EmptyTitle", "Palette.EmptyDescription", "Palette.Open", "Palette.Close", "Palette.Results" })
            Assert.True(loc.TryGet(key, out var text) && text != key, key);
        foreach (var a in ShortcutRegistry.Actions) Assert.True(loc.TryGet(a.TitleKey, out _) && loc.TryGet(a.AreaKey, out _), a.Id);
    }

    [Fact]
    public void Chinese_labels_differ_from_english()
    {
        var zh = Build(true, loc: new Localizer { Language = Language.ChineseSimplified });
        Assert.Equal("前往设置", zh.Single(e => e.Id == "nav:settings").Label);
        Assert.Equal("导航", zh.Single(e => e.Id == "nav:settings").Group);
    }
}
