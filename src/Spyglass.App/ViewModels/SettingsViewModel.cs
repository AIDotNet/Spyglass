using Spyglass.App.Core.Settings;
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
        ResetCommand = new RelayCommand(() => _service.Reset());
    }

    public RelayCommand ResetCommand { get; }

    void RaiseAll() => Raise(string.Empty);

    AppSettings S => _service.Current;

    // ---- appearance ----
    public string AppearanceKey { get => S.Appearance.ToString(); set => Apply(s => s.Appearance = Parse(value, AppearanceMode.System)); }
    public string MotionKey { get => S.Motion.ToString(); set => Apply(s => s.Motion = Parse(value, MotionMode.Full)); }
    public IReadOnlyList<string> Accents { get; } = Theme.SpyglassTheme.AccentNames;
    public string Accent { get => S.Accent; set { if (!string.IsNullOrEmpty(value)) Apply(s => s.Accent = value); } }

    // ---- connection ----
    public double Port { get => S.Port ?? SpyglassProtocol.DefaultPort; set => Apply(s => s.Port = (int)value == SpyglassProtocol.DefaultPort ? null : (int)value); }
    public string ListeningText => $"Listening on 127.0.0.1:{_listeningPort()}";
    public bool PortOverriddenByEnvironment => SettingsService.EnvironmentPort is not null;
    public string PortNote => PortOverriddenByEnvironment
        ? $"SPYGLASS_PORT={SettingsService.EnvironmentPort} overrides this setting."
        : _listeningPort() == _service.EffectivePort
            ? "Debugged apps connect to this port. Changing it takes effect the next time Spyglass starts; apps must use the same port (UseSpyglass(o => o.Port = …) or SPYGLASS_PORT)."
            : $"Saved: apps connect to {_service.EffectivePort} after the next start of Spyglass (currently listening on {_listeningPort()}).";

    // ---- elements ----
    public bool ShowTemplateParts { get => S.ShowTemplateParts; set => Apply(s => s.ShowTemplateParts = value); }
    public bool KeepHighlight { get => S.KeepHighlight; set => Apply(s => s.KeepHighlight = value); }

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
        EditorKind.VsCode => "Runs  code --goto file:line:column  (on macOS also tries `open -b com.microsoft.VSCode`).",
        EditorKind.Rider => "Runs  rider --line N --column N file  (on macOS also tries `open -b com.jetbrains.rider`).",
        EditorKind.VisualStudio => OperatingSystem.IsWindows() ? "Runs  devenv /Edit file  (opens the file, not the line)." : "Visual Studio can only be started from Windows.",
        EditorKind.System => "Opens the file with the system's default application (no line number).",
        _ => "Placeholders: {file}  {line}  {column}. Use double quotes around arguments with spaces.",
    };

    // ---- about ----
    public string Version { get; } = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    public string ProtocolText { get; } = $"Protocol {SpyglassProtocol.Version}";
    public string FilePath => _service.FilePath;
    public string SaveStatus => _service.LastSaveFailed ? $"Could not write {_service.FilePath}: settings apply now but will not be remembered." : "Changes are saved automatically.";
    public bool SaveFailed => _service.LastSaveFailed;

    /// <summary>In-app and client shortcuts (cheat sheet; the in-app ones are agent options in code).</summary>
    public IReadOnlyList<ShortcutRow> Shortcuts { get; } = BuildShortcuts();

    static IReadOnlyList<ShortcutRow> BuildShortcuts()
    {
        var mac = OperatingSystem.IsMacOS();
        string Cmd(string key) => mac ? $"⌘ {key}" : $"Ctrl {key}";
        return
        [
            new("In your app", mac ? "⌘ ⌥ I" : "Ctrl Shift I", "Bring Spyglass forward (starts it if it is not running)"),
            new("In your app", mac ? "⌘ ⇧ C" : "Ctrl Shift C", "Pick an element; Spyglass selects it. Esc cancels"),
            new("Network", Cmd("E"), "Start / stop recording"),
            new("Network", Cmd("F"), "Search headers, payloads and responses"),
            new("Network", "Esc", "Close the request details"),
            new("Network", "↑  ↓", "Move through requests"),
            new("Elements", "Enter", "Commit an edited property value"),
            new("Elements", "Esc", "Revert the value being edited"),
        ];
    }

    void Apply(Action<AppSettings> change) => _service.Update(change);

    static T Parse<T>(string value, T fallback) where T : struct, Enum => Enum.TryParse<T>(value, out var v) ? v : fallback;
}

public sealed record ShortcutRow(string Area, string Keys, string Action);
