using System.Diagnostics;
using Spyglass.App.Core.Localization;
using Spyglass.Protocol.Messages;

namespace Spyglass.App.Core.Settings;

/// <summary>One way to start an editor: program plus arguments (never a shell string, so file names cannot inject commands).</summary>
public sealed record EditorCommand(string Program, IReadOnlyList<string> Arguments);

/// <summary>
/// "Open in editor" for XAML locations (design/ux: Settings › Editor). Pure command building plus a launcher that
/// tries candidates in order (e.g. the `code` CLI, then macOS `open -b`).
/// </summary>
public static class EditorLauncher
{
    /// <summary>Local file path of a source location, or null when it is not a file (avares://, resource URIs).</summary>
    public static string? FilePath(SourceInfo source)
    {
        if (!Uri.TryCreate(source.Uri, UriKind.Absolute, out var uri)) return Path.IsPathRooted(source.Uri) ? source.Uri : null;
        return uri.IsFile ? uri.LocalPath : null;
    }

    /// <summary>Candidates in preference order. <paramref name="os"/>: "macos", "windows" or "linux".</summary>
    public static IReadOnlyList<EditorCommand> Build(EditorKind kind, string customTemplate, string file, int line, int column, string os)
    {
        line = Math.Max(1, line);
        column = Math.Max(1, column);
        switch (kind)
        {
            case EditorKind.VsCode:
                var goto_ = $"{file}:{line}:{column}";
                var list = new List<EditorCommand> { new("code", ["--goto", goto_]) };
                if (os == "macos") list.Add(new("open", ["-b", "com.microsoft.VSCode", "--args", "--goto", goto_]));
                if (os == "windows") list.Add(new("code.cmd", ["--goto", goto_]));
                return list;
            case EditorKind.Rider:
                var rider = new List<EditorCommand> { new("rider", ["--line", line.ToString(), "--column", column.ToString(), file]) };
                if (os == "macos") rider.Add(new("open", ["-b", "com.jetbrains.rider", "--args", "--line", line.ToString(), "--column", column.ToString(), file]));
                if (os == "windows") rider.Add(new("rider64.exe", ["--line", line.ToString(), "--column", column.ToString(), file]));
                return rider;
            case EditorKind.VisualStudio:
                // devenv cannot open at a line from the command line; /Edit reuses a running instance.
                return os == "windows" ? [new("devenv", ["/Edit", file])] : [];
            case EditorKind.System:
                return os switch
                {
                    "macos" => [new("open", [file])],
                    "windows" => [new("cmd", ["/c", "start", "", file])],
                    _ => [new("xdg-open", [file])],
                };
            default:
                var tokens = Tokenize(customTemplate)
                    .Select(t => t.Replace("{file}", file).Replace("{line}", line.ToString()).Replace("{column}", column.ToString())).ToList();
                return tokens.Count == 0 ? [] : [new(tokens[0], tokens.Skip(1).ToList())];
        }
    }

    /// <summary>Splits a command template on spaces; double quotes group an argument. Placeholders are replaced after splitting.</summary>
    public static List<string> Tokenize(string template)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var started = false;
        foreach (var ch in template)
        {
            if (ch == '"') { quoted = !quoted; started = true; continue; }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (started) { result.Add(current.ToString()); current.Clear(); started = false; }
                continue;
            }
            current.Append(ch);
            started = true;
        }
        if (started) result.Add(current.ToString());
        return result;
    }

    public static string CurrentOs => OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux";

    /// <returns>null on success, otherwise a message the UI can show.</returns>
    public static string? Open(SourceInfo source, AppSettings settings, Func<EditorCommand, bool>? start = null)
    {
        if (FilePath(source) is not { } file)
            return Localizer.Shared.Format("Editor.NotAFile", source.Uri);
        if (!File.Exists(file) && start is null)
            return Localizer.Shared.Format("Editor.FileNotFound", file);
        var candidates = Build(settings.Editor, settings.CustomEditorCommand, file, source.Line, Math.Max(1, source.Column), CurrentOs);
        if (candidates.Count == 0)
            return settings.Editor switch
            {
                EditorKind.Custom => Localizer.Shared.Get("Editor.NoCommand"),
                EditorKind.VisualStudio => Localizer.Shared.Get("Editor.VsWindowsOnly"),
                _ => Localizer.Shared.Get("Editor.NoneConfigured"),
            };
        start ??= TryStart;
        foreach (var c in candidates)
            if (start(c)) return null;
        return Localizer.Shared.Format("Editor.CouldNotStart", candidates[0].Program);
    }

    static bool TryStart(EditorCommand c)
    {
        try
        {
            var psi = new ProcessStartInfo(c.Program) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in c.Arguments) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return p is not null;
        }
        catch (Exception) { return false; }   // not installed / not on PATH: try the next candidate
    }
}
