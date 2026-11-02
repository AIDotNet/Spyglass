using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spyglass.App.Core.Settings;

/// <summary>A client action that can be bound to a key. <see cref="Default"/> uses "Mod" for Cmd (macOS) / Ctrl (elsewhere).</summary>
public sealed record ShortcutAction(string Id, string Area, string Title, string Default);

/// <summary>A key combination in Arc's textual syntax ("Ctrl+Shift+K", "Escape", "Cmd+E"). Pure data; Avalonia types are not needed here.</summary>
public readonly record struct Gesture(bool Ctrl, bool Alt, bool Shift, bool Meta, string Key)
{
    static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = "Space", ["enter"] = "Enter", ["return"] = "Enter", ["escape"] = "Escape", ["esc"] = "Escape",
        ["backspace"] = "Backspace", ["delete"] = "Delete", ["del"] = "Delete", ["tab"] = "Tab",
        ["up"] = "Up", ["down"] = "Down", ["left"] = "Left", ["right"] = "Right",
        ["home"] = "Home", ["end"] = "End", ["pageup"] = "PageUp", ["pagedown"] = "PageDown",
    };
    const string Punctuation = ",./;'[]\\-=`";

    /// <summary>Canonical text, e.g. "Ctrl+Shift+K". Round-trips through <see cref="TryParse"/>.</summary>
    public override string ToString()
    {
        var s = (Ctrl ? "Ctrl+" : "") + (Alt ? "Alt+" : "") + (Shift ? "Shift+" : "") + (Meta ? "Cmd+" : "");
        return s + Key;
    }

    /// <summary>Parses "mod+shift+k", "Ctrl+E", "esc"… ("mod" = Cmd when <paramref name="mac"/>, else Ctrl). False for anything else.</summary>
    public static bool TryParse(string? text, bool mac, out Gesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool ctrl = false, alt = false, shift = false, meta = false;
        string? key = null;
        foreach (var raw in text.Trim().Split('+', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0) return false;
            switch (raw.ToLowerInvariant())
            {
                case "mod": if (mac) meta = true; else ctrl = true; continue;
                case "ctrl" or "control": ctrl = true; continue;
                case "alt" or "option": alt = true; continue;
                case "shift": shift = true; continue;
                case "meta" or "cmd" or "command" or "win" or "super": meta = true; continue;
            }
            if (key is not null || Canonical(raw) is not { } k) return false;
            key = k;
        }
        if (key is null) return false;
        gesture = new Gesture(ctrl, alt, shift, meta, key);
        return true;
    }

    static string? Canonical(string raw)
    {
        if (Named.TryGetValue(raw, out var n)) return n;
        if (raw.Length == 1)
        {
            var c = raw[0];
            if (char.IsAsciiLetter(c)) return char.ToUpperInvariant(c).ToString();
            if (char.IsAsciiDigit(c) || Punctuation.Contains(c)) return raw;
        }
        if ((raw[0] is 'f' or 'F') && raw.Length <= 3 && int.TryParse(raw.AsSpan(1), out var f) && f is >= 1 and <= 24) return "F" + f;
        return null;
    }

    /// <summary>Display text with spaces between caps: "⌘ E" on a Mac, "Ctrl E" elsewhere.</summary>
    public string ToDisplay(bool mac)
    {
        var parts = new List<string>();
        if (mac)
        {
            if (Ctrl) parts.Add("⌃");
            if (Alt) parts.Add("⌥");
            if (Shift) parts.Add("⇧");
            if (Meta) parts.Add("⌘");
        }
        else
        {
            if (Ctrl) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            if (Meta) parts.Add("Win");
        }
        parts.Add(Key switch { "Escape" => "Esc", "Up" => "↑", "Down" => "↓", "Left" => "←", "Right" => "→", "Enter" => mac ? "↩" : "Enter", _ => Key });
        return string.Join(" ", parts);
    }
}

/// <summary>The client's bindable actions and the rules for user overrides stored in <see cref="AppSettings.Shortcuts"/>.</summary>
public static class ShortcutRegistry
{
    public const string NetworkToggleRecord = "network.toggleRecord";
    public const string NetworkSearch = "network.search";
    public const string NetworkClose = "network.close";
    public const string NetworkPrevious = "network.previous";
    public const string NetworkNext = "network.next";

    /// <summary>Registry order is also the order of the Settings list and of dispatch.</summary>
    public static IReadOnlyList<ShortcutAction> Actions { get; } =
    [
        new(NetworkToggleRecord, "Network", "Start / stop recording", "Mod+E"),
        new(NetworkSearch, "Network", "Search headers, payloads and responses", "Mod+F"),
        new(NetworkClose, "Network", "Close the search / request details", "Escape"),
        new(NetworkPrevious, "Network", "Previous request", "Up"),
        new(NetworkNext, "Network", "Next request", "Down"),
    ];

