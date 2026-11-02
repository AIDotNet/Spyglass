using System.Text.Json;
using System.Text.Json.Serialization;
using Spyglass.Protocol;

namespace Spyglass.App.Core.Settings;

public enum AppearanceMode { System, Light, Dark }
public enum MotionMode { Full, Reduced, Off }
public enum EditorKind { VsCode, Rider, VisualStudio, System, Custom }

/// <summary>Persisted client preferences (design/ux/02-wireframes.md W7). Plain data: Arc/Avalonia types stay out of Core.</summary>
public sealed class AppSettings
{
    // ---- appearance ----
    [JsonConverter(typeof(JsonStringEnumConverter))] public AppearanceMode Appearance { get; set; } = AppearanceMode.System;
    /// <summary>Arc accent preset name: Neutral, Violet, Blue, Green, Amber, Orange, Coral, Rose.</summary>
    public string Accent { get; set; } = "Blue";
    [JsonConverter(typeof(JsonStringEnumConverter))] public MotionMode Motion { get; set; } = MotionMode.Full;

    // ---- connection ----
    /// <summary>Port debugged apps connect to. Null = default (29500). SPYGLASS_PORT overrides; takes effect at next start.</summary>
    public int? Port { get; set; }

    // ---- elements ----
    public bool ShowTemplateParts { get; set; }
    /// <summary>Keep the highlight box in the app until another element is selected (default: it fades after 1.5 s).</summary>
    public bool KeepHighlight { get; set; }

    // ---- network ----
    /// <summary>Start recording when the Network page is first opened (Chrome records while DevTools is open).</summary>
    public bool AutoRecordNetwork { get; set; } = true;
    public bool CaptureBodies { get; set; } = true;
    public int BodyLimitKb { get; set; } = 1024;
    public bool CaptureInitiator { get; set; } = true;

    // ---- editor ----
    [JsonConverter(typeof(JsonStringEnumConverter))] public EditorKind Editor { get; set; } = EditorKind.VsCode;
    /// <summary>Command template for <see cref="EditorKind.Custom"/>: <c>{file}</c>, <c>{line}</c>, <c>{column}</c>; quotes group arguments.</summary>
    public string CustomEditorCommand { get; set; } = "";

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this)) ?? new();

    /// <summary>Clamps hand-edited values to what the app can handle.</summary>
    public AppSettings Normalize()
    {
        if (Port is { } p && (p < 1 || p > 65535)) Port = null;
        BodyLimitKb = Math.Clamp(BodyLimitKb, 1, 10 * 1024);
        if (string.IsNullOrWhiteSpace(Accent)) Accent = "Blue";
        return this;
    }
}

/// <summary>JSON file store; the path is injectable (tests, SPYGLASS_SETTINGS). Never throws: a missing or corrupt file yields defaults.</summary>
public sealed class AppSettingsStore(string? path = null)
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath => Environment.GetEnvironmentVariable("SPYGLASS_SETTINGS") is { Length: > 0 } env ? env : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spyglass", "settings.json");

    public string FilePath { get; } = path ?? DefaultPath;

    public AppSettings Load()
    {
        try { return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new()).Normalize(); }
        catch { return new(); }
    }

    /// <returns>false when the file could not be written (read-only profile…); the in-memory settings still apply.</returns>
    public bool Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
            return true;
        }
        catch { return false; }
    }
}

/// <summary>The live settings shared by view models. <see cref="Update"/> applies a change, saves, and raises <see cref="Changed"/>.</summary>
public sealed class SettingsService(AppSettingsStore? store = null)
{
    readonly AppSettingsStore _store = store ?? new AppSettingsStore();
    AppSettings? _current;

    /// <summary>The process-wide instance used by the app; tests create their own with a temp store.</summary>
    public static SettingsService Shared { get; set; } = new();

    public AppSettings Current => _current ??= _store.Load();
    public string FilePath => _store.FilePath;
    public bool LastSaveFailed { get; private set; }

    public event Action<AppSettings>? Changed;

    public void Update(Action<AppSettings> change)
    {
        var next = Current.Clone();
        change(next);
        next.Normalize();
        _current = next;
        LastSaveFailed = !_store.Save(next);
        Changed?.Invoke(next);
    }

    public void Reset() => Update(s =>
    {
        var d = new AppSettings();
        s.Appearance = d.Appearance; s.Accent = d.Accent; s.Motion = d.Motion; s.Port = d.Port;
        s.ShowTemplateParts = d.ShowTemplateParts; s.KeepHighlight = d.KeepHighlight;
        s.AutoRecordNetwork = d.AutoRecordNetwork; s.CaptureBodies = d.CaptureBodies; s.BodyLimitKb = d.BodyLimitKb; s.CaptureInitiator = d.CaptureInitiator;
        s.Editor = d.Editor; s.CustomEditorCommand = d.CustomEditorCommand;
    });

    /// <summary>The port in effect: SPYGLASS_PORT, then the saved setting, then 29500.</summary>
    public int EffectivePort => EnvironmentPort ?? Current.Port ?? SpyglassProtocol.DefaultPort;

    public static int? EnvironmentPort =>
        int.TryParse(Environment.GetEnvironmentVariable("SPYGLASS_PORT"), out var p) && p is > 0 and < 65536 ? p : null;
}
