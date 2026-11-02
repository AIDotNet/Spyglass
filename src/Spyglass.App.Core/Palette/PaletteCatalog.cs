using Spyglass.App.Core.Localization;
using Spyglass.App.Core.Settings;

namespace Spyglass.App.Core.Palette;

/// <summary>One row of the command palette. <see cref="Id"/> is stable ("nav:network", "act:pick", "set:lang:English", "el:42").</summary>
public sealed record PaletteEntry(string Id, string Group, string Label, string? Description, string? Shortcut, string Keywords);

/// <summary>A named element of the connected app's tree (the palette jumps to it).</summary>
public sealed record NamedElement(int Id, string Type, string Name);

/// <summary>
/// The commands the palette offers (wireframe W6): navigation, actions on the connected app, settings, and elements with a
/// Name. Pure: texts come from the string tables, shortcut hints from the user's effective shortcuts; running an entry is the GUI's job.
/// </summary>
public static class PaletteCatalog
{
    public const int MaxElements = 400;

    public const string Navigation = "Palette.Group.Navigation", Actions = "Palette.Group.Actions", SettingsGroup = "Palette.Group.Settings", Elements = "Palette.Group.Elements";

    public const string Screenshot = "act:screenshot", Pick = "act:pick", Refresh = "act:refresh", ToggleTree = "act:toggleTree", RecordNetwork = "act:network.toggle", ClearNetwork = "act:network.clear";
    public static string Page(string page) => "nav:" + page;
    public static string Theme(AppearanceMode m) => "set:theme:" + m;
    public static string LanguageId(string setting) => "set:lang:" + setting;
    public static string Element(int id) => "el:" + id;

    public static IReadOnlyList<PaletteEntry> Build(bool hasSession, IEnumerable<NamedElement> elements, ShortcutMap map, bool mac, Localizer loc)
    {
        var list = new List<PaletteEntry>();
        string Hint(string actionId) => map.Get(actionId) is { } g ? g.ToDisplay(mac) : "";
        PaletteEntry E(string id, string group, string labelKey, string? shortcut = null, string? descKey = null, string keywords = "") =>
            new(id, loc.Get(group), loc.Get(labelKey), descKey is null ? null : loc.Get(descKey), string.IsNullOrEmpty(shortcut) ? null : shortcut, keywords);

        foreach (var page in ShortcutRegistry.NavPages)
        {
            var pageNeedsApp = page is not ("connections" or "settings");
            if (pageNeedsApp && !hasSession) continue;   // those pages show nothing without an app
            list.Add(E(Page(page), Navigation, "Palette.Go." + page, Hint(ShortcutRegistry.NavId(page)), keywords: page));
        }

        if (hasSession)
        {
            list.Add(E(Pick, Actions, "Palette.Act.Pick", keywords: "inspect select cursor"));
            list.Add(E(Screenshot, Actions, "Palette.Act.Screenshot", keywords: "png image capture"));
            list.Add(E(Refresh, Actions, "Palette.Act.Refresh", keywords: "reload tree"));
            list.Add(E(ToggleTree, Actions, "Palette.Act.ToggleTree", keywords: "visual logical"));
            list.Add(E(RecordNetwork, Actions, "Palette.Act.RecordNetwork", Hint(ShortcutRegistry.NetworkToggleRecord), keywords: "http request"));
            list.Add(E(ClearNetwork, Actions, "Palette.Act.ClearNetwork", keywords: "http request"));
        }

        foreach (var mode in Enum.GetValues<AppearanceMode>())
            list.Add(E(Theme(mode), SettingsGroup, "Palette.Theme." + mode, keywords: "appearance dark light"));
        foreach (var lang in Languages.SettingValues)
            list.Add(E(LanguageId(lang), SettingsGroup, "Palette.Lang." + lang, keywords: "language 语言 語言"));

        if (hasSession)
        {
            var heading = loc.Get(Elements);
            foreach (var e in elements.Take(MaxElements))
                list.Add(new PaletteEntry(Element(e.Id), heading, $"{e.Type} #{e.Name}", null, null, e.Name));
        }
        return list;
    }
}