    /// <summary>Stored in the file for "no shortcut".</summary>
    public const string None = "None";

    public static ShortcutAction? Find(string id) => Actions.FirstOrDefault(a => a.Id == id);

    public static Gesture DefaultOf(ShortcutAction a, bool mac) =>
        Gesture.TryParse(a.Default, mac, out var g) ? g : throw new InvalidOperationException($"Bad default shortcut for {a.Id}");

    /// <summary>Drops unknown ids and unparsable values, writes the rest in canonical form, and drops entries equal to the default.</summary>
    public static Dictionary<string, string> Sanitize(IReadOnlyDictionary<string, string>? overrides, bool mac)
    {
        var clean = new Dictionary<string, string>();
        if (overrides is null) return clean;
        foreach (var (id, text) in overrides)
        {
            if (Find(id) is not { } action || text is null) continue;
            if (string.Equals(text.Trim(), None, StringComparison.OrdinalIgnoreCase)) { clean[id] = None; continue; }
            if (!Gesture.TryParse(text, mac, out var g)) continue;
            if (g != DefaultOf(action, mac)) clean[id] = g.ToString();
        }
        return clean;
    }
}

/// <summary>The effective shortcuts for one settings snapshot: defaults plus the (sanitized) overrides, with conflict detection.</summary>
public sealed class ShortcutMap
{
    readonly Dictionary<string, Gesture?> _effective = new();
    readonly bool _mac;

    public ShortcutMap(IReadOnlyDictionary<string, string>? overrides, bool? mac = null)
    {
        _mac = mac ?? OperatingSystem.IsMacOS();
        var clean = ShortcutRegistry.Sanitize(overrides, _mac);
        foreach (var a in ShortcutRegistry.Actions)
        {
            if (clean.TryGetValue(a.Id, out var t))
                _effective[a.Id] = Gesture.TryParse(t, _mac, out var g) ? g : null;   // "None" -> unbound
            else _effective[a.Id] = ShortcutRegistry.DefaultOf(a, _mac);
        }
    }

    public static ShortcutMap From(AppSettings settings, bool? mac = null) => new(settings.Shortcuts, mac);

    public Gesture? Get(string id) => _effective.GetValueOrDefault(id);
    public Gesture Default(string id) => ShortcutRegistry.DefaultOf(ShortcutRegistry.Find(id)!, _mac);
    public bool IsCustom(string id) => Get(id) != Default(id);

    /// <summary>Titles of the other actions bound to the same combination (empty when none).</summary>
    public IReadOnlyList<string> ConflictsOf(string id) =>
        Get(id) is not { } g ? [] :
        ShortcutRegistry.Actions.Where(a => a.Id != id && Get(a.Id) == g).Select(a => a.Title).ToList();

    /// <summary>The first action bound to this combination, so a key press runs exactly one action even when two conflict.</summary>
    public ShortcutAction? Find(Gesture pressed) => ShortcutRegistry.Actions.FirstOrDefault(a => Get(a.Id) == pressed);

    /// <summary>Returns the overrides with <paramref name="id"/> set to <paramref name="gesture"/> (null = unbound); equal to default removes the entry.</summary>
    public static Dictionary<string, string> With(IReadOnlyDictionary<string, string>? overrides, string id, Gesture? gesture, bool? mac = null)
    {
        var m = mac ?? OperatingSystem.IsMacOS();
        var next = ShortcutRegistry.Sanitize(overrides, m);
        if (ShortcutRegistry.Find(id) is not { } a) return next;
        if (gesture is null) next[id] = ShortcutRegistry.None;
        else if (gesture == ShortcutRegistry.DefaultOf(a, m)) next.Remove(id);
        else next[id] = gesture.Value.ToString();
        return next;
    }
}

/// <summary>Reads the Shortcuts object leniently: non-object JSON or non-string values are skipped instead of failing the whole file.</summary>
public sealed class TolerantStringMapConverter : JsonConverter<Dictionary<string, string>>
{
    public override Dictionary<string, string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var map = new Dictionary<string, string>();
        using var doc = JsonDocument.ParseValue(ref reader);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
        foreach (var p in doc.RootElement.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } s) map[p.Name] = s;
        return map;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, string> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        // Relaxed escaping keeps "Ctrl+E" readable in the hand-editable file (the default encoder writes "+").
        foreach (var (k, v) in value)
        {
            writer.WritePropertyName(JsonEncodedText.Encode(k, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping));
            writer.WriteStringValue(JsonEncodedText.Encode(v, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping));
        }
        writer.WriteEndObject();
    }
}
