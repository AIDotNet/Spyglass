using Spyglass.App.Core.Localization;
using Spyglass.App.Core.Palette;
using Spyglass.App.Core.Settings;

namespace Spyglass.App.ViewModels;

/// <summary>Builds the command palette's entries for the current state and runs the chosen one (wireframe W6).</summary>
public sealed class PaletteController(MainViewModel main, SettingsService settings)
{
    public IReadOnlyList<PaletteEntry> Build()
    {
        var session = main.Current;
        var named = session is null ? [] : Named(session.Elements.Roots);
        return PaletteCatalog.Build(session is not null, named, ShortcutBridge.Current, OperatingSystem.IsMacOS(), Localizer.Shared);
    }

    /// <summary>Elements that have a Name, in tree order (the cap is applied by the catalog).</summary>
    static IEnumerable<NamedElement> Named(IEnumerable<ElementNodeViewModel> nodes)
    {
        foreach (var n in nodes)
        {
            if (n.NameText.Length > 0 && !n.IsTemplatePart) yield return new NamedElement(n.Id, n.Type.Split(' ')[0], n.NameText[1..]);
            foreach (var c in Named(n.Children)) yield return c;
        }
    }

    /// <summary>Runs an entry by id; unknown ids and commands that need an app when none is connected do nothing.</summary>
    public void Run(string id)
    {
        var session = main.Current;
        var parts = id.Split(':', 3);
        switch (parts)
        {
            case ["nav", var page]:
                if (page is "connections" or "settings" || session is not null) main.Page = page;
                break;
            case ["act", "pick"] when session is not null:
                main.Page = "elements";
                session.Elements.PickCommand.Execute(null);
                break;
            case ["act", "refresh"] when session is not null:
                main.Page = "elements";
                session.Elements.RefreshCommand.Execute(null);
                break;
            case ["act", "toggleTree"] when session is not null:
                main.Page = "elements";
                session.Elements.TreeKind = session.Elements.TreeKind == "logical" ? "visual" : "logical";
                break;
            case ["act", "network.toggle"] when session is not null:
                main.Page = "network";
                session.Network.ToggleRecordCommand.Execute(null);
                break;
            case ["act", "network.clear"] when session is not null:
                session.Network.ClearCommand.Execute(null);
                break;
            case ["set", "theme", var mode] when Enum.TryParse<AppearanceMode>(mode, out var m):
                settings.Update(s => s.Appearance = m);
                break;
            case ["set", "lang", var lang]:
                settings.Update(s => s.Language = lang);
                break;
            case ["el", var element] when session is not null && int.TryParse(element, out var elementId):
                main.Page = "elements";
                session.Elements.SelectById(elementId);
                break;
        }
    }
}
