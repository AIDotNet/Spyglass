using Spyglass.App.Core;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>Files live in ~/.spyglass; settings from the old OS app-data folder are carried over once.</summary>
public class SpyglassHomeTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("spyglass-home-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Files_are_resolved_inside_the_home_directory()
    {
        Assert.Equal(Path.Combine(SpyglassHome.Directory, "settings.json"), SpyglassHome.File("settings.json"));
        if (Environment.GetEnvironmentVariable("SPYGLASS_HOME") is not { Length: > 0 })
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".spyglass"), SpyglassHome.Directory);
    }

    [Fact]
    public void Legacy_file_is_copied_once_and_never_overwrites()
    {
        var old = Path.Combine(_dir, "old", "settings.json");
        var now = Path.Combine(_dir, "home", "sub", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, "{ \"Accent\": \"Rose\" }");

        SpyglassHome.Migrate(old, now);
        Assert.Equal("{ \"Accent\": \"Rose\" }", File.ReadAllText(now));
        Assert.True(File.Exists(old));   // the old file stays

        File.WriteAllText(now, "{ \"Accent\": \"Teal\" }");
        SpyglassHome.Migrate(old, now);
        Assert.Equal("{ \"Accent\": \"Teal\" }", File.ReadAllText(now));   // newer file wins
    }

    [Fact]
    public void Missing_legacy_file_or_unwritable_target_is_not_an_error()
    {
        SpyglassHome.Migrate(Path.Combine(_dir, "nope.json"), Path.Combine(_dir, "x", "a.json"));
        Assert.False(File.Exists(Path.Combine(_dir, "x", "a.json")));

        var old = Path.Combine(_dir, "o.json");
        File.WriteAllText(old, "{}");
        var blocker = Path.Combine(_dir, "file");
        File.WriteAllText(blocker, "x");
        SpyglassHome.Migrate(old, Path.Combine(blocker, "a.json"));   // parent is a file: cannot be created
    }
}
