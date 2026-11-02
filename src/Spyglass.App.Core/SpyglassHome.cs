namespace Spyglass.App.Core;

/// <summary>
/// Where Spyglass keeps its files: <c>~/.spyglass</c> on every platform (<c>SPYGLASS_HOME</c> overrides it).
/// Holds settings.json, network-settings.json and the IDE token file.
/// </summary>
public static class SpyglassHome
{
    public static string Directory =>
        Environment.GetEnvironmentVariable("SPYGLASS_HOME") is { Length: > 0 } h ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".spyglass");

    public static string File(string name) => Path.Combine(Directory, name);

    /// <summary>
    /// Before 0.2 the files lived in the OS app-data folder (macOS ~/Library/Application Support/Spyglass, Windows
    /// %APPDATA%\Spyglass). Their copy is used once when the new location has no file yet; the old file is left alone.
    /// </summary>
    public static string LegacyFile(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spyglass", name);

    /// <summary>Copies the legacy file of that name to <paramref name="path"/> when only the legacy one exists. Never throws.</summary>
    public static void MigrateLegacy(string path, string name) => Migrate(LegacyFile(name), path);

    /// <summary>Copies <paramref name="from"/> to <paramref name="to"/> unless <paramref name="to"/> exists or <paramref name="from"/> is missing. Never throws.</summary>
    public static void Migrate(string from, string to)
    {
        try
        {
            if (System.IO.File.Exists(to) || !System.IO.File.Exists(from)) return;
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(to))!);
            System.IO.File.Copy(from, to);
        }
        catch { /* the new location simply starts with defaults */ }
    }
}
