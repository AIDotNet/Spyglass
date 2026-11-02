using Spyglass.App.Core.Settings;
using Spyglass.App.Localization;
using Spyglass.Protocol;

namespace Spyglass.App.ViewModels;

/// <summary>Settings page (wireframe W7). Every change is applied and saved immediately; nothing needs an "Apply" button.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    readonly SettingsService _service;
    readonly Func<int> _listeningPort;

    public SettingsViewModel(SettingsService service, Func<int> listeningPort)
    {
        _service = service;
        _listeningPort = listeningPort;
        service.Changed += _ => RaiseAll();
        WatchLanguage();
        ResetCommand = new RelayCommand(() => _service.Reset());
    }

    public RelayCommand ResetCommand { get; }

    void RaiseAll() => Raise(string.Empty);

    AppSettings S => _service.Current;

    // ---- appearance ----
    public string AppearanceKey { get => S.Appearance.ToString(); set => Apply(s => s.Appearance = Parse(value, AppearanceMode.System)); }
    public string MotionKey { get => S.Motion.ToString(); set => Apply(s => s.Motion = Parse(value, MotionMode.Full)); }
    /// <summary>Language choices; each shows in its own language ("System" is bilingual so the list never has to change).</summary>
    public IReadOnlyList<string> LanguageOptions { get; } = [SystemLanguageLabel, "English", "简体中文", "繁體中文"];
    const string SystemLanguageLabel = "System · 跟随系统";
    static readonly string[] LanguageValues = ["System", "English", "ChineseSimplified", "ChineseTraditional"];
    public string LanguageLabel
    {
        get => LanguageOptions[Math.Max(0, Array.IndexOf(LanguageValues, S.Language))];
        set { var i = LanguageOptions.ToList().IndexOf(value ?? ""); if (i >= 0) Apply(s => s.Language = LanguageValues[i]); }
    }
    public IReadOnlyList<string> Accents { get; } = Theme.SpyglassTheme.AccentNames;
    public string Accent { get => S.Accent; set { if (!string.IsNullOrEmpty(value)) Apply(s => s.Accent = value); } }

    // ---- connection ----
    public double Port { get => S.Port ?? SpyglassProtocol.DefaultPort; set => Apply(s => s.Port = (int)value == SpyglassProtocol.DefaultPort ? null : (int)value); }
    public string ListeningText => Loc.F("Settings.ListeningOn", Lan?.IsActive == true ? "0.0.0.0" : "127.0.0.1", _listeningPort());
    public bool PortOverriddenByEnvironment => SettingsService.EnvironmentPort is not null;
    /// <summary>Set by MainViewModel; the LAN settings live in <see cref="LanViewModel"/>, shared with the Connections page.</summary>
    public LanViewModel? Lan { get; set; }
    public string PortNote => PortOverriddenByEnvironment
        ? Loc.F("Settings.PortOverridden", SettingsService.EnvironmentPort)
        : _listeningPort() == _service.EffectivePort
            ? Loc.T("Settings.PortNote")
            : Loc.F("Settings.PortSaved", _service.EffectivePort, _listeningPort());

    // ---- elements ----
    public bool ShowTemplateParts { get => S.ShowTemplateParts; set => Apply(s => s.ShowTemplateParts = value); }
    public bool KeepHighlight { get => S.KeepHighlight; set => Apply(s => s.KeepHighlight = value); }

    // ---- events ----
    public double EventBufferLimit { get => S.EventBufferLimit; set => Apply(s => s.EventBufferLimit = (int)value); }

    // ---- network ----
    public bool AutoRecordNetwork { get => S.AutoRecordNetwork; set => Apply(s => s.AutoRecordNetwork = value); }
    public bool CaptureBodies { get => S.CaptureBodies; set => Apply(s => s.CaptureBodies = value); }
    public double BodyLimitKb { get => S.BodyLimitKb; set => Apply(s => s.BodyLimitKb = (int)value); }
    public bool CaptureInitiator { get => S.CaptureInitiator; set => Apply(s => s.CaptureInitiator = value); }

    // ---- editor ----
    public string EditorKey { get => S.Editor.ToString(); set => Apply(s => s.Editor = Parse(value, EditorKind.VsCode)); }
    public bool IsCustomEditor => S.Editor == EditorKind.Custom;
    public string CustomEditorCommand { get => S.CustomEditorCommand; set => Apply(s => s.CustomEditorCommand = value ?? ""); }
    public string EditorHint => S.Editor switch
    {
        EditorKind.VsCode => Loc.T("Settings.HintVsCode"),
        EditorKind.Rider => Loc.T("Settings.HintRider"),
        EditorKind.VisualStudio => OperatingSystem.IsWindows() ? Loc.T("Settings.HintVs") : Loc.T("Settings.HintVsWindowsOnly"),
        EditorKind.System => Loc.T("Settings.HintSystem"),
        _ => Loc.T("Settings.HintCustom"),
    };

    // ---- about ----
    public string Version { get; } = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    public string ProtocolText => Loc.F("Settings.Protocol", SpyglassProtocol.Version);
    public string FilePath => _service.FilePath;
    public string SaveStatus => _service.LastSaveFailed ? Loc.F("Settings.SaveFailed", _service.FilePath) : Loc.T("Settings.SavedAuto");
    public bool SaveFailed => _service.LastSaveFailed;

    /// <summary>Editable client shortcuts (one recorder per registry action).</summary>
    public IReadOnlyList<ShortcutItemViewModel> ClientShortcuts => _clientShortcuts ??= CreateShortcutItems();
    IReadOnlyList<ShortcutItemViewModel>? _clientShortcuts;

    IReadOnlyList<ShortcutItemViewModel> CreateShortcutItems()
    {
        var items = ShortcutRegistry.Actions.Select(a => new ShortcutItemViewModel(a, _service)).ToList();
        _service.Changed += _ => { foreach (var i in items) i.Refresh(); Raise(nameof(HasShortcutConflict)); Raise(nameof(HasCustomShortcuts)); };
        return items;
    }

    public bool HasShortcutConflict => ClientShortcuts.Any(i => i.HasConflict);
    public bool HasCustomShortcuts => S.Shortcuts.Count > 0;
    public RelayCommand ResetShortcutsCommand => _resetShortcuts ??= new RelayCommand(() => _service.Update(s => s.Shortcuts = new()));
    RelayCommand? _resetShortcuts;

    /// <summary>Read-only reference rows: the in-app hotkeys are agent options in code, and text editing keys are fixed.</summary>
    public IReadOnlyList<ShortcutRow> Shortcuts => BuildShortcuts();

    static IReadOnlyList<ShortcutRow> BuildShortcuts()
    {
        var mac = OperatingSystem.IsMacOS();
        return
        [
            new(Loc.T("Settings.AreaInApp"), mac ? "⌘ ⌥ I" : "Ctrl Shift I", Loc.T("Settings.KeyBringForward")),
            new(Loc.T("Settings.AreaInApp"), mac ? "⌘ ⇧ C" : "Ctrl Shift C", Loc.T("Settings.KeyPick")),
            new(Loc.T("Settings.Elements"), "Enter", Loc.T("Settings.KeyCommit")),
            new(Loc.T("Settings.Elements"), "Esc", Loc.T("Settings.KeyRevert")),
        ];
    }

    void Apply(Action<AppSettings> change) => _service.Update(change);

    static T Parse<T>(string value, T fallback) where T : struct, Enum => Enum.TryParse<T>(value, out var v) ? v : fallback;
}

public sealed record ShortcutRow(string Area, string Keys, string Action);
