using Spyglass.App.Core.Settings;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>Client keyboard shortcuts: gesture parsing, defaults, overrides in the settings file, conflicts, reset (pure logic).</summary>
public class ShortcutTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("spyglass-shortcuts-").FullName;
    string FilePath => Path.Combine(_dir, "settings.json");
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("ctrl+e", false, "Ctrl+E")]
    [InlineData("Mod+Shift+k", false, "Ctrl+Shift+K")]
    [InlineData("Mod+Shift+k", true, "Shift+Cmd+K")]
    [InlineData("command+alt+i", true, "Alt+Cmd+I")]
    [InlineData("esc", false, "Escape")]
    [InlineData("f5", false, "F5")]
    [InlineData("Ctrl+/", false, "Ctrl+/")]
    public void Parses_and_canonicalizes(string text, bool mac, string expected)
    {
        Assert.True(Gesture.TryParse(text, mac, out var g));
        Assert.Equal(expected, g.ToString());
        Assert.True(Gesture.TryParse(g.ToString(), mac, out var again));
        Assert.Equal(g, again);
    }

    [Theory]
    [InlineData("")] [InlineData("Ctrl")] [InlineData("Ctrl+")] [InlineData("Ctrl+E+F")] [InlineData("Banana")] [InlineData("f99")] [InlineData("Ctrl+Hyper")]
    public void Rejects_garbage(string text) => Assert.False(Gesture.TryParse(text, false, out _));

    [Fact]
    public void Defaults_follow_the_platform_and_have_no_conflicts()
    {
        var win = new ShortcutMap(null, mac: false);
        var mac = new ShortcutMap(null, mac: true);
        Assert.Equal("Ctrl+E", win.Get(ShortcutRegistry.NetworkToggleRecord)!.Value.ToString());
        Assert.Equal("Cmd+E", mac.Get(ShortcutRegistry.NetworkToggleRecord)!.Value.ToString());
        Assert.Equal("⌘ E", mac.Get(ShortcutRegistry.NetworkToggleRecord)!.Value.ToDisplay(true));
        Assert.Equal("Ctrl F", win.Get(ShortcutRegistry.NetworkSearch)!.Value.ToDisplay(false));
        foreach (var a in ShortcutRegistry.Actions)
        {
            Assert.False(win.IsCustom(a.Id));
            Assert.Empty(win.ConflictsOf(a.Id));
        }
    }

    [Fact]
    public void Overrides_apply_and_unknown_or_unparsable_entries_are_ignored()
    {
        var map = new ShortcutMap(new Dictionary<string, string>
        {
            [ShortcutRegistry.NetworkSearch] = "ctrl+shift+f",
            [ShortcutRegistry.NetworkToggleRecord] = "not a key",   // falls back to the default
            ["no.such.action"] = "Ctrl+Q",
        }, mac: false);
        Assert.Equal("Ctrl+Shift+F", map.Get(ShortcutRegistry.NetworkSearch)!.Value.ToString());
        Assert.True(map.IsCustom(ShortcutRegistry.NetworkSearch));
        Assert.Equal("Ctrl+E", map.Get(ShortcutRegistry.NetworkToggleRecord)!.Value.ToString());
        Assert.Equal(ShortcutRegistry.NetworkSearch, map.Find(map.Get(ShortcutRegistry.NetworkSearch)!.Value)!.Id);
    }

    [Fact]
    public void Conflicts_are_detected_on_both_sides_and_the_first_action_wins()
    {
        var map = new ShortcutMap(new Dictionary<string, string> { [ShortcutRegistry.NetworkSearch] = "Ctrl+E" }, mac: false);
        Assert.Single(map.ConflictsOf(ShortcutRegistry.NetworkSearch));
        Assert.Single(map.ConflictsOf(ShortcutRegistry.NetworkToggleRecord));
        Assert.Equal(ShortcutRegistry.NetworkToggleRecord, map.Find(map.Get(ShortcutRegistry.NetworkSearch)!.Value)!.Id);
    }

    [Fact]
    public void Unbound_is_stored_as_None_and_never_conflicts()
    {
        var o = ShortcutMap.With(null, ShortcutRegistry.NetworkSearch, null, mac: false);
        Assert.Equal("None", o[ShortcutRegistry.NetworkSearch]);
        var map = new ShortcutMap(o, mac: false);
        Assert.Null(map.Get(ShortcutRegistry.NetworkSearch));
        Assert.Empty(map.ConflictsOf(ShortcutRegistry.NetworkSearch));
    }

    [Fact]
    public void Setting_the_default_removes_the_override()
    {
        var o = ShortcutMap.With(null, ShortcutRegistry.NetworkSearch, new Gesture(true, false, true, false, "F"), mac: false);
        Assert.Single(o);
        var back = ShortcutMap.With(o, ShortcutRegistry.NetworkSearch, new Gesture(true, false, false, false, "F"), mac: false);
        Assert.Empty(back);
    }

    [Fact]
    public void Overrides_persist_in_the_settings_file_and_garbage_does_not_break_loading()
    {
        var store = new AppSettingsStore(FilePath);
        var svc = new SettingsService(store);
        svc.Update(s => s.Shortcuts = ShortcutMap.With(s.Shortcuts, ShortcutRegistry.NetworkSearch, new Gesture(true, false, true, false, "F")));
        Assert.True(File.ReadAllText(FilePath).Contains("Ctrl+Shift+F"), File.ReadAllText(FilePath));
        Assert.Equal("Ctrl+Shift+F", new AppSettingsStore(FilePath).Load().Shortcuts[ShortcutRegistry.NetworkSearch]);

        File.WriteAllText(FilePath, """{ "Accent": "Violet", "Shortcuts": { "network.search": 5, "bogus": "Ctrl+Z", "network.close": "???", "network.next": "j" } }""");
        var s = new AppSettingsStore(FilePath).Load();
        Assert.Equal("Violet", s.Accent);
        Assert.Equal(new Dictionary<string, string> { ["network.next"] = "J" }, s.Shortcuts);

        File.WriteAllText(FilePath, """{ "Accent": "Violet", "Shortcuts": [1, 2] }""");
        var t = new AppSettingsStore(FilePath).Load();
        Assert.Equal("Violet", t.Accent);
        Assert.Empty(t.Shortcuts);
    }

    [Fact]
    public void Reset_clears_shortcut_overrides_and_notifies()
    {
        var svc = new SettingsService(new AppSettingsStore(FilePath));
        svc.Update(s => s.Shortcuts = ShortcutMap.With(s.Shortcuts, ShortcutRegistry.NetworkNext, new Gesture(false, false, false, false, "J")));
        Assert.NotEmpty(svc.Current.Shortcuts);
        var raised = 0;
        svc.Changed += _ => raised++;
        svc.Reset();
        Assert.Empty(svc.Current.Shortcuts);
        Assert.Equal(1, raised);
    }
}
