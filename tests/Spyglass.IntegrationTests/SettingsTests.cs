using Spyglass.App.Core.Settings;
using Spyglass.Protocol.Messages;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>Settings model, persistence and "open in editor" command building (pure logic, no UI).</summary>
public class SettingsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("spyglass-settings-").FullName;
    string FilePath => Path.Combine(_dir, "nested", "settings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Defaults_when_the_file_is_missing_or_corrupt()
    {
        var s = new AppSettingsStore(FilePath).Load();
        Assert.Equal(AppearanceMode.System, s.Appearance);
        Assert.True(s.AutoRecordNetwork);
        Assert.Equal(EditorKind.VsCode, s.Editor);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{ this is not json");
        Assert.Equal("Blue", new AppSettingsStore(FilePath).Load().Accent);
    }

    [Fact]
    public void Round_trip_creates_the_folder_and_keeps_every_value()
    {
        var store = new AppSettingsStore(FilePath);
        Assert.True(store.Save(new AppSettings
        {
            Appearance = AppearanceMode.Dark, Accent = "Violet", Motion = MotionMode.Reduced, Port = 29777,
            ShowTemplateParts = true, KeepHighlight = true, AutoRecordNetwork = false, CaptureBodies = false,
            BodyLimitKb = 256, CaptureInitiator = false, Editor = EditorKind.Custom, CustomEditorCommand = "edit {file}",
        }));
        var s = new AppSettingsStore(FilePath).Load();
        Assert.Equal((AppearanceMode.Dark, "Violet", MotionMode.Reduced, 29777), (s.Appearance, s.Accent, s.Motion, s.Port));
        Assert.True(s.ShowTemplateParts && s.KeepHighlight);
        Assert.False(s.AutoRecordNetwork || s.CaptureBodies || s.CaptureInitiator);
        Assert.Equal(256, s.BodyLimitKb);
        Assert.Equal((EditorKind.Custom, "edit {file}"), (s.Editor, s.CustomEditorCommand));
        Assert.Contains("\"Dark\"", File.ReadAllText(FilePath));   // enums are written as names, readable and hand-editable
    }

    [Fact]
    public void Hand_edited_nonsense_is_clamped()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """{ "Port": 99999, "BodyLimitKb": 99999999, "Accent": "" }""");
        var s = new AppSettingsStore(FilePath).Load();
        Assert.Null(s.Port);
        Assert.Equal(10 * 1024, s.BodyLimitKb);
        Assert.Equal("Blue", s.Accent);
    }

    [Fact]
    public void Service_updates_saves_and_notifies_without_mutating_old_snapshots()
    {
        var service = new SettingsService(new AppSettingsStore(FilePath));
        var before = service.Current;
        AppSettings? notified = null;
        service.Changed += s => notified = s;

        service.Update(s => s.Accent = "Rose");
        Assert.Equal("Rose", service.Current.Accent);
        Assert.Equal("Blue", before.Accent);                              // old snapshot untouched
        Assert.Same(service.Current, notified);
        Assert.Equal("Rose", new AppSettingsStore(FilePath).Load().Accent);   // persisted

        service.Reset();
        Assert.Equal("Blue", service.Current.Accent);
    }

    [Fact]
    public void Save_failure_is_reported_but_settings_still_apply()
    {
        var blocked = Path.Combine(_dir, "file");
        File.WriteAllText(blocked, "x");
        var service = new SettingsService(new AppSettingsStore(Path.Combine(blocked, "settings.json")));   // parent is a file
        service.Update(s => s.KeepHighlight = true);
        Assert.True(service.LastSaveFailed);
        Assert.True(service.Current.KeepHighlight);
    }

    [Fact]
    public void Port_precedence_is_env_then_setting_then_default()
    {
        var old = Environment.GetEnvironmentVariable("SPYGLASS_PORT");
        try
        {
            var service = new SettingsService(new AppSettingsStore(FilePath));
            Environment.SetEnvironmentVariable("SPYGLASS_PORT", null);
            Assert.Equal(29500, service.EffectivePort);
            service.Update(s => s.Port = 31000);
            Assert.Equal(31000, service.EffectivePort);
            Environment.SetEnvironmentVariable("SPYGLASS_PORT", "32000");
            Assert.Equal(32000, service.EffectivePort);
        }
        finally { Environment.SetEnvironmentVariable("SPYGLASS_PORT", old); }
    }

    // ---------------------------------------------------------------- editor

    [Theory]
    [InlineData("file:///Users/me/App/MainView.axaml", "/Users/me/App/MainView.axaml")]
    [InlineData("file:///C:/src/App/MainView.axaml", "C:\\src\\App\\MainView.axaml")]
    [InlineData("/already/a/path.axaml", "/already/a/path.axaml")]
    [InlineData("avares://App/MainView.axaml", null)]
    public void Source_uris_become_file_paths_only_for_real_files(string uri, string? expected)
    {
        if (expected?.Contains('\\') == true && !OperatingSystem.IsWindows()) return;   // C:\ paths only convert on Windows
        Assert.Equal(expected, EditorLauncher.FilePath(new SourceInfo(uri, 1, 1)));
    }

    [Fact]
    public void Preset_commands_per_platform()
    {
        var vs = EditorLauncher.Build(EditorKind.VsCode, "", "/p/a b.axaml", 12, 5, "macos");
        Assert.Equal("code", vs[0].Program);
        Assert.Equal(["--goto", "/p/a b.axaml:12:5"], vs[0].Arguments);
        Assert.Equal("open", vs[1].Program);                                 // macOS fallback when the `code` CLI is not on PATH
        Assert.Contains("com.microsoft.VSCode", vs[1].Arguments);

        var rider = EditorLauncher.Build(EditorKind.Rider, "", "/p/a.axaml", 3, 1, "linux");
        Assert.Equal(["--line", "3", "--column", "1", "/p/a.axaml"], rider[0].Arguments);

        Assert.Empty(EditorLauncher.Build(EditorKind.VisualStudio, "", "/p/a.axaml", 1, 1, "macos"));
        Assert.Equal("devenv", EditorLauncher.Build(EditorKind.VisualStudio, "", "C:\\a.axaml", 1, 1, "windows")[0].Program);
        Assert.Equal("xdg-open", EditorLauncher.Build(EditorKind.System, "", "/p/a.axaml", 1, 1, "linux")[0].Program);

        // line/column below 1 (unknown positions) are clamped
        Assert.Equal("/p/a.axaml:1:1", EditorLauncher.Build(EditorKind.VsCode, "", "/p/a.axaml", 0, 0, "linux")[0].Arguments[1]);
    }

    [Fact]
    public void Custom_template_is_tokenized_before_substitution_so_paths_with_spaces_stay_one_argument()
    {
        var c = Assert.Single(EditorLauncher.Build(EditorKind.Custom, "\"/Applications/My Editor/bin/ed\" --at {file}:{line}:{column} -n", "/p/my file.axaml", 7, 2, "macos"));
        Assert.Equal("/Applications/My Editor/bin/ed", c.Program);
        Assert.Equal(["--at", "/p/my file.axaml:7:2", "-n"], c.Arguments);
        Assert.Empty(EditorLauncher.Build(EditorKind.Custom, "   ", "/p/a.axaml", 1, 1, "linux"));
        // shell metacharacters in a file name are just data: arguments are never joined into a shell string
        var evil = Assert.Single(EditorLauncher.Build(EditorKind.Custom, "ed {file}", "/p/a; rm -rf ~.axaml", 1, 1, "linux"));
        Assert.Equal(["/p/a; rm -rf ~.axaml"], evil.Arguments);
    }

    [Fact]
    public void Open_tries_candidates_in_order_and_explains_failures()
    {
        var file = Path.Combine(_dir, "V.axaml");
        File.WriteAllText(file, "<x/>");
        var src = new SourceInfo(new Uri(file).AbsoluteUri, 4, 2);
        var tried = new List<string>();
        var vscode = new AppSettings { Editor = EditorKind.VsCode };

        // success stops at the first command that starts (the system opener is a single candidate on every OS)
        Assert.Null(EditorLauncher.Open(src, new AppSettings { Editor = EditorKind.System }, c => { tried.Add(c.Program); return true; }));
        Assert.Single(tried);
        Assert.Contains(tried[0], new[] { "open", "cmd", "xdg-open" });

        var nothing = EditorLauncher.Open(src, vscode, _ => false);
        Assert.Contains("Could not start code", nothing);
        Assert.Contains("Settings", nothing);

        Assert.Contains("not a file on disk", EditorLauncher.Open(new SourceInfo("avares://App/V.axaml", 1, 1), vscode));
        Assert.Contains("No editor command is set", EditorLauncher.Open(src, new AppSettings { Editor = EditorKind.Custom }, _ => true));
        Assert.Contains("File not found", EditorLauncher.Open(new SourceInfo(new Uri(Path.Combine(_dir, "missing.axaml")).AbsoluteUri, 1, 1), vscode));
    }
}
