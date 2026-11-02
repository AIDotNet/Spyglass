using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spyglass.App.Core.Network;

/// <summary>Persisted Network panel preferences (Chrome keeps these in DevTools settings): visible columns, custom header columns, overview, HAR option.</summary>
public sealed class NetworkSettings
{
    public static readonly NetworkColumn[] DefaultColumns =
    [
        NetworkColumn.Method, NetworkColumn.Status, NetworkColumn.Type, NetworkColumn.Initiator,
        NetworkColumn.Size, NetworkColumn.Time, NetworkColumn.Waterfall,
    ];

    public List<string> Columns { get; set; } = DefaultColumns.Select(c => c.ToString()).ToList();
    public List<string> CustomHeaders { get; set; } = [];
    public bool ShowOverview { get; set; }
    /// <summary>"Allow to generate HAR with sensitive data" (Settings › Preferences › Network).</summary>
    public bool AllowSensitiveHar { get; set; }

    [JsonIgnore]
    public HashSet<NetworkColumn> VisibleColumns =>
        Columns.Select(c => Enum.TryParse<NetworkColumn>(c, out var v) ? (NetworkColumn?)v : null).OfType<NetworkColumn>().Append(NetworkColumn.Name).ToHashSet();

    public void SetVisible(NetworkColumn c, bool on)
    {
        var set = VisibleColumns;
        if (on) set.Add(c); else set.Remove(c);
        Columns = Enum.GetValues<NetworkColumn>().Where(set.Contains).Select(x => x.ToString()).ToList();
    }
}

/// <summary>JSON file store; the path is injectable so tests use a temp file. Never throws: a missing or corrupt file yields defaults.</summary>
public sealed class NetworkSettingsStore(string? path = null)
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>User app-data file; <c>SPYGLASS_NETWORK_SETTINGS</c> overrides it (screenshot runs, portable setups).</summary>
    public static string DefaultPath => Environment.GetEnvironmentVariable("SPYGLASS_NETWORK_SETTINGS") is { Length: > 0 } env ? env : SpyglassHome.File("network-settings.json");

    public string FilePath { get; } = path ?? DefaultPath;

    public NetworkSettings Load()
    {
        if (FilePath == SpyglassHome.File("network-settings.json")) SpyglassHome.MigrateLegacy(FilePath, "network-settings.json");
        try { return JsonSerializer.Deserialize<NetworkSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save(NetworkSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch { /* preferences are best effort */ }
    }
}
